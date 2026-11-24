using System.Globalization;
using System.Xml.Linq;
using log4net;
using MissionPlanner.ArduPilot;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.Params;

/// <summary>Documentation for one parameter (from ArduPilot's apm.pdef.xml).</summary>
public sealed record ParamMeta(
    string Name,
    string DisplayName,
    string Description,
    string Units,
    double? Min,
    double? Max,
    double? Increment,
    IReadOnlyList<KeyValuePair<double, string>> Values,
    IReadOnlyList<KeyValuePair<int, string>> Bitmask,
    string UserLevel,
    bool RebootRequired,
    bool ReadOnly)
{
    public bool IsStandard => string.Equals(UserLevel, "Standard", StringComparison.OrdinalIgnoreCase);

    public string RangeText => Min is { } a && Max is { } b
        ? $"{a.ToString(CultureInfo.InvariantCulture)} {b.ToString(CultureInfo.InvariantCulture)}"
        : "";

    public string OptionsText => Values.Count > 0
        ? string.Join("\n", Values.Select(v => $"{v.Key.ToString(CultureInfo.InvariantCulture)}:{v.Value}"))
        : Bitmask.Count > 0 ? string.Join("\n", Bitmask.Select(b => $"bit {b.Key}:{b.Value}")) : RangeText;
}

