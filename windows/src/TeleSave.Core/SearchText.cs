using System.Globalization;
using System.Text;

namespace TeleSave.Core;

/// <summary>
/// Accent- and case-insensitive matching tuned for Turkish and Azerbaijani:
/// "Iğdır", "IGDIR" and "igdir" match; so do "Əli" and "eli", "ŞEMA" and "sema".
/// </summary>
public static class SearchText
{
    public static string Normalize(string text)
    {
        // Letters without a Unicode decomposition would survive accent stripping.
        var mapped = text.Replace('ı', 'i').Replace('I', 'i').Replace('İ', 'i').Replace('ə', 'e').Replace('Ə', 'e');
        var decomposed = mapped.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(c);
        }
        return builder.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    /// <summary>Every word of the query appears somewhere in the text, in any order.</summary>
    public static bool Matches(string text, string query)
    {
        var haystack = Normalize(text);
        return Normalize(query).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).All(haystack.Contains);
    }
}
