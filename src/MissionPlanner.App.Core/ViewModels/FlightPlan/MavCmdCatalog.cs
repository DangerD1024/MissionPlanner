using System.Xml.Linq;
using MissionPlanner.ArduPilot;

namespace MissionPlanner.App.Core.ViewModels.FlightPlan;

/// <summary>Vehicle section of mavcmd.xml.</summary>
public enum MavCmdSection
{
    /// <summary><c>AC2</c>: copter, and the default for every other firmware.</summary>
    Copter,

    /// <summary><c>APM</c>: plane (and Ateryx).</summary>
    Plane,

    /// <summary><c>APRover</c>: rover and boat.</summary>
    Rover,
}

/// <summary>One command of a mavcmd.xml section: the labels of its seven values (P1-P4, X=lat, Y=long, Z=alt).</summary>
public sealed class MavCmdInfo
{
    public MavCmdInfo(string name, MAVLink.MAV_CMD? command, string[] labels, string?[] unitTypes)
    {
        Name = name;
        Command = command;
        Labels = labels;
        UnitTypes = unitTypes;
    }

    /// <summary>Element name, normally a MAV_CMD member.</summary>
    public string Name { get; }

    /// <summary>The MAV_CMD the name stands for; null for a name the MAVLink library does not know.</summary>
    public MAVLink.MAV_CMD? Command { get; }

    /// <summary>The raw labels, empty where the value is unused. Always seven.</summary>
    public string[] Labels { get; }

    /// <summary><c>alt</c>, <c>speed</c>, <c>dist</c> or null, per value.</summary>
    public string?[] UnitTypes { get; }
}

/// <summary>
/// Column labels for mission items, read from Mission Planner's <c>mavcmd.xml</c> (the file next to
/// the executable). Replaces <c>FlightPlanner.readCMDXML</c> / <c>updateCMDParams</c>: one section per
/// vehicle type, and fixed labels for fence and rally items. Values are shown in metres.
/// </summary>
public sealed class MavCmdCatalog
{
    public const int ValueCount = 7;
    public const string FileName = "mavcmd.xml";

    /// <summary>Headers for a command the catalog does not describe.</summary>
    public static readonly IReadOnlyList<string> GenericHeaders = ["P1", "P2", "P3", "P4", "Lat", "Long", "Alt (m)"];

    public static readonly IReadOnlyList<MAVLink.MAV_CMD> FenceCommands =
    [
        MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION,
        MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_EXCLUSION,
        MAVLink.MAV_CMD.FENCE_CIRCLE_INCLUSION,
        MAVLink.MAV_CMD.FENCE_CIRCLE_EXCLUSION,
        MAVLink.MAV_CMD.FENCE_RETURN_POINT,
        MAVLink.MAV_CMD.FENCE_HOME_CIRCLE_INCLUSION,
    ];

    public static readonly IReadOnlyList<MAVLink.MAV_CMD> RallyCommands = [MAVLink.MAV_CMD.RALLY_POINT];

    /// <summary>Used when mavcmd.xml cannot be found or read: the common navigation commands, generic labels.</summary>
    private static readonly MAVLink.MAV_CMD[] FallbackCommands =
    [
        MAVLink.MAV_CMD.WAYPOINT, MAVLink.MAV_CMD.SPLINE_WAYPOINT, MAVLink.MAV_CMD.TAKEOFF, MAVLink.MAV_CMD.LAND,
        MAVLink.MAV_CMD.RETURN_TO_LAUNCH, MAVLink.MAV_CMD.LOITER_UNLIM, MAVLink.MAV_CMD.LOITER_TURNS,
        MAVLink.MAV_CMD.LOITER_TIME, MAVLink.MAV_CMD.DELAY, MAVLink.MAV_CMD.DO_JUMP, MAVLink.MAV_CMD.DO_CHANGE_SPEED,
        MAVLink.MAV_CMD.DO_SET_ROI, MAVLink.MAV_CMD.DO_SET_SERVO, MAVLink.MAV_CMD.DO_SET_RELAY,
        MAVLink.MAV_CMD.DO_SET_CAM_TRIGG_DIST, MAVLink.MAV_CMD.DO_DIGICAM_CONTROL, MAVLink.MAV_CMD.CONDITION_DELAY,
        MAVLink.MAV_CMD.CONDITION_YAW,
    ];

