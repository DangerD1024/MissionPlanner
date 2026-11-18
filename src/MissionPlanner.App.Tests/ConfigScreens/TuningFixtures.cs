using MissionPlanner.App.Core.Services;
using MissionPlanner.ArduPilot;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Tests.ConfigScreens;

/// <summary>
/// Parameter lists dumped from ArduPilot 4.7.2 SITL (Fixtures/*.param): a quad copter, a plane and a
/// QuadPlane (Q_ENABLE 1), to check the tuning pages against what real vehicles have.
/// </summary>
internal static class TuningFixtures
{
    public const string Copter = "copter-sitl.param";
    public const string Plane = "plane-sitl.param";
    public const string QuadPlane = "quadplane-sitl.param";

    private static readonly Dictionary<string, Dictionary<string, double>> Cache = new();

    /// <summary>The fixture's parameters (from the source tree, found above the test binaries).</summary>
    public static Dictionary<string, double> Load(string file)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(file, out var p))
                Cache[file] = p = ParamFile.loadParamFile(Find(file));
            return new Dictionary<string, double>(p, StringComparer.Ordinal);
        }
    }

    private static string Find(string file)
    {
        var relative = Path.Combine("src", "MissionPlanner.App.Tests", "ConfigScreens", "Fixtures", file);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
                return candidate;
        }

        Assert.Inconclusive(relative + " not found above " + AppContext.BaseDirectory);
        return "";
    }

    /// <summary>Puts a fixture into the selected vehicle's list, as a download would, with its firmware.</summary>
    public static void Apply(AppServices services, string file, Firmwares firmware)
    {
        services.Session.Cs.firmware = firmware;
        foreach (var (name, value) in Load(file))
            services.Session.Vehicle.param.Add(new MAVLink.MAVLinkParam(name, value, MAVLink.MAV_PARAM_TYPE.REAL32));
    }
}
