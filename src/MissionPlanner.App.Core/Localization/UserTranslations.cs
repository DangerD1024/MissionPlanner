using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MissionPlanner.App.Core.Localization;

/// <summary>The string list of Localization/untranslated.txt, as src/tools/LocalizationHarvest writes it.</summary>
public static class StringList
{
    /// <summary>The English column: "text TAB cultures" lines, \n \t \r \\ escaped, '#' lines are comments.</summary>
    public static List<string> Parse(string text)
    {
        var list = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            list.Add(Unescape(line.Split('\t')[0]));
        }

        return list;
    }

    private static string Unescape(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                char? c = s[i + 1] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', '\\' => '\\', _ => null };
                if (c is { } ch)
                {
                    sb.Append(ch);
                    i++;
                    continue;
                }
            }

            sb.Append(s[i]);
        }

        return sb.ToString();
    }
}

/// <summary>
/// A user's own translations: &lt;user data&gt;/Localization/&lt;culture&gt;.json, applied on top of the
/// built-in tables (see <see cref="AppStartup"/>). Written by TOOLS > Translation editor with the
/// table format's "overrides" section, so the file can also be sent in as a correction.
/// </summary>
public static class UserTranslations
{
    public static string Directory => Path.Combine(Utilities.Settings.GetUserDataDirectory(), "Localization");

    public static string PathFor(string culture, string? directory = null) => Path.Combine(directory ?? Directory, culture + ".json");

    /// <summary>Whether a source is the user's folder (the editor shows the built-in tables without it).</summary>
    public static bool IsUserSource(ITranslationSource source, string? directory = null) =>
        source is DirectoryTranslationSource d &&
        string.Equals(Path.GetFullPath(d.Directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(directory ?? Directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>English → translation from a user file ("strings" then "overrides"); empty when there is none.</summary>
    public static Dictionary<string, string> Read(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
            return map;
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        foreach (var section in new[] { "strings", "overrides" })
        {
            if (!doc.RootElement.TryGetProperty(section, out var m) || m.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var p in m.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String)
                    map[p.Name] = p.Value.GetString()!;
            }
        }

        return map;
    }

    /// <summary>Writes the overrides (sorted, readable UTF-8) as a table file; no entries deletes the file.</summary>
    public static void Write(string path, string culture, IEnumerable<KeyValuePair<string, string>> overrides)
    {
        var entries = overrides.Where(e => e.Key.Length > 0 && e.Value.Length > 0)
            .GroupBy(e => e.Key, StringComparer.Ordinal).Select(g => g.Last())
            .OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase).ToList();
        if (entries.Count == 0)
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }

        var map = new JsonObject();
        foreach (var (k, v) in entries)
            map[k] = v;
        var root = new JsonObject
        {
            ["culture"] = culture,
            ["about"] = "A user's corrections, written by Mission Planner's translation editor (TOOLS). They win over the built-in table.",
            ["overrides"] = map,
        };

        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) + Environment.NewLine);
        File.Move(temp, path, overwrite: true);
    }
}