/// <summary>
/// Parameter documentation indexed once per vehicle type. The library's own lookup scans the XML
/// for every query; a full parameter list makes thousands of them, so this builds a dictionary
/// off the UI thread instead, from the same files the WinForms app downloads.
/// </summary>
public static class ParamMetadata
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(ParamMetadata));
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Indexed> Indexes = new();

    /// <summary>An index and the file it came from, so a newer download replaces it.</summary>
    private sealed record Indexed(Dictionary<string, ParamMeta> Index, string? Source, DateTime SourceTime);

    /// <summary>Key of the pdef file set for a firmware ("ArduPlane", "ArduCopter", "Rover"...).</summary>
    public static string VehicleKey(Firmwares firmware) => firmware switch
    {
        Firmwares.ArduPlane => "ArduPlane",
        Firmwares.ArduCopter2 => "ArduCopter",
        Firmwares.ArduRover => "Rover",
        Firmwares.ArduSub => "ArduSub",
        Firmwares.ArduTracker => "AntennaTracker",
        Firmwares.AP_Periph => "AP_Periph",
        _ => "ArduCopter",
    };

    private static string VersionedPrefix(Firmwares firmware) => firmware switch
    {
        Firmwares.ArduPlane => "Plane",
        Firmwares.ArduCopter2 => "Copter",
        Firmwares.ArduRover => "Rover",
        Firmwares.ArduSub => "Sub",
        Firmwares.ArduTracker => "Tracker",
        _ => "",
    };

    /// <summary>The loaded index for a vehicle, or null until <see cref="LoadAsync"/> has run.</summary>
    public static IReadOnlyDictionary<string, ParamMeta>? For(Firmwares firmware, Version? version = null)
    {
        lock (Gate)
            return Indexes.GetValueOrDefault(CacheKey(firmware, version))?.Index;
    }

    /// <summary>
    /// Downloads newer metadata if due (weekly, like the WinForms app), then indexes the best file
    /// available: the one for this exact firmware version, else the generic one, else the
    /// legacy file shipped with the app.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, ParamMeta>> LoadAsync(Firmwares firmware, Version? version, bool download = true)
    {
        var key = CacheKey(firmware, version);
        lock (Gate)
        {
            if (Indexes.TryGetValue(key, out var ready) && IsCurrent(ready))
                return ready.Index;
        }

        if (download)
        {
            try
            {
                await ParameterMetaDataRepositoryAPMpdef.GetMetaData().ConfigureAwait(false);
                if (version != null && version.Revision != 255)
                    await ParameterMetaDataRepositoryAPMpdef.GetMetaDataVersioned(version).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn("metadata download", ex);
            }
        }

        var built = await Task.Run(() => Build(firmware, version)).ConfigureAwait(false);
        lock (Gate)
            Indexes[key] = built;
        return built.Index;
    }

    /// <summary>Forgets every index, e.g. after the documentation files were downloaded again.</summary>
    public static void Invalidate()
    {
        lock (Gate)
            Indexes.Clear();
    }

    private static bool IsCurrent(Indexed indexed) =>
        indexed.Source == null || !File.Exists(indexed.Source) || File.GetLastWriteTimeUtc(indexed.Source) == indexed.SourceTime;

    /// <summary>"4.7.2", or "" while the vehicle has not reported its version (Version(0,0) has no Build).</summary>
    private static string VersionText(Version? version) => version is { Build: >= 0 } ? version.ToString(3) : "";

    private static string CacheKey(Firmwares firmware, Version? version) =>
        VehicleKey(firmware) + "|" + VersionText(version);

    private static Indexed Build(Firmwares firmware, Version? version)
    {
        var dir = Settings.GetDataDirectory();
        var candidates = new List<string>();
        if (VersionText(version).Length > 0 && VersionedPrefix(firmware) is { Length: > 0 } prefix)
            candidates.Add(Path.Combine(dir, $"{prefix}{VersionText(version)}.apm.pdef.xml"));
        candidates.Add(Path.Combine(dir, VehicleKey(firmware) + ".apm.pdef.xml"));

        foreach (var file in candidates.Where(File.Exists))
        {
            try
            {
                var index = ParsePdef(file);
                if (index.Count > 0)
                {
                    Log.Info($"parameter metadata: {index.Count} from {file}");
                    return new Indexed(index, file, File.GetLastWriteTimeUtc(file));
                }
            }
            catch (Exception ex)
            {
                Log.Warn("metadata " + file, ex);
            }
        }

        // the legacy format: TOOLS > Parameter documentation > Generate from source (Mission Planner's
        // "Param gen") writes ParameterMetaData.xml to the user data folder; the app ships a backup copy
        foreach (var legacy in LegacyFiles().Where(File.Exists))
        {
            try
            {
                // the generic file may still arrive: its path is the source, so the index is rebuilt when it does
                var index = ParseLegacy(legacy, firmware);
                if (index.Count > 0)
                    return new Indexed(index, Path.Combine(dir, VehicleKey(firmware) + ".apm.pdef.xml"), default);
            }
            catch (Exception ex)
            {
                Log.Warn("metadata " + legacy, ex);
            }
        }

        return new Indexed(new Dictionary<string, ParamMeta>(StringComparer.Ordinal), null, default);
    }

    /// <summary>Mission Planner's ParameterMetaData.xml (generated from the ArduPilot sources), then the shipped backup.</summary>
    public static IEnumerable<string> LegacyFiles() =>
    [
        Settings.GetUserDataDirectory() + GeneratedFileName,
        Path.Combine(Settings.GetRunningDirectory(), "ParameterMetaDataBackup.xml"),
    ];

    /// <summary>The file Mission Planner's "Param gen" writes in the user data folder.</summary>
    public const string GeneratedFileName = "ParameterMetaData.xml";

    /// <summary>Parses apm.pdef.xml: vehicle and library parameters alike.</summary>
    public static Dictionary<string, ParamMeta> ParsePdef(string file)
    {
        var doc = XDocument.Load(file);
        var index = new Dictionary<string, ParamMeta>(StringComparer.Ordinal);
        foreach (var p in doc.Descendants("param"))
        {
            var name = (string?)p.Attribute("name");
            if (string.IsNullOrEmpty(name))
                continue;
            var colon = name.IndexOf(':');
            if (colon >= 0)
                name = name[(colon + 1)..];

            string Field(string field) =>
                p.Elements("field").FirstOrDefault(f => (string?)f.Attribute("name") == field)?.Value.Trim() ?? "";

            var values = p.Element("values")?.Elements("value")
                .Select(v => (Code: (string?)v.Attribute("code"), Label: v.Value.Trim()))
                .Where(v => double.TryParse(v.Code, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                .Select(v => new KeyValuePair<double, string>(double.Parse(v.Code!, CultureInfo.InvariantCulture), v.Label))
                .ToList() ?? [];

            var bits = p.Element("bitmask")?.Elements("bit")
                .Select(b => (Code: (string?)b.Attribute("code"), Label: b.Value.Trim()))
                .Where(b => int.TryParse(b.Code, out _))
                .Select(b => new KeyValuePair<int, string>(int.Parse(b.Code!, CultureInfo.InvariantCulture), b.Label))
                .ToList() ?? [];

            index[name] = Make(name, (string?)p.Attribute("humanName") ?? "", (string?)p.Attribute("documentation") ?? "",
                Field("Units"), Field("Range"), Field("Increment"), values, bits, (string?)p.Attribute("user") ?? "",
                Field("RebootRequired"), Field("ReadOnly"));
        }

        return index;
    }

    /// <summary>Parses the legacy ParameterMetaDataBackup.xml (Params/&lt;vehicle&gt;/&lt;PARAM&gt;/&lt;key&gt;).</summary>
    public static Dictionary<string, ParamMeta> ParseLegacy(string file, Firmwares firmware)
    {
        var doc = XDocument.Load(file);
        var index = new Dictionary<string, ParamMeta>(StringComparer.Ordinal);
        var section = doc.Root?.Element(firmware.ToString()) ?? doc.Root?.Elements().FirstOrDefault();
        if (section == null)
            return index;

        foreach (var p in section.Elements())
        {
            string Get(string key) => p.Element(key)?.Value.Trim() ?? "";
            var values = Get("Values").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(v => v.Split(':', 2))
                .Where(v => v.Length == 2 && double.TryParse(v[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                .Select(v => new KeyValuePair<double, string>(double.Parse(v[0], CultureInfo.InvariantCulture), v[1].Trim()))
                .ToList();
            var bits = Get("Bitmask").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(v => v.Split(':', 2))
                .Where(v => v.Length == 2 && int.TryParse(v[0], out _))
                .Select(v => new KeyValuePair<int, string>(int.Parse(v[0], CultureInfo.InvariantCulture), v[1].Trim()))
                .ToList();
            index[p.Name.LocalName] = Make(p.Name.LocalName, Get("DisplayName"), Get("Description"), Get("Units"), Get("Range"),
                Get("Increment"), values, bits, Get("User"), Get("RebootRequired"), Get("ReadOnly"));
        }

        return index;
    }

    private static ParamMeta Make(string name, string display, string doc, string units, string range, string increment,
        List<KeyValuePair<double, string>> values, List<KeyValuePair<int, string>> bits, string user, string reboot, string readOnly)
    {
        double? min = null, max = null, inc = null;
        var parts = range.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
        {
            min = a;
            max = b;
        }

        if (double.TryParse(increment, NumberStyles.Float, CultureInfo.InvariantCulture, out var i))
            inc = i;

        return new ParamMeta(name, display, doc, units, min, max, inc, values, bits, user,
            string.Equals(reboot, "True", StringComparison.OrdinalIgnoreCase),
            string.Equals(readOnly, "True", StringComparison.OrdinalIgnoreCase));
    }
}