    private readonly Dictionary<string, MavCmdInfo> _byName;
    private readonly Dictionary<MAVLink.MAV_CMD, MavCmdInfo> _byCommand = new();

    private MavCmdCatalog(MavCmdSection section, List<MavCmdInfo> commands, string? source)
    {
        Section = section;
        Commands = commands;
        Source = source;
        _byName = new Dictionary<string, MavCmdInfo>(StringComparer.Ordinal);
        foreach (var c in commands)
        {
            _byName.TryAdd(c.Name, c);
            if (c.Command is { } cmd)
                _byCommand.TryAdd(cmd, c);
        }

        MissionCommands = commands.Where(c => c.Command != null).Select(c => c.Command!.Value).Distinct().ToArray();
    }

    public MavCmdSection Section { get; }

    /// <summary>Every command of the section, in file order.</summary>
    public IReadOnlyList<MavCmdInfo> Commands { get; }

    /// <summary>The section's commands that are MAV_CMD members, in file order: the mission command list.</summary>
    public IReadOnlyList<MAVLink.MAV_CMD> MissionCommands { get; }

    /// <summary>Where the catalog came from; null for the built-in fallback.</summary>
    public string? Source { get; }

    /// <summary>The section Mission Planner uses for a firmware.</summary>
    public static MavCmdSection SectionFor(Firmwares firmware) => firmware switch
    {
        Firmwares.ArduPlane or Firmwares.Ateryx => MavCmdSection.Plane,
        Firmwares.ArduRover => MavCmdSection.Rover,
        _ => MavCmdSection.Copter,
    };

    public static string ElementName(MavCmdSection section) => section switch
    {
        MavCmdSection.Plane => "APM",
        MavCmdSection.Rover => "APRover",
        _ => "AC2",
    };

    /// <summary>Reads one section of a mavcmd.xml document.</summary>
    public static MavCmdCatalog Parse(Stream xml, MavCmdSection section, string? source = null)
    {
        var doc = XDocument.Load(xml);
        var root = doc.Root ?? throw new InvalidDataException("mavcmd.xml has no root element");
        var element = root.Element(ElementName(section))
                      ?? throw new InvalidDataException($"mavcmd.xml has no <{ElementName(section)}> section");

        var list = new List<MavCmdInfo>();
        foreach (var cmd in element.Elements())
        {
            var labels = new string[ValueCount];
            var units = new string?[ValueCount];
            var i = 0;
            foreach (var value in cmd.Elements())
            {
                if (i >= ValueCount)
                    break;
                labels[i] = value.Value.Trim();
                units[i] = (string?)value.Attribute("unitType");

                // a mavcmd.xml from before the unitType attribute: Z "Alt" is an altitude
                if (units[i] == null && i == 6 && labels[i] == "Alt")
                    units[i] = "alt";
                i++;
            }

            for (; i < ValueCount; i++)
                labels[i] = "";

            var name = cmd.Name.LocalName;
            MAVLink.MAV_CMD? id = Enum.TryParse<MAVLink.MAV_CMD>(name, ignoreCase: false, out var parsed) &&
                                  Enum.IsDefined(parsed)
                ? parsed
                : null;
            list.Add(new MavCmdInfo(name, id, labels, units));
        }

        return new MavCmdCatalog(section, list, source);
    }

    public static MavCmdCatalog Parse(string path, MavCmdSection section)
    {
        using var stream = File.OpenRead(path);
        return Parse(stream, section, path);
    }

    /// <summary>The section for a firmware from mavcmd.xml next to the executable, or a built-in fallback.</summary>
    public static MavCmdCatalog Load(Firmwares firmware) => Load(SectionFor(firmware));

    public static MavCmdCatalog Load(MavCmdSection section, string? path = null)
    {
        path ??= FindFile();
        if (path != null)
        {
            try
            {
                return Parse(path, section);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException or UnauthorizedAccessException)
            {
                // fall through to the built-in list
            }
        }

        return Fallback(section);
    }

