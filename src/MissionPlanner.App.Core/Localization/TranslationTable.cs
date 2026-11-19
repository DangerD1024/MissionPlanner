using System.Globalization;
using System.Text.Json;

namespace MissionPlanner.App.Core.Localization;

/// <summary>
/// English → translated text for one language, as stored in Localization/&lt;culture&gt;.json:
/// <code>
/// {
///   "culture": "ru-RU",
///   "name": "Русский",
///   "colon": ":",
///   "overrides": { "Connect": "Подключить" },
///   "strings":   { "Accel Calibration": "Калибровка акселерометра", ... }
/// }
/// </code>
/// Keys are English text without the decoration <see cref="LocalizationText.Split"/> removes; lookups
/// ignore case. "strings" is generated from Mission Planner's .resx translations; "overrides" is kept
/// by the generator and wins, so hand-made additions and corrections go there.
/// </summary>
public sealed class TranslationTable
{
    // by lower-case key; text in capitals ("CONNECT", Mission Planner's menu labels) has its own entries,
    // used for text in capitals and otherwise only when there is no other
    private readonly Dictionary<string, (string English, string Translation)> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string English, string Translation)> _capitals = new(StringComparer.Ordinal);

    public TranslationTable(string culture)
    {
        Culture = culture;
        CultureInfo = TryCulture(culture) ?? CultureInfo.InvariantCulture;
    }

    public string Culture { get; }

    public CultureInfo CultureInfo { get; }

    /// <summary>How the language writes a trailing colon (":" mostly, "：" in some CJK files, " :" in French).</summary>
    public string Colon { get; set; } = ":";

    public int Count => _entries.Count + _capitals.Count;

    /// <summary>The entries as English → translation.</summary>
    public IEnumerable<KeyValuePair<string, string>> Entries =>
        _entries.Values.Concat(_capitals.Values).Select(e => new KeyValuePair<string, string>(e.English, e.Translation));

    /// <summary>Adds or replaces an entry; both sides are normalised. Empty or letter-less text is ignored.</summary>
    public void Set(string english, string translation)
    {
        var source = LocalizationText.Split(english).Core;
        var target = LocalizationText.Split(translation).Core;
        if (source.Length == 0 || target.Length == 0 || !LocalizationText.HasLetters(source))
            return;
        if (!LocalizationText.SameFormatItems(source, target))
            return;
        (LocalizationText.IsAllCaps(source) ? _capitals : _entries)[source.ToLowerInvariant()] = (source, target);
    }

    public bool Contains(string english)
    {
        var key = LocalizationText.Key(english);
        return _entries.ContainsKey(key) || _capitals.ContainsKey(key);
    }

    private bool TryFind(string core, out (string English, string Translation) entry)
    {
        var key = core.ToLowerInvariant();
        return LocalizationText.IsAllCaps(core)
            ? _capitals.TryGetValue(key, out entry) || _entries.TryGetValue(key, out entry)
            : _entries.TryGetValue(key, out entry) || _capitals.TryGetValue(key, out entry);
    }

    /// <summary>
    /// Translates text, keeping its surrounding whitespace and trailing ':' / '...' and upper-casing
    /// the translation when the text is in capitals ("CONNECT") but the entry is not.
    /// </summary>
    public bool TryTranslate(string? text, out string result)
    {
        result = text ?? "";
        if (string.IsNullOrEmpty(text))
            return false;
        var parts = LocalizationText.Split(text);
        if (parts.Core.Length == 0 || !TryFind(parts.Core, out var entry))
            return false;

        var core = entry.Translation;
        if (LocalizationText.IsAllCaps(parts.Core) && !LocalizationText.IsAllCaps(entry.English))
            core = core.ToUpper(CultureInfo);

        var suffix = parts.Suffix.TrimStart() switch
        {
            ":" or "：" => Colon,
            _ => parts.Suffix,
        };
        result = parts.Leading + core + suffix + parts.Trailing;
        return true;
    }

    /// <summary>Reads a table file; "overrides" are applied after "strings".</summary>
    public static TranslationTable Load(Stream json, string? culture = null)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        var root = doc.RootElement;
        var table = new TranslationTable(culture ?? Text(root, "culture") ?? "");
        if (Text(root, "colon") is { Length: > 0 } colon)
            table.Colon = colon;
        table.Merge(root);
        return table;
    }

    /// <summary>Adds the entries of another table file (a user's corrections) on top of these.</summary>
    public void MergeFrom(Stream json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        if (Text(doc.RootElement, "colon") is { Length: > 0 } colon)
            Colon = colon;
        Merge(doc.RootElement);
    }

    private void Merge(JsonElement root)
    {
        foreach (var section in new[] { "strings", "overrides" })
        {
            if (!root.TryGetProperty(section, out var map) || map.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var p in map.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String)
                    Set(p.Name, p.Value.GetString()!);
            }
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    internal static CultureInfo? TryCulture(string name)
    {
        try
        {
            return string.IsNullOrEmpty(name) ? null : CultureInfo.GetCultureInfo(name);
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }
}
