using System.Globalization;

namespace TeleSave.Core;

public enum UploadStatus { Queued, Uploading, Done, Failed, Cancelled }

/// <summary>One file in the upload queue. Immutable: the queue replaces items with <c>with</c> copies.</summary>
public sealed record UploadItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Path { get; init; }
    public required string DisplayName { get; init; }
    public long Size { get; init; }
    /// <summary>Created by TeleSave (zipped folder, pasted image); deleted when no longer needed.</summary>
    public bool IsTemporary { get; init; }
    /// <summary>Items sharing a group id are sent together as one album.</summary>
    public Guid? GroupId { get; init; }
    /// <summary>null = Saved Messages.</summary>
    public UploadDestination? Destination { get; init; }
    public UploadStatus Status { get; init; } = UploadStatus.Queued;
    public double Progress { get; init; }
    /// <summary>Temporary id while uploading; the final message id once done.</summary>
    public long? MessageId { get; init; }
    public string? FailureReason { get; init; }
    public int Attempts { get; init; }
    public DateTimeOffset? NotBefore { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? FinishedAt { get; init; }

    public bool IsTerminal => Status is UploadStatus.Done or UploadStatus.Failed or UploadStatus.Cancelled;

    public double ShownProgress => Status switch
    {
        UploadStatus.Uploading => Progress,
        UploadStatus.Done => 1,
        _ => 0
    };

    public static UploadItem From(PreparedFile file, Guid? groupId, UploadDestination? destination, DateTimeOffset now) => new()
    {
        Path = file.Path,
        DisplayName = file.DisplayName,
        Size = file.Size,
        IsTemporary = file.IsTemporary,
        GroupId = groupId,
        Destination = destination,
        CreatedAt = now
    };
}

public sealed record PreparedFile(string Path, string DisplayName, long Size, bool IsTemporary);

public sealed record RetryPolicy(int MaxAttempts = 5, double BaseDelaySeconds = 2, double MaxDelaySeconds = 60, double FloodScale = 1)
{
    /// <summary>Delay before the next attempt, or null when the item should fail permanently.</summary>
    public TimeSpan? DelayAfter(Exception error, int attempts)
    {
        var kind = (error as TelegramError)?.Kind ?? new TelegramError.FailureKind.Transient();
        switch (kind)
        {
            case TelegramError.FailureKind.Permanent:
                return null;
            case TelegramError.FailureKind.Flood flood:
                // Flood waits do not burn the attempt budget as fast: allow twice as many.
                return attempts < MaxAttempts * 2 ? TimeSpan.FromSeconds(flood.Seconds * FloodScale) : null;
            default:
                if (attempts >= MaxAttempts) return null;
                return TimeSpan.FromSeconds(Math.Min(MaxDelaySeconds, BaseDelaySeconds * Math.Pow(2, Math.Max(0, attempts - 1))));
        }
    }
}

public static class MediaClassifier
{
    /// <summary>Photos above this size are sent as documents.</summary>
    public const long MaxPhotoSize = 10L * 1024 * 1024;

    public static SendKind Kind(string path, long size, bool sendMediaCompressed)
    {
        if (!sendMediaCompressed) return SendKind.Document;
        if (FileTypes.IsTelegramPhoto(path)) return size <= MaxPhotoSize ? SendKind.Photo : SendKind.Document;
        if (FileTypes.IsTelegramVideo(path)) return SendKind.Video;
        return SendKind.Document;
    }
}

public static class AlbumGrouper
{
    public const int MaxAlbumSize = 10;

    /// <summary>
    /// Telegram albums cannot mix documents with photos/videos: files are grouped per family and chunked
    /// to 10. Single-file chunks get no group id.
    /// </summary>
    public static IReadOnlyList<(PreparedFile File, Guid? Group)> AssignGroups(IReadOnlyList<PreparedFile> files, bool sendMediaCompressed)
    {
        var media = files.Where(f => MediaClassifier.Kind(f.Path, f.Size, sendMediaCompressed) != SendKind.Document).ToList();
        var documents = files.Where(f => MediaClassifier.Kind(f.Path, f.Size, sendMediaCompressed) == SendKind.Document).ToList();
        var result = new List<(PreparedFile, Guid?)>();
        foreach (var family in new[] { media, documents })
        {
            foreach (var chunk in family.Chunk(MaxAlbumSize))
            {
                Guid? group = chunk.Length > 1 ? Guid.NewGuid() : null;
                result.AddRange(chunk.Select(f => (f, group)));
            }
        }
        return result;
    }
}

public sealed record CaptionTemplate(string Template)
{
    /// <summary>Supported tokens: {filename}, {date}, {size}.</summary>
    public string Render(string fileName, long size, DateTimeOffset date) =>
        Template.Length == 0 ? "" : Template
            .Replace("{filename}", fileName)
            .Replace("{date}", date.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
            .Replace("{size}", Format.Bytes(size));
}

public static class Format
{
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1) { value /= 1000; unit++; }
        var culture = L10n.Culture;
        return unit == 0 ? $"{bytes} {units[0]}" : value.ToString(value < 10 ? "0.#" : "0", culture) + " " + units[unit];
    }

    /// <summary>"2 Eyl 2026, 15:15" in the UI language.</summary>
    public static string UploadDate(DateTimeOffset date) =>
        date.ToLocalTime().ToString("d MMM yyyy, HH:mm", L10n.Culture);

    public static string Percent(double fraction) => fraction.ToString("P0", L10n.Culture);

    public static string Duration(TimeSpan span) => span.TotalHours >= 1
        ? $"{(int)span.TotalHours}h {span.Minutes}m"
        : span.TotalMinutes >= 1 ? $"{span.Minutes}m {span.Seconds}s" : $"{span.Seconds}s";
}
