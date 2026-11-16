using TeleSave.Core;
using TeleSave.TdLib;

// Verifies the TDLib integration without a Telegram account (same checks as the macOS TeleSaveSmoke):
// a fresh client must accept setTdlibParameters, reach the phone-number step, and get API_ID_INVALID
// from Telegram for a dummy key, both directly and through ApiKeyValidator.
var credentials = args.Length >= 2 ? new ApiCredentials(int.Parse(args[0]), args[1]) : new ApiCredentials(1, new string('0', 32));
var root = Path.Combine(Path.GetTempPath(), "TeleSaveSmoke-" + Guid.NewGuid());
var reached = new TaskCompletionSource();
var client = new TdLibTelegramClient(
    new TdLibTelegramClient.Configuration(Path.Combine(root, "db"), Path.Combine(root, "files"), Path.Combine(root, "tdlib.log"),
        credentials, new byte[32], "smoke"),
    e =>
    {
        Console.WriteLine($"event: {e}");
        if (e is TelegramEvent.Auth { State: AuthState.WaitPhoneNumber }) reached.TrySetResult();
    });
client.Start();
if (await Task.WhenAny(reached.Task, Task.Delay(30_000)) != reached.Task)
{
    Console.WriteLine("FAIL: did not reach waitPhoneNumber within 30s");
    return 1;
}
Console.WriteLine("OK: TDLib initialised and is waiting for a phone number");

string outcome;
try { await client.RequestQrCodeAsync().WaitAsync(TimeSpan.FromSeconds(30)); outcome = "accepted"; }
catch (Exception e) { outcome = "error: " + e.Message; }
Console.WriteLine($"requestQRCode → {outcome}");
client.Close();

var validation = await ApiKeyValidator.ValidateAsync(credentials);
Console.WriteLine($"ApiKeyValidator → {validation}");
try { Directory.Delete(root, true); } catch (Exception) { }

var expected = args.Length >= 2 || (outcome.Contains("API_ID_INVALID") && validation is ApiKeyValidator.Outcome.Invalid);
Console.WriteLine(expected ? "SMOKE PASSED" : "SMOKE FAILED");
return expected ? 0 : 1;
