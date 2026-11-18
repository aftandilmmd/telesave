using System.Text.Json.Serialization;

namespace TeleSave.Core;

public sealed record ApiCredentials(int ApiId, string ApiHash)
{
    public bool IsValid => ApiId > 0 && ApiHash.Length == 32 && ApiHash.All(Uri.IsHexDigit);
}

public sealed record TelegramUser(long Id, string FirstName, string LastName, string? Username, bool IsPremium, int? PhotoFileId = null)
{
    public string DisplayName => string.Join(" ", new[] { FirstName, LastName }.Where(s => !string.IsNullOrEmpty(s)));

    /// <summary>Telegram per-file upload limit: 2000 MiB, or 4000 MiB with Premium.</summary>
    public long MaxUploadSize => (IsPremium ? 4000L : 2000L) * 1024 * 1024;
}

public enum SendKind { Document, Photo, Video }

public sealed record MediaDimensions(int Width, int Height, int Duration = 0);

public sealed record OutgoingFile(string Path, SendKind Kind, string Caption, MediaDimensions? Dimensions = null);

/// <summary>A message TDLib accepted for sending. <see cref="MessageId"/> is temporary until SendSucceeded.</summary>
public sealed record SentMessage(long MessageId, int? FileId);

public enum ConnectionState { WaitingForNetwork, ConnectingToProxy, Connecting, Updating, Ready }

public sealed class TelegramError : Exception
{
    public int Code { get; }

    public TelegramError(int code, string message) : base(message) => Code = code;

    public abstract record FailureKind
    {
        public sealed record Flood(int Seconds) : FailureKind;
        public sealed record Transient : FailureKind;
        public sealed record Permanent : FailureKind;
    }

    public FailureKind Kind
    {
        get
        {
            if (Code == 429 || Message.StartsWith("FLOOD_WAIT", StringComparison.Ordinal) || Message.Contains("retry after", StringComparison.Ordinal))
                return new FailureKind.Flood(FloodSeconds(Message) ?? 30);
            if (Code == 0 || Code >= 500) return new FailureKind.Transient();
            var upper = Message.ToUpperInvariant();
            if (upper.Contains("TIMEOUT") || upper.Contains("NETWORK") || upper.Contains("CONNECTION")) return new FailureKind.Transient();
            return new FailureKind.Permanent();
        }
    }

    internal static int? FloodSeconds(string message)
    {
        // "Too Many Requests: retry after 17" or "FLOOD_WAIT_17"
        var digits = new string(message.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return digits.Length == 0 ? null : int.Parse(digits);
    }
}

public abstract record TelegramEvent
{
    public sealed record Auth(AuthState State) : TelegramEvent;
    public sealed record Connection(ConnectionState State) : TelegramEvent;
    public sealed record FileProgress(int FileId, long Uploaded, long Total) : TelegramEvent;
    public sealed record SendSucceeded(long OldMessageId, long NewMessageId) : TelegramEvent;
    public sealed record SendFailed(long OldMessageId, TelegramError Error) : TelegramEvent;
}

/// <summary>Where an upload goes. <c>ChatId == null</c> means the user's own Saved Messages.</summary>
public sealed record UploadDestination(long? ChatId, int? ForumTopicId, string Title, string? Username = null)
{
    public static readonly UploadDestination SavedMessages = new(null, null, "Saved Messages");

    [JsonIgnore] public bool IsSavedMessages => ChatId is null;

    /// <summary>Saved Messages in the current language, otherwise the chat title.</summary>
    [JsonIgnore] public string DisplayTitle => IsSavedMessages ? L10n.T("Saved Messages") : Title;
}

public enum ChatKind { SavedMessages, User, Bot, Group, Channel }

public sealed record ChatSummary(long Id, string Title, ChatKind Kind, bool IsForum, string? Username);

public sealed record ForumTopicSummary(int Id, string Name, bool IsGeneral, bool IsClosed);

/// <summary>The part of the Telegram client the upload queue needs.</summary>
public interface IUploadTransport
{
    /// <summary>Sends one file, or an album when there are several. Returns one SentMessage per file, in order.</summary>
    Task<IReadOnlyList<SentMessage>> SendAsync(IReadOnlyList<OutgoingFile> files, UploadDestination? destination);

    Task CancelAsync(IReadOnlyList<long> messageIds, UploadDestination? destination);
}

public interface ITelegramClient : IUploadTransport, IDisposable
{
    void Start();
    Task RequestQrCodeAsync();
    Task SubmitPhoneNumberAsync(string phoneNumber);
    Task SubmitCodeAsync(string code);
    Task SubmitPasswordAsync(string password);
    Task LogOutAsync();
    Task<TelegramUser> FetchMeAsync();
    Task<IReadOnlyList<ChatSummary>> LoadChatsAsync(int limit);
    Task<bool> CanSendFilesAsync(long chatId);
    Task<IReadOnlyList<ForumTopicSummary>> ForumTopicsAsync(long chatId);
    Task<string> DownloadFileAsync(int fileId);
    Task<string> DownloadMessageFileAsync(long messageId, UploadDestination? destination);
    Task DeleteMessagesAsync(IReadOnlyList<long> messageIds, UploadDestination? destination);
    Task<ChatFilePage> SearchChatFilesAsync(UploadDestination? destination, string query, ChatFileFilter filter, ChatFileCursor? cursor, int limit);
    /// <summary>Sends a plain text message (at most ClipboardText.MessageLimit UTF-16 units). Returns once TDLib accepted it.</summary>
    Task SendTextAsync(string text, UploadDestination? destination);
    /// <summary>Shuts TDLib down and waits (a few seconds at most).</summary>
    void Close();
}
