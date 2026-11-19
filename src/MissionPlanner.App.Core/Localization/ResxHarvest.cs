using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MissionPlanner.App.Core.Localization;

/// <summary>Reads the user-visible strings of a .resx file (Strings.resx entries, control Text/ToolTip...).</summary>
public static partial class ResxReader
{
    [GeneratedRegex(@"^(Text|ToolTip|ToolTipText|HeaderText|Caption|Title|Items\d*)$")]
    private static partial Regex TextProperty();

    /// <summary>
    /// Name → value of the entries that hold text: no type or a string type, and either a plain resource
    /// name (Strings.resx) or a control's text property ("BUT_connect.Text", "$this.Text", "label1.ToolTip").
    /// Sizes, locations, images and designer metadata ("&gt;&gt;label1.Name") are skipped.
    /// </summary>
    public static Dictionary<string, string> ReadStrings(string xml)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        }
        catch (System.Xml.XmlException)
        {
            return result;
        }

        foreach (var data in doc.Root?.Elements("data") ?? [])
        {
            var name = (string?)data.Attribute("name");
            if (string.IsNullOrEmpty(name) || name.StartsWith(">>", StringComparison.Ordinal))
                continue;
            var type = (string?)data.Attribute("type");
            if (data.Attribute("mimetype") != null || (type != null && !type.StartsWith("System.String", StringComparison.Ordinal)))
                continue;
            var dot = name.LastIndexOf('.');
            if (dot >= 0 && !TextProperty().IsMatch(name[(dot + 1)..]))
                continue;
            var value = data.Element("value")?.Value;
            if (!string.IsNullOrWhiteSpace(value))
                result[name] = value;
        }

        return result;
    }
}

/// <summary>
/// Builds one language's table from Mission Planner's .resx files: every English entry is paired with
/// the same key in the culture sibling. When one English text has several translations, the best
/// ranked source wins (the language's own files before fallback cultures, ExtLibs/Strings/Strings.resx
/// before screen files), then the most frequent translation.
/// </summary>
public sealed class ResxHarvest
{
    private readonly record struct Candidate(string English, string Translation, int Rank);

    private readonly Dictionary<string, List<Candidate>> _candidates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _colons = new(StringComparer.Ordinal);

    /// <summary>Pairs entries found in both files. Lower <paramref name="rank"/> is preferred.</summary>
    public int Add(IReadOnlyDictionary<string, string> english, IReadOnlyDictionary<string, string> translated, int rank)
    {
        var added = 0;
        foreach (var (name, value) in translated)
        {
            if (!english.TryGetValue(name, out var source))
                continue;
            var s = LocalizationText.Split(source);
            var t = LocalizationText.Split(value);
            if (s.Core.Length == 0 || t.Core.Length == 0 || !LocalizationText.HasLetters(s.Core))
                continue;
            // a copy of the English text is not a translation
            if (string.Equals(s.Core, t.Core, StringComparison.Ordinal))
                continue;
            if (!LocalizationText.SameFormatItems(s.Core, t.Core))
                continue;

            var key = s.Core.ToLowerInvariant();
            if (!_candidates.TryGetValue(key, out var list))
                _candidates[key] = list = new List<Candidate>();
            list.Add(new Candidate(s.Core, t.Core, rank));
            added++;

            if (s.Suffix.TrimStart() is ":" or "：" && t.Suffix.TrimStart() is ":" or "：")
                _colons[t.Suffix] = _colons.GetValueOrDefault(t.Suffix) + 1;
        }

        return added;
    }

    public void Add(string englishXml, string translatedXml, int rank) =>
        Add(ResxReader.ReadStrings(englishXml), ResxReader.ReadStrings(translatedXml), rank);

    /// <summary>The colon form most translations use.</summary>
    public string Colon => _colons.Count == 0
        ? ":"
        : _colons.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).First().Key;

    /// <summary>
    /// English → chosen translation, sorted by English. Text written in capitals in Mission Planner
    /// ("DATA", "CONNECT": its menu) keeps its own entry next to the ordinary one ("Data").
    /// </summary>
    public SortedDictionary<string, string> Build()
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var all in _candidates.Values)
        {
            foreach (var group in all.GroupBy(c => LocalizationText.IsAllCaps(c.English)))
                Choose(group.ToList(), result);
        }

        return result;
    }

    private static void Choose(List<Candidate> list, SortedDictionary<string, string> result)
    {
        var best = list.Min(c => c.Rank);
        var chosen = list.Where(c => c.Rank == best)
            .GroupBy(c => c.Translation, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .First();
        var english = chosen
            .GroupBy(c => c.English, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .First().Key;
        result[english] = chosen.Key;
    }
}
