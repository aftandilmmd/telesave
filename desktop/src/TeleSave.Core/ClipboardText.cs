using System.Globalization;

namespace TeleSave.Core;

/// <summary>Copied text sent from the clipboard: as Telegram messages, or as a .txt file.</summary>
public enum TextPasteMode { Message, File }

/// <summary>Splits copied text into Telegram messages. Same rules as the macOS app (ClipboardText.swift).</summary>
public static class ClipboardText
{
    /// <summary>Telegram's limit for one text message, in UTF-16 code units.</summary>
    public const int MessageLimit = 4096;

    /// <summary>The text without leading/trailing whitespace, or null when nothing is left.</summary>
    public static string? Prepare(string? text) => text?.Trim() is { Length: > 0 } trimmed ? trimmed : null;

    /// <summary>
    /// Chunks of at most <paramref name="limit"/> UTF-16 units. A chunk ends after the last line break in its second half,
    /// else after the last space there, else at the limit; never inside a character (emoji, combined letters).
    /// </summary>
    public static IReadOnlyList<string> Split(string text, int limit = MessageLimit)
    {
        var chunks = new List<string>();
        var position = 0;
        while (true)
        {
            while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
            if (position >= text.Length) break;
            if (text.Length - position <= limit)
            {
                chunks.Add(text[position..]);
                break;
            }
            int end = position, units = 0;
            int? lastNewline = null, lastSpace = null;
            while (end < text.Length)
            {
                var width = StringInfo.GetNextTextElementLength(text, end);
                if (units + width > limit) break;
                var first = text[end];
                units += width;
                end += width;
                if (units >= limit / 2)
                {
                    if (IsNewline(first)) lastNewline = end;
                    else if (char.IsWhiteSpace(first)) lastSpace = end;
                }
            }
            // A single character longer than the limit (pathological): send it whole rather than loop forever.
            if (end == position) end += StringInfo.GetNextTextElementLength(text, position);
            var cut = lastNewline ?? lastSpace ?? end;
            var chunk = text[position..cut].TrimEnd();
            if (chunk.Length > 0) chunks.Add(chunk);
            position = cut;
        }
        return chunks;
    }

    private static bool IsNewline(char c) => c is '\n' or '\r' or '\u000B' or '\u000C' or '\u0085' or '\u2028' or '\u2029';
}
