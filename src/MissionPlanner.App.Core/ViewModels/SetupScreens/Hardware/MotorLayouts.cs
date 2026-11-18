using System.Text.Json;
using log4net;
using MissionPlanner.ArduPilot;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;

/// <summary>One motor of a frame, from ArduPilot's AP_Motors test output (APMotorLayout.json).</summary>
public sealed record MotorInfo(int Number, int TestOrder, string Rotation, double Roll, double Pitch)
{
    /// <summary>Motor test letter: test order 1 is A.</summary>
    public string TestLetter => TestLetterFor(TestOrder);

    /// <summary>Position across the frame, right positive (a negative roll factor is a right-hand motor).</summary>
    public double X => -Roll;

    /// <summary>Position along the frame, rear positive (screen down; a positive pitch factor is a front motor).</summary>
    public double Y => -Pitch;

    public bool IsClockwise => string.Equals(Rotation, "CW", StringComparison.OrdinalIgnoreCase);

    public bool IsCounterClockwise => string.Equals(Rotation, "CCW", StringComparison.OrdinalIgnoreCase);

    public bool HasRotation => IsClockwise || IsCounterClockwise;

    public static string TestLetterFor(int testOrder) =>
        testOrder is >= 1 and <= 26 ? ((char)('A' + testOrder - 1)).ToString() : testOrder.ToString();
}

public sealed record MotorLayout(int Class, string ClassName, int Type, string TypeName, IReadOnlyList<MotorInfo> Motors)
{
    /// <summary>The layout ArduPilot uses for any type it has no specific matrix for (Y6, Tri).</summary>
    public bool IsDefaultType => string.Equals(TypeName, "default", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Motor layouts per FRAME_CLASS / FRAME_TYPE, as Mission Planner reads them for Motor Test.</summary>
public sealed class MotorLayoutCatalog
{
    public const string SupportedVersion = "AP_Motors library test ver 1.2";
    public const string FileName = "APMotorLayout.json";

    private static readonly ILog Log = LogManager.GetLogger(typeof(MotorLayoutCatalog));
    private static readonly Lazy<MotorLayoutCatalog> Shipped = new(() => Load(Path.Combine(AppContext.BaseDirectory, FileName)));

    public MotorLayoutCatalog(IReadOnlyList<MotorLayout> layouts) => Layouts = layouts;

    public static MotorLayoutCatalog Empty { get; } = new([]);

    /// <summary>The file shipped next to the executable (empty if missing or unreadable).</summary>
    public static MotorLayoutCatalog Default => Shipped.Value;

    public IReadOnlyList<MotorLayout> Layouts { get; }

    public MotorLayout? Find(int frameClass, int frameType) =>
        Layouts.FirstOrDefault(l => l.Class == frameClass && l.Type == frameType);

    public IEnumerable<MotorLayout> ForClass(int frameClass) => Layouts.Where(l => l.Class == frameClass);

    public static MotorLayoutCatalog Load(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : Empty;
        }
        catch (Exception ex)
        {
            Log.Warn("motor layouts " + path, ex);
            return Empty;
        }
    }

    /// <summary>Parses the file; a different format version gives an empty catalogue, as in Mission Planner.</summary>
    public static MotorLayoutCatalog Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;
        if (!root.TryGetProperty("Version", out var version) || version.GetString() != SupportedVersion)
            return Empty;
        if (!root.TryGetProperty("layouts", out var layouts) || layouts.ValueKind != JsonValueKind.Array)
            return Empty;

        var list = new List<MotorLayout>();
        foreach (var l in layouts.EnumerateArray())
        {
            var motors = new List<MotorInfo>();
            if (l.TryGetProperty("motors", out var ms) && ms.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in ms.EnumerateArray())
                {
                    motors.Add(new MotorInfo(
                        Int(m, "Number"),
                        Int(m, "TestOrder"),
                        Str(m, "Rotation"),
                        Num(m, "Roll"),
                        Num(m, "Pitch")));
                }
            }

            list.Add(new MotorLayout(Int(l, "Class"), Str(l, "ClassName"), Int(l, "Type"), Str(l, "TypeName"), motors));
        }

