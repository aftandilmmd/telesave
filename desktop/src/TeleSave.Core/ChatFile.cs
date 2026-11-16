using System.Globalization;
using System.Text.Json.Nodes;

namespace TeleSave.Core;

public enum ChatFileKind { Document, Photo, Video, Audio, Animation, Voice }

/// <summary>A file message in a chat, as shown in the chat file list.</summary>
public sealed record ChatFile(
    long MessageId, int FileId, ChatFileKind Kind, string FileName, long Size, DateTimeOffset Date, string Caption,
    byte[]? Minithumbnail, int? ThumbnailFileId, bool IsOutgoing, string MimeType = "")
{
    public long Id => MessageId;

    /// <summary>A photo, or an image sent as a document (TeleSave's default for original quality).</summary>
    public bool IsImage => Kind == ChatFileKind.Photo || (Kind == ChatFileKind.Document && FileTypes.IsImage(FileName, MimeType));

    /// <summary>A video, or a video sent as a document.</summary>
    public bool IsVideo => Kind == ChatFileKind.Video || (Kind == ChatFileKind.Document && FileTypes.IsVideo(FileName, MimeType));
}

public enum ChatFileFilter { All, Documents, Images, Videos, Audio }

public sealed record ChatFileStream(string TdFilter, Func<ChatFile, bool> Keep);

public static class ChatFileFilterExtensions
{
    /// <summary>
    /// One TDLib search per stream; results are filtered by Keep and merged newest-first.
    /// Images and videos also search documents, because files sent in original quality are documents.
    /// </summary>
    public static IReadOnlyList<ChatFileStream> Streams(this ChatFileFilter filter) => filter switch
    {
        ChatFileFilter.All => [new("searchMessagesFilterEmpty", _ => true)],
        ChatFileFilter.Documents => [new("searchMessagesFilterDocument", f => !f.IsImage && !f.IsVideo)],
        ChatFileFilter.Images => [new("searchMessagesFilterPhoto", _ => true), new("searchMessagesFilterDocument", f => f.IsImage)],
        ChatFileFilter.Videos => [new("searchMessagesFilterVideo", _ => true), new("searchMessagesFilterDocument", f => f.IsVideo)],
        ChatFileFilter.Audio => [new("searchMessagesFilterAudio", _ => true)],
        _ => throw new ArgumentOutOfRangeException(nameof(filter))
    };
}

/// <summary>Where the next page continues: per-stream search position plus items fetched but not yet shown.</summary>
public sealed class ChatFileCursor
{
    internal sealed class StreamState
    {
        public long FromMessageId;
        public List<ChatFile> Buffer = [];
        public bool Exhausted;
    }

    internal List<StreamState> Streams { get; }

    internal ChatFileCursor(List<StreamState> streams) => Streams = streams;
}

public sealed record ChatFilePage(IReadOnlyList<ChatFile> Files, ChatFileCursor? Cursor)
{
    public bool IsLastPage => Cursor is null;
}

/// <summary>Merges one or more TDLib searches into a single newest-first list, page by page.</summary>
public static class ChatFileSearch
{
    /// <summary>Fetches one page of one stream: (parsed files, next from_message_id; 0 = end).</summary>
    public delegate Task<(IReadOnlyList<ChatFile> Files, long Next)> Fetch(string tdFilter, long fromMessageId, int limit);

    /// <summary>Upper bound on server requests per page, so sparse streams cannot loop for long.</summary>
    public const int MaxRequestsPerPage = 8;

    /// <remarks>
    /// The query is matched locally against file name and caption (see <see cref="SearchText"/>):
    /// Telegram's server search does not reliably match file names and is not accent-insensitive.
    /// </remarks>
    public static async Task<ChatFilePage> PageAsync(ChatFileFilter filter, string query, ChatFileCursor? cursor, int limit, Fetch fetch)
    {
        var trimmed = query.Trim();
        var specs = filter.Streams()
            .Select(s => trimmed.Length == 0 ? s : new ChatFileStream(s.TdFilter, f => s.Keep(f) && SearchText.Matches(f.FileName + " " + f.Caption, trimmed)))
            .ToList();
        var state = cursor ?? new ChatFileCursor(specs.Select(_ => new ChatFileCursor.StreamState()).ToList());
        var files = new List<ChatFile>();
        var requests = 0;

        bool Done() => state.Streams.All(s => s.Exhausted && s.Buffer.Count == 0);

        while (files.Count < limit)
        {
            // Refill: a stream that may still have items needs a buffered head before merging,
            // otherwise an older item from another stream could be shown before a newer one from it.
            for (var i = 0; i < state.Streams.Count; i++)
            {
                var stream = state.Streams[i];
                if (stream.Buffer.Count > 0 || stream.Exhausted) continue;
                if (requests >= MaxRequestsPerPage) break;
                requests++;
                var (fetched, next) = await fetch(specs[i].TdFilter, stream.FromMessageId, limit).ConfigureAwait(false);
                stream.Buffer.AddRange(fetched.Where(specs[i].Keep));
                stream.FromMessageId = next;
                stream.Exhausted = next == 0;
            }
            var waiting = state.Streams.Any(s => s.Buffer.Count == 0 && !s.Exhausted);
            if (waiting && requests >= MaxRequestsPerPage) break;
            if (waiting) continue;

            // Merge: newest head first, until a live stream runs dry.
            var emitted = false;
            while (files.Count < limit)
            {
                var candidates = state.Streams.Where(s => s.Buffer.Count > 0).ToList();
                if (candidates.Count == 0) break;
                var best = candidates.MaxBy(s => s.Buffer[0].MessageId)!;
                files.Add(best.Buffer[0]);
                best.Buffer.RemoveAt(0);
                emitted = true;
                if (best.Buffer.Count == 0 && !best.Exhausted) break;
            }
            if (Done()) return new ChatFilePage(files, null);
            if (!emitted && requests >= MaxRequestsPerPage) break;
        }
        return new ChatFilePage(files, Done() ? null : state);
    }
}

