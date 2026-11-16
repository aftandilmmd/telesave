namespace TeleSave.Core;

/// <summary>File type checks by extension / MIME type (the Windows counterpart of UTType checks).</summary>
public static class FileTypes
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "jpg", "jpeg", "png", "heic", "heif", "webp", "gif", "bmp", "tif", "tiff", "avif" };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "mp4", "m4v", "mov", "avi", "mkv", "webm", "wmv", "mpg", "mpeg", "3gp" };

    /// <summary>Formats Telegram accepts as compressed photos.</summary>
    private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "jpg", "jpeg", "png", "heic", "webp" };

    /// <summary>Formats Telegram plays inline as video.</summary>
    private static readonly HashSet<string> InlineVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "mp4", "m4v", "mov" };

    public static string Extension(string fileName) => Path.GetExtension(fileName).TrimStart('.');

    public static bool IsImage(string fileName, string mimeType = "") =>
        mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || ImageExtensions.Contains(Extension(fileName));

    public static bool IsVideo(string fileName, string mimeType = "") =>
        mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || VideoExtensions.Contains(Extension(fileName));

    internal static bool IsTelegramPhoto(string fileName) => PhotoExtensions.Contains(Extension(fileName));

    internal static bool IsTelegramVideo(string fileName) => InlineVideoExtensions.Contains(Extension(fileName));
}