        return new MotorLayoutCatalog(list);
    }

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}

/// <summary>What Motor Test drives: the layout if known, otherwise just a motor count.</summary>
public sealed record MotorTestPlan(bool Enabled, int MotorCount, MotorLayout? Layout, string? ClassParam, string? TypeParam);

public static class MotorTestLogic
{
    /// <summary>
    /// Mission Planner's motor count rules (ConfigMotorTest.get_motormax): rovers and boats have 4;
    /// otherwise the FRAME_CLASS/FRAME_TYPE (or Q_ for QuadPlanes) layout; otherwise a count by
    /// vehicle type. The page is disabled without FRAME, FRAME_TYPE or Q_FRAME_TYPE.
    /// </summary>
    public static MotorTestPlan Resolve(Func<string, double?> param, MAVLink.MAV_TYPE vehicleType, MotorLayoutCatalog layouts)
    {
        if (vehicleType is MAVLink.MAV_TYPE.GROUND_ROVER or MAVLink.MAV_TYPE.SURFACE_BOAT)
            return new MotorTestPlan(true, 4, null, null, null);

        bool Has(string n) => param(n) != null;

        if (!Has("FRAME") && !Has("Q_FRAME_TYPE") && !Has("FRAME_TYPE"))
            return new MotorTestPlan(false, 0, null, null, null);

        // first pair present wins, found layout or not (as in Mission Planner)
        foreach (var (cls, typ) in new[] { ("FRAME_CLASS", "FRAME_TYPE"), ("Q_FRAME_CLASS", "Q_FRAME_TYPE") })
        {
            if (param(cls) is not { } c || param(typ) is not { } t)
                continue;
            var layout = layouts.Find((int)c, (int)t);
            if (layout is { Motors.Count: > 0 })
                return new MotorTestPlan(true, layout.Motors.Count, layout, cls, typ);
            return new MotorTestPlan(true, CountFor(FallbackType(param, vehicleType)), null, cls, typ);
        }

        return new MotorTestPlan(true, CountFor(FallbackType(param, vehicleType)), null, null, null);
    }

    private static MAVLink.MAV_TYPE FallbackType(Func<string, double?> param, MAVLink.MAV_TYPE vehicleType)
    {
        if (param("Q_FRAME_CLASS") is { } q)
        {
            return (int)q switch
            {
                0 or 1 => MAVLink.MAV_TYPE.QUADROTOR,
                2 or 5 => MAVLink.MAV_TYPE.HEXAROTOR,
                3 or 4 => MAVLink.MAV_TYPE.OCTOROTOR,
                6 => MAVLink.MAV_TYPE.HELICOPTER,
                7 => MAVLink.MAV_TYPE.TRICOPTER,
                _ => MAVLink.MAV_TYPE.QUADROTOR,
            };
        }

        return param("FRAME") != null || param("FRAME_TYPE") != null ? vehicleType : MAVLink.MAV_TYPE.QUADROTOR;
    }

    /// <summary>Motor count by vehicle type when no layout is known.</summary>
    public static int CountFor(MAVLink.MAV_TYPE type) => type switch
    {
        MAVLink.MAV_TYPE.TRICOPTER => 4,
        MAVLink.MAV_TYPE.QUADROTOR => 4,
        MAVLink.MAV_TYPE.HEXAROTOR => 6,
        MAVLink.MAV_TYPE.OCTOROTOR => 8,
        MAVLink.MAV_TYPE.HELICOPTER => 0,
        MAVLink.MAV_TYPE.DECAROTOR => 10,
        MAVLink.MAV_TYPE.DODECAROTOR => 12,
        _ => 8,
    };
}

