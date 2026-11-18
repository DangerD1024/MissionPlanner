using System.Globalization;
using System.Text.RegularExpressions;
using MissionPlanner.App.Core.Params;
using MissionPlanner.ArduPilot;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>Orders names the way people read them: SERVO2 before SERVO10, case-insensitive.</summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static NaturalStringComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x == null)
            return -1;
        if (y == null)
            return 1;

        int ix = 0, iy = 0;
        while (true)
        {
            if (ix == x.Length)
                return iy == y.Length ? 0 : -1;
            if (iy == y.Length)
                return 1;

            var cx = x[ix];
            var cy = y[iy];
            if (char.IsDigit(cx) && char.IsDigit(cy))
            {
                while (ix < x.Length && x[ix] == '0')
                    ix++;
                while (iy < y.Length && y[iy] == '0')
                    iy++;
                int ex = ix, ey = iy;
                while (ex < x.Length && char.IsDigit(x[ex]))
                    ex++;
                while (ey < y.Length && char.IsDigit(y[ey]))
                    ey++;
                if (ex - ix != ey - iy)
                    return (ex - ix) - (ey - iy);
                for (; ix < ex; ix++, iy++)
                {
                    if (x[ix] != y[iy])
                        return x[ix] - y[iy];
                }
            }
            else
            {
                var c = char.ToUpperInvariant(cx).CompareTo(char.ToUpperInvariant(cy));
                if (c != 0)
                    return c;
                ix++;
                iy++;
            }
        }
    }
}

/// <summary>An entry of the Full Parameter List's left tree: "All" or a name prefix.</summary>
public sealed record ParamGroup(string Name, int Count, bool IsAll = false)
{
    public bool Matches(string paramName) => IsAll || ParamGroups.GroupOf(paramName) == Name;

    public override string ToString() => Name;
}

public static class ParamGroups
{
    public const string AllName = "All";

    /// <summary>The group a parameter belongs to: its name up to the first '_' (ACRO, AHRS, BATT...).</summary>
    public static string GroupOf(string name)
    {
        var i = name.IndexOf('_');
        return i > 0 ? name[..i] : name;
    }

    /// <summary>"All" followed by every group in natural order, with parameter counts.</summary>
    public static IReadOnlyList<ParamGroup> Build(IEnumerable<string> names)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var total = 0;
        foreach (var n in names)
        {
            total++;
            var g = GroupOf(n);
            counts[g] = counts.GetValueOrDefault(g) + 1;
        }

        var list = new List<ParamGroup>(counts.Count + 1) { new(AllName, total, IsAll: true) };
        list.AddRange(counts.OrderBy(kv => kv.Key, NaturalStringComparer.Instance).Select(kv => new ParamGroup(kv.Key, kv.Value)));
        return list;
    }
}

/// <summary>What the Full Parameter List shows.</summary>
public sealed record ParamFilterOptions(
    ParamGroup? Group = null,
    string? Search = null,
    bool ModifiedOnly = false,
    bool NonDefaultOnly = false);

public static class ParamFilter
{
    /// <summary>Rows passing every active filter, in their original order.</summary>
    public static List<ParamRow> Apply(IEnumerable<ParamRow> rows, ParamFilterOptions options)
    {
        var matcher = SearchMatcher.Create(options.Search);
        var result = new List<ParamRow>();
        foreach (var r in rows)
        {
            if (options.Group is { } g && !g.Matches(r.Name))
                continue;
            if (options.ModifiedOnly && !r.IsModified)
                continue;
            if (options.NonDefaultOnly && !r.IsNonDefault)
                continue;
            if (matcher != null && !matcher.Matches(r.Name, r.DisplayName, r.Description))
                continue;
            result.Add(r);
        }

        return result;
    }
}

/// <summary>
/// Live search over names and documentation: every word must appear (case-insensitive);
/// '*' and '?' work as wildcards, as in Mission Planner.
/// </summary>
public sealed class SearchMatcher
{
    private readonly string[] _terms;
    private readonly Regex? _regex;

    private SearchMatcher(string[] terms, Regex? regex)
    {
        _terms = terms;
        _regex = regex;
    }

    /// <summary>Null when the search text is empty (everything matches).</summary>
    public static SearchMatcher? Create(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return null;
        var s = search.Trim();
        if (s.Contains('*') || s.Contains('?'))
        {
            var pattern = Regex.Escape(s).Replace(@"\*", ".*").Replace(@"\?", ".");
            return new SearchMatcher([], new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline));
        }

