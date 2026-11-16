using System.Globalization;
using System.Text.Json;

namespace TeleSave.Core;

public sealed record LanguageInfo(string Code, string Name, CultureInfo Culture, int Order);

/// <summary>
/// Translations from one JSON file per language (shared with the macOS app):
/// { "_language": { "name", "locale", "order" }, "strings": { "&lt;Turkish key&gt;": "&lt;translation&gt;" } }.
/// Placeholders are %@ (all arguments are strings); translations may reorder them with %1$@, %2$@.
/// English is the fallback for unsupported system languages and for missing strings.
/// </summary>
public static class L10n
{
    public const string SystemCode = "system";
    public const string FallbackCode = "en";

    private static readonly object Gate = new();
    private static string _selected = SystemCode;
    private static Catalog? _catalog;
    private static string? _directory;

    private sealed record Catalog(IReadOnlyList<LanguageInfo> Languages, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Strings);

    /// <summary>Directory holding the *.json files; defaults to "Localization" next to the app.</summary>
    public static string Directory
    {
        get => _directory ?? Path.Combine(AppContext.BaseDirectory, "Localization");
        set { lock (Gate) { _directory = value; _catalog = null; } }
    }

    public static string SelectedCode
    {
        get { lock (Gate) return _selected; }
        set { lock (Gate) _selected = value; }
    }

    /// <summary>Preferred UI languages of the OS, most preferred first (injectable for tests).</summary>
    public static Func<IReadOnlyList<string>> PreferredLanguages { get; set; } = () => new[] { CultureInfo.CurrentUICulture.Name };

    public static IReadOnlyList<LanguageInfo> Languages => Load().Languages;

    public static string CurrentCode => Resolve(SelectedCode, Load().Strings.Keys.ToHashSet(), PreferredLanguages());

    /// <summary>What "System" resolves to right now.</summary>
    public static string SystemLanguageCode => Resolve(SystemCode, Load().Strings.Keys.ToHashSet(), PreferredLanguages());

    public static CultureInfo Culture
    {
        get
        {
            var code = CurrentCode;
            return Languages.FirstOrDefault(l => l.Code == code)?.Culture ?? CultureInfo.GetCultureInfo("en-US");
        }
    }

    internal static string Resolve(string selected, IReadOnlySet<string> available, IReadOnlyList<string> preferred)
    {
        if (selected != SystemCode && available.Contains(selected)) return selected;
        foreach (var identifier in preferred)
        {
            var code = identifier.Split('-', '_')[0].ToLowerInvariant();
            if (available.Contains(code)) return code;
        }
        return FallbackCode;
    }

    /// <summary>Localized string for the Turkish key, with %@ placeholders filled from args.</summary>
    public static string T(string key, params string[] args)
    {
        var catalog = Load();
        var code = CurrentCode;
        var template = (catalog.Strings.TryGetValue(code, out var s) && s.TryGetValue(key, out var v)) ? v
            : (catalog.Strings.TryGetValue(FallbackCode, out var en) && en.TryGetValue(key, out var e)) ? e
            : key;
        return Format(template, args);
    }

    internal static string Format(string template, IReadOnlyList<string> args)
    {
        if (args.Count == 0) return template;
        var result = template;
        for (var i = 0; i < args.Count; i++) result = result.Replace($"%{i + 1}$@", args[i]);
        var next = 0;
        int index;
        while (next < args.Count && (index = result.IndexOf("%@", StringComparison.Ordinal)) >= 0)
        {
            result = result[..index] + args[next] + result[(index + 2)..];
            next++;
        }
        return result;
    }

    internal static int PlaceholderCount(string template)
    {
        var positional = Enumerable.Range(1, 9).Count(i => template.Contains($"%{i}$@"));
        return positional > 0 ? positional : (template.Length - template.Replace("%@", "").Length) / 2;
    }

    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> AllStrings => Load().Strings;

    private static Catalog Load()
    {
        lock (Gate)
        {
            if (_catalog is not null) return _catalog;
            var languages = new List<LanguageInfo>();
            var strings = new Dictionary<string, IReadOnlyDictionary<string, string>>();
            if (System.IO.Directory.Exists(Directory))
            {
                foreach (var file in System.IO.Directory.GetFiles(Directory, "*.json"))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(file));
                        var meta = doc.RootElement.GetProperty("_language");
                        var code = Path.GetFileNameWithoutExtension(file);
                        var map = doc.RootElement.GetProperty("strings").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
                        strings[code] = map;
                        var locale = meta.GetProperty("locale").GetString()!.Replace('_', '-');
                        languages.Add(new LanguageInfo(code, meta.GetProperty("name").GetString()!,
                            CultureInfo.GetCultureInfo(locale),
                            meta.TryGetProperty("order", out var order) ? order.GetInt32() : int.MaxValue));
                    }
                    catch (Exception)
                    {
                        // A broken language file must not take the app down.
                    }
                }
            }
            languages.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : string.CompareOrdinal(a.Name, b.Name));
            return _catalog = new Catalog(languages, strings);
        }
    }
}