/// <summary>Frame classes and types for the Frame Type page.</summary>
public static class FrameCatalog
{
    /// <summary>
    /// Types worth offering for a multirotor class: ArduPilot.Common.ValidList plus the specific
    /// layouts in APMotorLayout.json. Classes that ignore FRAME_TYPE (heli, tri, single...) get none.
    /// </summary>
    public static IReadOnlyList<int> ValidTypes(int frameClass, MotorLayoutCatalog layouts)
    {
        var listed = Common.ValidList.Where(v => (int)v.Item1 == frameClass).ToList();
        var types = new SortedSet<int>(listed.Where(v => v.Item2 != null).Select(v => (int)v.Item2!.Value));
        if (listed.Count > 0 && types.Count == 0)
            return [];
        foreach (var l in layouts.ForClass(frameClass).Where(l => !l.IsDefaultType))
            types.Add(l.Type);
        return types.ToList();
    }

    /// <summary>The layout to draw for a class and type (falling back to the class's default matrix).</summary>
    public static MotorLayout? Preview(int frameClass, int frameType, MotorLayoutCatalog layouts) =>
        layouts.Find(frameClass, frameType) ?? layouts.ForClass(frameClass).FirstOrDefault(l => l.IsDefaultType);

    /// <summary>The layout that represents a class on its tile: X if it has one, else its first.</summary>
    public static MotorLayout? ClassPreview(int frameClass, MotorLayoutCatalog layouts) =>
        layouts.Find(frameClass, (int)motor_frame_type.MOTOR_FRAME_TYPE_X) ?? layouts.ForClass(frameClass).FirstOrDefault();

    /// <summary>Picks the type to write with a newly chosen class: keep the current one if valid, else X, else the first.</summary>
    public static int TypeForClass(int frameClass, int currentType, MotorLayoutCatalog layouts)
    {
        var valid = ValidTypes(frameClass, layouts);
        if (valid.Count == 0 || valid.Contains(currentType))
            return currentType;
        return valid.Contains((int)motor_frame_type.MOTOR_FRAME_TYPE_X) ? (int)motor_frame_type.MOTOR_FRAME_TYPE_X : valid[0];
    }

    public static string ClassName(int frameClass) => Enum.IsDefined(typeof(motor_frame_class), frameClass)
        ? frameClass switch
        {
            0 => "Undefined",
            1 => "Quad",
            2 => "Hexa",
            3 => "Octa",
            4 => "OctaQuad",
            5 => "Y6",
            6 => "Heli",
            7 => "Tri",
            8 => "SingleCopter",
            9 => "CoaxCopter",
            10 => "Tailsitter",
            11 => "Heli Dual",
            12 => "DodecaHexa",
            13 => "HeliQuad",
            _ => frameClass.ToString(),
        }
        : frameClass == 14 ? "Deca" : "Class " + frameClass;

    public static string TypeName(int frameType) => frameType switch
    {
        0 => "Plus",
        1 => "X",
        2 => "V",
        3 => "H",
        4 => "V-Tail",
        5 => "A-Tail",
        6 => "Plus Reversed",
        10 => "Y6B",
        11 => "Y6F",
        12 => "BetaFlight X",
        13 => "DJI X",
        14 => "Clockwise X",
        15 => "I",
        16 => "Plus (no yaw torque)",
        17 => "X (no yaw torque)",
        18 => "BetaFlight X Reversed",
        19 => "Y4",
        _ => "Type " + frameType,
    };

    /// <summary>The legacy single FRAME parameter (Copter before 3.5): value, label and the layout that pictures it.</summary>
    public static IReadOnlyList<(int Value, string Label, int LayoutClass, int LayoutType)> LegacyFrames { get; } =
    [
        ((int)Frame.Plus, "Plus", 1, 0),
        ((int)Frame.X, "X", 1, 1),
        ((int)Frame.V, "V", 1, 2),
        ((int)Frame.H, "H", 1, 3),
        ((int)Frame.VTail, "V-Tail", 1, 4),
        ((int)Frame.Y, "Y6", 5, 10),
    ];
}
