using System.Security.Cryptography;
using System.Threading.Channels;
using TeleSave.Core;

namespace TeleSave.TdLib;

/// <summary>
/// Checks an api_id / api_hash with Telegram before it replaces the current key: a throwaway TDLib
/// instance in a temporary directory asks for a QR login token. Telegram answers API_ID_INVALID for a
/// bad key; any other answer means the key works. The running client is not touched.
/// </summary>
public static class ApiKeyValidator
{
    public abstract record Outcome
    {
        public sealed record Valid : Outcome;
        public sealed record Invalid : Outcome;
        public sealed record Failed(string Reason) : Outcome;
    }

    public static async Task<Outcome> ValidateAsync(ApiCredentials credentials, TimeSpan? timeout = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "TeleSaveKeyCheck-" + Guid.NewGuid());
        var states = Channel.CreateUnbounded<AuthState>();
        var client = new TdLibTelegramClient(
            new TdLibTelegramClient.Configuration(Path.Combine(root, "db"), Path.Combine(root, "files"), null, credentials,
                RandomNumberGenerator.GetBytes(32), "key-check"),
            e => { if (e is TelegramEvent.Auth auth) states.Writer.TryWrite(auth.State); });
        client.Start();
        try
        {
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(25));
            var check = CheckAsync(client, states.Reader, cts.Token);
            var finished = await Task.WhenAny(check, Task.Delay(Timeout.Infinite, cts.Token)).ConfigureAwait(false);
            return finished == check && check.IsCompletedSuccessfully ? check.Result : new Outcome.Failed(L10n.T("Zaman aşımı"));
        }
        finally
        {
            // Close() waits for TDLib; keep it off the caller. Closing also releases a request still pending.
            _ = Task.Run(() =>
            {
                client.Close();
                try { Directory.Delete(root, true); } catch (Exception) { }
            });
        }
    }

    private static async Task<Outcome> CheckAsync(TdLibTelegramClient client, ChannelReader<AuthState> states, CancellationToken token)
    {
        await foreach (var state in states.ReadAllAsync(token).ConfigureAwait(false))
        {
            if (state is not AuthState.WaitPhoneNumber) continue;
            try
            {
                await client.RequestQrCodeAsync().WaitAsync(token).ConfigureAwait(false);
                return new Outcome.Valid();
            }
            catch (TelegramError e) when (e.Message.StartsWith("API_ID", StringComparison.Ordinal))
            {
                return new Outcome.Invalid();
            }
            catch (TelegramError e)
            {
                return new Outcome.Failed(e.Message);
            }
        }
        return new Outcome.Failed(L10n.T("Zaman aşımı"));
    }
}