/// <summary>Turns a TDLib message object into a ChatFile; null for messages without a file.</summary>
public static class ChatFileParser
{
    public static ChatFile? Parse(JsonObject message)
    {
        if (Int64(message["id"]) is not { } messageId || message["content"] is not JsonObject content) return null;
        var date = DateTimeOffset.FromUnixTimeSeconds(Int64(message["date"]) ?? 0);
        var caption = (content["caption"] as JsonObject)?["text"]?.GetValue<string>() ?? "";
        var isOutgoing = message["is_outgoing"]?.GetValue<bool>() ?? false;

        ChatFileKind kind;
        JsonObject? media;
        JsonObject? file;
        string name;
        JsonObject? thumbnail = null;
        JsonObject? mini = null;

        switch (content["@type"]?.GetValue<string>())
        {
            case "messageDocument":
                kind = ChatFileKind.Document;
                media = content["document"] as JsonObject;
                file = media?["document"] as JsonObject;
                name = Str(media?["file_name"]);
                break;
            case "messagePhoto":
                kind = ChatFileKind.Photo;
                media = content["photo"] as JsonObject;
                var sizes = (media?["sizes"] as JsonArray)?.OfType<JsonObject>().OrderBy(Area).ToList() ?? [];
                file = sizes.LastOrDefault()?["photo"] as JsonObject;
                // Smallest size that is still sharp at list-row size.
                var preview = sizes.FirstOrDefault(s => (Int64(s["width"]) ?? 0) >= 160) ?? sizes.LastOrDefault();
                if (preview?["photo"] is JsonObject previewFile) thumbnail = new JsonObject { ["file"] = previewFile.DeepClone() };
                name = L10n.T("Fotoğraf %@.jpg", FileNameDate(date));
                break;
            case "messageVideo":
                kind = ChatFileKind.Video;
                media = content["video"] as JsonObject;
                file = media?["video"] as JsonObject;
                name = Str(media?["file_name"]);
                if (name.Length == 0) name = L10n.T("Video %@.mp4", FileNameDate(date));
                break;
            case "messageAudio":
                kind = ChatFileKind.Audio;
                media = content["audio"] as JsonObject;
                file = media?["audio"] as JsonObject;
                name = Str(media?["file_name"]);
                thumbnail = media?["album_cover_thumbnail"] as JsonObject;
                mini = media?["album_cover_minithumbnail"] as JsonObject;
                break;
            case "messageAnimation":
                kind = ChatFileKind.Animation;
                media = content["animation"] as JsonObject;
                file = media?["animation"] as JsonObject;
                name = Str(media?["file_name"]);
                if (name.Length == 0) name = L10n.T("GIF %@.mp4", FileNameDate(date));
                break;
            case "messageVoiceNote":
                kind = ChatFileKind.Voice;
                media = content["voice_note"] as JsonObject;
                file = media?["voice"] as JsonObject;
                name = L10n.T("Sesli mesaj %@.ogg", FileNameDate(date));
                break;
            default:
                return null;
        }

        if (file is null || Int64(file["id"]) is not { } fileId) return null;
        thumbnail ??= media?["thumbnail"] as JsonObject;
        mini ??= media?["minithumbnail"] as JsonObject;
        if (name.Length == 0) name = L10n.T("Dosya %@", FileNameDate(date));
        var rawSize = Int64(file["size"]) ?? 0;
        var size = rawSize > 0 ? rawSize : Int64(file["expected_size"]) ?? 0;
        var thumbnailFileId = Int64((thumbnail?["file"] as JsonObject)?["id"]);
        byte[]? miniData = null;
        // TDLib JSON encodes bytes as base64.
        if (mini?["data"]?.GetValue<string>() is { } b64) { try { miniData = Convert.FromBase64String(b64); } catch (FormatException) { } }
        var mime = Str(media?["mime_type"]);
        if (mime.Length == 0 && kind == ChatFileKind.Photo) mime = "image/jpeg";

        return new ChatFile(messageId, (int)fileId, kind, name, size, date, caption, miniData,
            thumbnailFileId is { } t ? (int)t : null, isOutgoing, mime);
    }

    private static string Str(JsonNode? node) => node?.GetValue<string>() ?? "";

    /// <summary>Reads a TDLib number (int53 may come as number or string).</summary>
    public static long? Int64(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<long>(out var l)) return l;
        if (value.TryGetValue<int>(out var i)) return i;
        if (value.TryGetValue<double>(out var d)) return (long)d;
        if (value.TryGetValue<string>(out var str) && long.TryParse(str, out var p)) return p;
        return null;
    }

    private static long Area(JsonObject size) => (Int64(size["width"]) ?? 0) * (Int64(size["height"]) ?? 0);

    private static string FileNameDate(DateTimeOffset date) =>
        date.ToLocalTime().ToString("yyyy-MM-dd HH.mm.ss", CultureInfo.InvariantCulture);
}