    public static MavCmdCatalog Fallback(MavCmdSection section) =>
        new(section,
            FallbackCommands.Select(c =>
            {
                var labels = GenericLabels(c);
                var units = new string?[ValueCount];
                if (labels[6].Length > 0)
                    units[6] = "alt";
                return new MavCmdInfo(c.ToString(), c, labels, units);
            }).ToList(),
            null);

    /// <summary>mavcmd.xml next to the executable (it is copied there by the build).</summary>
    public static string? FindFile()
    {
        foreach (var dir in new[] { AppContext.BaseDirectory, MissionPlanner.Utilities.Settings.GetRunningDirectory() })
        {
            if (string.IsNullOrEmpty(dir))
                continue;
            var path = Path.Combine(dir, FileName);
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    public MavCmdInfo? Find(MAVLink.MAV_CMD command) => _byCommand.GetValueOrDefault(command);

    public MavCmdInfo? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>Commands offered for a list: the section's for a mission, the fence or rally commands otherwise.</summary>
    public IReadOnlyList<MAVLink.MAV_CMD> CommandsFor(MAVLink.MAV_MISSION_TYPE type) => type switch
    {
        MAVLink.MAV_MISSION_TYPE.FENCE => FenceCommands,
        MAVLink.MAV_MISSION_TYPE.RALLY => RallyCommands,
        _ => MissionCommands,
    };

    /// <summary>
    /// The seven column headers (P1, P2, P3, P4, Lat, Long, Alt) for a command in a list, units
    /// appended. An unused value has an empty header.
    /// </summary>
    public string[] HeadersFor(MAVLink.MAV_CMD command, MAVLink.MAV_MISSION_TYPE type = MAVLink.MAV_MISSION_TYPE.MISSION)
    {
        if (type == MAVLink.MAV_MISSION_TYPE.FENCE)
            return FenceHeaders(command);
        if (type == MAVLink.MAV_MISSION_TYPE.RALLY)
            return ["", "", "", "", "Lat", "Long", "Alt (m)"];

        if (Find(command) is not { } info)
        {
            // fence and rally commands keep their meaning in any list
            if (FenceCommands.Contains(command))
                return FenceHeaders(command);
            if (command == MAVLink.MAV_CMD.RALLY_POINT)
                return ["", "", "", "", "Lat", "Long", "Alt (m)"];
            return GenericHeaders.ToArray();
        }

        var headers = new string[ValueCount];
        for (var i = 0; i < ValueCount; i++)
            headers[i] = WithUnit(info.Labels[i], info.UnitTypes[i]);
        return headers;
    }

    public static string UnitLabel(string? unitType) => unitType switch
    {
        "alt" => "m",
        "dist" => "m",
        "speed" => "m/s",
        _ => "",
    };

    private static string WithUnit(string label, string? unitType)
    {
        var unit = UnitLabel(unitType);
        return label.Length == 0 || unit.Length == 0 ? label : $"{label} ({unit})";
    }

    private static string[] FenceHeaders(MAVLink.MAV_CMD command) => command switch
    {
        MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION or MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_EXCLUSION =>
            ["Points", "", "", "", "Lat", "Long", ""],
        MAVLink.MAV_CMD.FENCE_CIRCLE_INCLUSION or MAVLink.MAV_CMD.FENCE_CIRCLE_EXCLUSION =>
            ["Radius (m)", "", "", "", "Lat", "Long", ""],
        // centred on home, so lat/lng are not used
        MAVLink.MAV_CMD.FENCE_HOME_CIRCLE_INCLUSION => ["Radius (m)", "", "", "", "", "", ""],
        _ => ["", "", "", "", "Lat", "Long", ""],
    };

    private static string[] GenericLabels(MAVLink.MAV_CMD command) =>
        MissionPlanner.Utilities.Locationwp.isLocationCommand((ushort)command)
            ? ["P1", "P2", "P3", "P4", "Lat", "Long", "Alt"]
            : ["P1", "P2", "P3", "P4", "", "", ""];
}
