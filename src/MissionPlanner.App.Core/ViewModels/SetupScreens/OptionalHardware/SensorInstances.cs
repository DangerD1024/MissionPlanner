using System.Globalization;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.OptionalHardware;

/// <summary>One instance of a multi-instance sensor library (RNGFND1_, ARSPD2_...).</summary>
/// <param name="Number">1-based instance number.</param>
/// <param name="Prefix">Parameter prefix including the trailing underscore ("RNGFND1_", or "RNGFND_" on old firmware).</param>
public sealed record SensorInstance(int Number, string Prefix)
{
    public string Title(string what) => $"{what} {Number.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>Finds the instances of a sensor from the parameter names.</summary>
public static class SensorInstances
{
    /// <summary>
    /// The instances that have <c>{stem}{n}_{key}</c>. ArduPilot numbers them 1-9 then A, B...
    /// (RNGFNDA_TYPE is the tenth rangefinder); before multi-instance support the first one had
    /// no number (<c>RNGFND_TYPE</c>, <c>ARSPD_TYPE</c>).
    /// </summary>
    public static IReadOnlyList<SensorInstance> Find(Func<string, bool> has, string stem, string key, int max = 10)
    {
        var list = new List<SensorInstance>();
        for (var n = 1; n <= max; n++)
        {
            var prefix = stem + InstanceChar(n) + "_";
            if (has(prefix + key))
                list.Add(new SensorInstance(n, prefix));
            else if (n == 1 && has(stem + "_" + key))
                list.Add(new SensorInstance(1, stem + "_"));
        }

        return list;
    }

    /// <summary>"1".."9" then "A", "B"... as ArduPilot names instances 10 and up.</summary>
    public static string InstanceChar(int n) => n < 10
        ? n.ToString(CultureInfo.InvariantCulture)
        : ((char)('A' + n - 10)).ToString();

    /// <summary>
    /// Orders the instance's parameters: the given keys first (in that order), then the rest by name.
    /// Returns full parameter names.
    /// </summary>
    public static (IReadOnlyList<string> Main, IReadOnlyList<string> Others) Split(IEnumerable<string> names, string prefix,
        IReadOnlyList<string[]> mainKeys, Func<string, bool>? exclude = null)
    {
        var mine = names.Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.Length > prefix.Length
                                    && (exclude == null || !exclude(n)))
            .ToHashSet(StringComparer.Ordinal);
        var main = new List<string>();
        foreach (var aliases in mainKeys)
        {
            var found = aliases.Select(k => prefix + k).FirstOrDefault(mine.Contains);
            if (found != null && !main.Contains(found))
                main.Add(found);
        }

        var rest = mine.Except(main).OrderBy(n => n, StringComparer.Ordinal).ToList();
        return (main, rest);
    }
}
