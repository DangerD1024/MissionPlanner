using System.Globalization;
using System.Xml.Linq;
using log4net;
using MissionPlanner.ArduPilot;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>A parameter a slider also sets, to the slider's value times <see cref="Multiplier"/>.</summary>
public sealed record TuningRelation(string Name, double Multiplier);

/// <summary>
/// A Basic Tuning slider. Sliders with the same <see cref="Section"/> and <see cref="Title"/> are
/// alternatives for different firmware versions: the first whose parameter exists is shown.
/// <see cref="Min"/>/<see cref="Max"/> apply when the parameter documentation has no range.
/// </summary>
public sealed record TuningSliderDef(
    string Title,
    string Description,
    string Name,
    double Min,
    double Max,
    IReadOnlyList<TuningRelation> Relations,
    string Section = "",
    bool Reversed = false)
{
    /// <summary>The slider slot, shared by a parameter's old and new names.</summary>
    public string Slot => Section + "|" + Title;
}

/// <summary>
/// Basic Tuning sliders: for copters Mission Planner's <c>acsimplepids.xml</c> (read from next to the
/// executable, as ConfigSimplePids does), completed with the names current firmware uses; for
/// planes and rovers the main gains.
/// </summary>
public static class TuningSimplePids
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(TuningSimplePids));

    public const string FileName = "acsimplepids.xml";

    private static readonly string Sensitivity = "Slide to the right if the copter is sluggish or slide to the left if the copter is twitchy.";
    private static readonly string Climb = "Slide to the right to climb more aggressively or slide to the left to climb more gently.";

    private static TuningRelation[] Rel(params (string Name, double Multiplier)[] r) =>
        r.Select(x => new TuningRelation(x.Name, x.Multiplier)).ToArray();

    /// <summary>The content of the acsimplepids.xml Mission Planner ships, used when the file is missing.</summary>
    public static IReadOnlyList<TuningSliderDef> CopterXmlDefaults { get; } =
    [
        new("RC Feel Roll/Pitch", "Slide to the left for softer response to user RC input or slide to the right for crisper response.",
            "RC_FEEL_RP", 0, 100, []),
        new("Roll/Pitch Sensitivity", Sensitivity, "RATE_RLL_P", 0.08, 0.4,
            Rel(("RATE_RLL_I", 1), ("RATE_PIT_P", 1), ("RATE_PIT_I", 1))),
        new("Roll/Pitch Sensitivity", Sensitivity, "ATC_RAT_RLL_P", 0.08, 0.4,
            Rel(("ATC_RAT_RLL_I", 1), ("ATC_RAT_PIT_P", 1), ("ATC_RAT_PIT_I", 1))),
        new("Throttle Hover", "How much throttle is needed to maintain a steady hover.", "THR_MID", 200, 800, []),
        new("Climb Sensitivity", Climb, "THR_ACCEL_P", 0.3, 1, Rel(("THR_ACCEL_I", 2))),
        new("Climb Sensitivity", Climb, "ACCEL_Z_P", 0.3, 1, Rel(("ACCEL_Z_I", 2))),
    ];

    /// <summary>The same sliders under the names newer copter firmware uses (not in the xml).</summary>
    public static IReadOnlyList<TuningSliderDef> CopterCurrent { get; } =
    [
        // the input time constant replaced RC_FEEL_RP: smaller is crisper, so the slider runs backwards
        new("RC Feel Roll/Pitch", "Slide to the left for softer response to user RC input or slide to the right for crisper response.",
            "ATC_INPUT_TC", 0, 1, [], Reversed: true),
        new("Throttle Hover", "How much throttle is needed to maintain a steady hover (learnt in flight when MOT_HOVER_LEARN is on).",
            "MOT_THST_HOVER", 0.125, 0.6875, []),
        new("Climb Sensitivity", Climb, "PSC_D_ACC_P", 0.02, 0.15, Rel(("PSC_D_ACC_I", 2))),
        new("Climb Sensitivity", Climb, "PSC_ACCZ_P", 0.2, 1.5, Rel(("PSC_ACCZ_I", 2))),
    ];

    public const string RollSection = "Roll Rate";
    public const string PitchSection = "Pitch Rate";
    public const string NavigationSection = "Navigation and Speed";
    public const string SteeringSection = "Steering";
    public const string SpeedSection = "Speed and Throttle";

    private static IEnumerable<TuningSliderDef> PlaneAxis(string section, string axis, string current, string legacy) =>
    [
        new("Feed-forward", $"The main {axis} response: slide to the right if the plane is slow to reach the {axis} rate it is asked for.",
            current + "_FF", 0, 3, [], section),
        new("Feed-forward", $"The main {axis} response: slide to the right if the plane is slow to reach the {axis} rate it is asked for.",
            legacy + "_FF", 0, 3, [], section),
        new("P", $"Corrects {axis} rate errors: slide to the right for a firmer response, to the left if it oscillates.",
            current + "_P", 0, 1, [], section),
        new("P", $"Corrects {axis} rate errors: slide to the right for a firmer response, to the left if it oscillates.",
            legacy + "_P", 0, 1, [], section),
        new("I", $"Removes steady {axis} errors such as an out-of-trim airframe.", current + "_I", 0, 1, [], section),
        new("I", $"Removes steady {axis} errors such as an out-of-trim airframe.", legacy + "_I", 0, 1, [], section),
        new("D", $"Damps quick {axis} changes: slide to the left if the plane oscillates quickly.", current + "_D", 0, 0.1, [], section),
        new("D", $"Damps quick {axis} changes: slide to the left if the plane oscillates quickly.", legacy + "_D", 0, 0.1, [], section),
    ];

    public static IReadOnlyList<TuningSliderDef> Plane { get; } =
    [
        .. PlaneAxis(RollSection, "roll", "RLL_RATE", "RLL2SRV"),
        .. PlaneAxis(PitchSection, "pitch", "PTCH_RATE", "PTCH2SRV"),
        new("Turn Sharpness (L1 Period)", "Slide to the left for tighter, more aggressive turns or to the right for gentler ones.",
            "NAVL1_PERIOD", 1, 60, [], NavigationSection),
        new("Height and Speed Response (TECS Time Constant)", "Slide to the left for quicker height and speed corrections or to the right for smoother ones.",
            "TECS_TIME_CONST", 3, 10, [], NavigationSection),
        new("Cruise Throttle", "The throttle needed to hold cruise airspeed in level flight.", "TRIM_THROTTLE", 0, 100, [], NavigationSection),
        new("Cruise Airspeed", "The airspeed to fly at in automatic modes.", "AIRSPEED_CRUISE", 5, 50, [], NavigationSection),
    ];

    public static IReadOnlyList<TuningSliderDef> Rover { get; } =
    [
        new("Steering Feed-forward", "The main steering response: slide to the right if the rover turns more slowly than asked.",
            "ATC_STR_RAT_FF", 0, 3, [], SteeringSection),
        new("Steering P", "Corrects turn rate errors: slide to the right for a firmer response, to the left if it weaves.",
            "ATC_STR_RAT_P", 0, 2, [], SteeringSection),
        new("Steering P", "Corrects turn rate errors: slide to the right for a firmer response, to the left if it weaves.",
            "STEER2SRV_P", 0, 2, [], SteeringSection),
        new("Steering I", "Removes steady turn rate errors.", "ATC_STR_RAT_I", 0, 2, [], SteeringSection),
        new("Steering I", "Removes steady turn rate errors.", "STEER2SRV_I", 0, 2, [], SteeringSection),
        new("Steering Angle P", "How quickly heading errors are turned into turn rate.", "ATC_STR_ANG_P", 1, 10, [], SteeringSection),
        new("Speed P", "Corrects speed errors: slide to the right for a firmer response, to the left if the speed surges.",
            "ATC_SPEED_P", 0, 2, [], SpeedSection),
        new("Speed P", "Corrects speed errors: slide to the right for a firmer response, to the left if the speed surges.",
            "SPEED2THR_P", 0, 2, [], SpeedSection),
        new("Speed I", "Removes steady speed errors.", "ATC_SPEED_I", 0, 2, [], SpeedSection),
        new("Speed I", "Removes steady speed errors.", "SPEED2THR_I", 0, 2, [], SpeedSection),
        new("Cruise Speed", "The speed measured at the cruise throttle.", "CRUISE_SPEED", 0, 20, [], SpeedSection),
        new("Cruise Throttle", "The throttle that gives the cruise speed.", "CRUISE_THROTTLE", 0, 100, [], SpeedSection),
        new("Waypoint Speed", "The speed to drive at in automatic modes.", "WP_SPEED", 0, 20, [], SpeedSection),
        new("Turn Sharpness (L1 Period)", "Slide to the left for tighter turns or to the right for gentler ones.", "NAVL1_PERIOD", 1, 60, [], SpeedSection),
    ];

    /// <summary>The copter sliders for the multicopter half of a QuadPlane (Q_ names).</summary>
    public static IReadOnlyList<TuningSliderDef> QuadPlane { get; } = [.. CopterXmlDefaults.Concat(CopterCurrent).Select(ToQuadPlane).OfType<TuningSliderDef>()];

    private static TuningSliderDef? ToQuadPlane(TuningSliderDef def)
    {
        if (TuningLayouts.QuadPlaneName(def.Name) is not { } name)
            return null;
        var relations = def.Relations
            .Select(r => TuningLayouts.QuadPlaneName(r.Name) is { } n ? new TuningRelation(n, r.Multiplier) : null)
            .OfType<TuningRelation>()
            .ToList();
        return def with
        {
            Name = name,
            Relations = relations,
            Section = TuningLayouts.QuadPlaneSection,
            Description = def.Description.Replace("the copter", "it", StringComparison.Ordinal),
        };
    }

    /// <summary>The copter sliders: the xml (when present) followed by the current names.</summary>
    public static IReadOnlyList<TuningSliderDef> Copter(string? xmlPath)
    {
        IReadOnlyList<TuningSliderDef> xml = CopterXmlDefaults;
        if (xmlPath != null && File.Exists(xmlPath))
        {
            try
            {
                xml = Load(xmlPath);
            }
            catch (Exception ex)
            {
                Log.Warn("reading " + xmlPath, ex);
            }
        }

        return [.. xml, .. CopterCurrent];
    }

    /// <summary>acsimplepids.xml next to the executable.</summary>
    public static string DefaultXmlPath => Path.Combine(Settings.GetRunningDirectory(), FileName);

    /// <summary>The sliders for a vehicle, before matching against its parameters.</summary>
    public static IReadOnlyList<TuningSliderDef> For(Firmwares firmware, Func<string, double?> get, string? xmlPath) => firmware switch
    {
        Firmwares.ArduCopter2 => Copter(xmlPath),
        Firmwares.ArduPlane => TuningLayouts.QuadPlaneEnabled(get) ? [.. Plane, .. QuadPlane] : Plane,
        Firmwares.ArduRover => Rover,
        _ => [],
    };

    /// <summary>
    /// The sliders this vehicle has: per slot, the first definition whose parameter exists. Slots
    /// keep the order of their first definition.
    /// </summary>
    public static IReadOnlyList<TuningSliderDef> Resolve(IEnumerable<TuningSliderDef> defs, Func<string, bool> has)
    {
        var slots = new List<string>();
        var chosen = new Dictionary<string, TuningSliderDef>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in defs)
        {
            if (!slots.Contains(d.Slot))
                slots.Add(d.Slot);
            if (chosen.ContainsKey(d.Slot) || used.Contains(d.Name) || !has(d.Name))
                continue;
            chosen[d.Slot] = d;
            used.Add(d.Name);
        }

        return slots.Where(chosen.ContainsKey).Select(s => chosen[s]).ToList();
    }

    /// <summary>
    /// The values a slider position writes, as ConfigSimplePids does: the parameter itself and each
    /// related parameter the vehicle has at value × multiplier.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, double>> Values(TuningSliderDef def, double value, Func<string, bool> has)
    {
        var list = new List<KeyValuePair<string, double>> { new(def.Name, value) };
        foreach (var r in def.Relations)
        {
            if (has(r.Name))
                list.Add(new KeyValuePair<string, double>(r.Name, Linked(value, r.Multiplier)));
        }

        return list;
    }

    /// <summary>value × multiplier at the precision the vehicle stores (no 0.30000000000000004).</summary>
    public static double Linked(double value, double multiplier) => Clean(value * multiplier);

    internal static double Clean(double v) =>
        double.IsFinite(v) && Math.Abs(v) < 7.9e27 ? (double)(decimal)(float)v : v;

    /// <summary>
    /// Reads acsimplepids.xml: <c>simple/ac/param</c> elements with title, desc, name, min, max and
    /// any number of <c>relation</c> (param, multiplier).
    /// </summary>
    public static IReadOnlyList<TuningSliderDef> Load(string path) => Parse(XDocument.Load(path));

    public static IReadOnlyList<TuningSliderDef> Parse(XDocument doc)
    {
        var list = new List<TuningSliderDef>();
        foreach (var p in doc.Descendants("ac").Elements("param"))
        {
            string Text(string element) => p.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(element, StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? "";

            var name = Text("name");
            if (name.Length == 0)
                continue;

            double Number(string element, double fallback) =>
                double.TryParse(Text(element), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

            var relations = p.Elements("relation")
                .Select(r => (Name: r.Element("param")?.Value.Trim() ?? "", Mult: r.Element("multiplier")?.Value.Trim() ?? ""))
                .Where(r => r.Name.Length > 0 && double.TryParse(r.Mult, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                .Select(r => new TuningRelation(r.Name, double.Parse(r.Mult, CultureInfo.InvariantCulture)))
                .ToList();

            list.Add(new TuningSliderDef(Text("title").TrimEnd(':', ' '), Text("desc"), name,
                Number("min", 0), Number("max", 1), relations));
        }

        return list;
    }
}