        return new SearchMatcher(s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries), null);
    }

    public bool Matches(params string?[] fields)
    {
        if (_regex != null)
            return fields.Any(f => f != null && _regex.IsMatch(f));

        foreach (var term in _terms)
        {
            var found = false;
            foreach (var f in fields)
            {
                if (f != null && f.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
                return false;
        }

        return true;
    }
}

/// <summary>What loading a .param file onto the current list would change.</summary>
public sealed record ParamFileApplyResult(
    IReadOnlyDictionary<string, double> Edits,
    IReadOnlyList<string> Unchanged,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> ReadOnlySkipped);

public static class ParamFileLoad
{
    /// <summary>
    /// Sorts a file's values into edits (differ from the vehicle), unchanged, names the vehicle
    /// does not have, and read-only parameters that are not touched.
    /// </summary>
    public static ParamFileApplyResult Compute(IReadOnlyDictionary<string, double> file, Func<string, double?> current,
        Func<string, ParamMeta?> meta)
    {
        var edits = new Dictionary<string, double>(StringComparer.Ordinal);
        var same = new List<string>();
        var missing = new List<string>();
        var readOnly = new List<string>();
        foreach (var (name, value) in file.OrderBy(kv => kv.Key, NaturalStringComparer.Instance))
        {
            if (current(name) is not { } now)
            {
                missing.Add(name);
                continue;
            }

            if (ParamValues.Same(now, value))
            {
                same.Add(name);
                continue;
            }

            if (meta(name)?.ReadOnly == true)
            {
                readOnly.Add(name);
                continue;
            }

            edits[name] = value;
        }

        return new ParamFileApplyResult(edits, same, missing, readOnly);
    }
}

/// <summary>One difference between the current list and a file.</summary>
public sealed record ParamDifference(string Name, double Current, double FromFile);

public static class ParamCompare
{
    /// <summary>
    /// Parameters present in both lists whose values differ (as the vehicle stores them, so
    /// 0.1 in a file equals the vehicle's 0.100000001). Names only in the file are counted.
    /// </summary>
    public static IReadOnlyList<ParamDifference> Diff(IReadOnlyDictionary<string, double> current,
        IReadOnlyDictionary<string, double> file, out int onlyInFile)
    {
        onlyInFile = 0;
        var diffs = new List<ParamDifference>();
        foreach (var (name, value) in file)
        {
            if (!current.TryGetValue(name, out var now))
            {
                onlyInFile++;
                continue;
            }

            if (!ParamValues.Same(now, value))
                diffs.Add(new ParamDifference(name, now, value));
        }

        diffs.Sort((a, b) => NaturalStringComparer.Instance.Compare(a.Name, b.Name));
        return diffs;
    }
}

/// <summary>Favourite parameters, listed first on the parameter pages.</summary>
public static class ParamFavourites
{
    public const string SettingsKey = "next_param_favs";

    public static HashSet<string> Load() => new(Settings.Instance.GetList(SettingsKey), StringComparer.Ordinal);

    public static void Set(string name, bool favourite)
    {
        if (favourite)
        {
            if (!Settings.Instance.GetList(SettingsKey).Contains(name))
                Settings.Instance.AppendList(SettingsKey, name);
        }
        else
        {
            // RemoveList also drops the key once the list is empty (SetList ignores empty lists)
            Settings.Instance.RemoveList(SettingsKey, name);
        }
    }

    /// <summary>Favourites first, then natural name order.</summary>
    public static Comparison<string> Order(ISet<string> favourites) => (a, b) =>
    {
        var fa = favourites.Contains(a);
        var fb = favourites.Contains(b);
        if (fa != fb)
            return fa ? -1 : 1;
        return NaturalStringComparer.Instance.Compare(a, b);
    };
}

/// <summary>Text shown for a parameter's documented options.</summary>
public static class ParamText
{
    /// <summary>
    /// Compact one-line options for a grid cell (range, then values or bits) and the full
    /// one-per-line list for its tooltip.
    /// </summary>
    public static (string Short, string Full) Options(ParamMeta? meta)
    {
        if (meta == null)
            return ("", "");

        var parts = new List<string>();
        var full = new List<string>();
        if (meta.Min is { } min && meta.Max is { } max)
        {
            var range = $"{ParamValues.Format(min)} to {ParamValues.Format(max)}";
            parts.Add(range);
            full.Add("Range: " + range);
        }

        if (meta.Values.Count > 0)
        {
            var values = meta.Values.Select(v => $"{ParamValues.Format(v.Key)}:{v.Value}").ToList();
            parts.Add(string.Join(", ", values));
            full.AddRange(values);
        }
        else if (meta.Bitmask.Count > 0)
        {
            var bits = meta.Bitmask.Select(b => $"{b.Key}:{b.Value}").ToList();
            parts.Add("Bitmask " + string.Join(", ", bits));
            full.Add("Bitmask (bit:meaning)");
            full.AddRange(bits);
        }

        if (meta.Increment is { } inc)
            full.Add("Increment: " + ParamValues.Format(inc));

        return (string.Join("  ", parts), string.Join("\n", full));
    }
}

/// <summary>Guesses which vehicle a parameter file is for, to show the right documentation offline.</summary>
public static class FirmwareGuess
{
    public static Firmwares FromParamNames(IEnumerable<string> names)
    {
        var set = names as ISet<string> ?? new HashSet<string>(names, StringComparer.Ordinal);
        if (set.Contains("SURFACE_DEPTH") || set.Contains("JS_GAIN_DEFAULT"))
            return Firmwares.ArduSub;
        if (set.Contains("TECS_SPDWEIGHT") || set.Contains("KFF_RDDRMIX") || set.Contains("ARSPD_FBW_MIN") ||
            set.Contains("AIRSPEED_MIN") || set.Contains("PTCH_RATE_P") || set.Contains("PTCH2SRV_P"))
            return Firmwares.ArduPlane;
        if (set.Contains("CRUISE_SPEED") && set.Contains("CRUISE_THROTTLE"))
            return Firmwares.ArduRover;
        if (set.Contains("YAW_SLEW_TIME") || set.Contains("ONOFF_YAW_RATE"))
            return Firmwares.ArduTracker;
        return Firmwares.ArduCopter2;
    }
}
