using System.Globalization;
using System.Xml.Linq;
using log4net;
using MissionPlanner.ArduPilot;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>A parameter a slider also sets, to the slider's value times <see cref="Multiplier"/>.</summary>
public sealed record TuningRelation(string Name, double Multiplier);

/// <summary>
/// A Basic Tuning slider. Sliders with the same <see cref="Title"/> are alternatives for different
/// firmware versions: the first whose parameter exists is shown. <see cref="Min"/>/<see cref="Max"/>
/// apply when the parameter documentation has no range.
/// </summary>
public sealed record TuningSliderDef(
    string Title,
    string Description,
    string Name,
    double Min,
    double Max,
    IReadOnlyList<TuningRelation> Relations,
    bool Reversed = false);

/// <summary>
/// Copter Basic Tuning sliders: Mission Planner's ConfigSimplePids reads <c>acsimplepids.xml</c>
/// from next to the executable and shows each slider whose parameter exists. Its names are those of
/// older copters, so the same sliders under the names current firmware uses follow them.
/// </summary>
public static class TuningSimplePids
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(TuningSimplePids));

    public const string FileName = "acsimplepids.xml";

    /// <summary>The note under Mission Planner's sliders.</summary>
    public const string Note = "NOTE: using this interface may reset some of your custom pids.";

    private const string Feel = "Slide to the left for softer response to user RC input or slide to the right for crisper response.";
    private const string Sensitivity = "Slide to the right if the copter is sluggish or slide to the left if the copter is twitchy.";
    private const string Climb = "Slide to the right to climb more aggressively or slide to the left to climb more gently.";

    private static TuningRelation[] Rel(params (string Name, double Multiplier)[] r) =>
        r.Select(x => new TuningRelation(x.Name, x.Multiplier)).ToArray();

    /// <summary>The content of the acsimplepids.xml Mission Planner ships, used when the file is missing.</summary>
    public static IReadOnlyList<TuningSliderDef> CopterXmlDefaults { get; } =
    [
        new("RC Feel Roll/Pitch", Feel, "RC_FEEL_RP", 0, 100, []),
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
        new("RC Feel Roll/Pitch", Feel, "ATC_INPUT_TC", 0, 1, [], Reversed: true),
        new("Throttle Hover", "How much throttle is needed to maintain a steady hover.", "MOT_THST_HOVER", 0.125, 0.6875, []),
        // Copter 4.7 renamed PSC_ACCZ to PSC_D_ACC and made its gains 10x smaller; I stays at twice P
        new("Climb Sensitivity", Climb, "PSC_D_ACC_P", 0.02, 0.15, Rel(("PSC_D_ACC_I", 2))),
        new("Climb Sensitivity", Climb, "PSC_ACCZ_P", 0.2, 1.5, Rel(("PSC_ACCZ_I", 2))),
    ];

    /// <summary>The copter sliders: the xml (when present, else its built-in copy) followed by the current names.</summary>
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

    /// <summary>The sliders for a vehicle, before matching against its parameters (copters only).</summary>
    public static IReadOnlyList<TuningSliderDef> For(Firmwares firmware, string? xmlPath) =>
        firmware == Firmwares.ArduCopter2 ? Copter(xmlPath) : [];

    /// <summary>
    /// The sliders this vehicle has: per title, the first definition whose parameter exists. Titles
    /// keep the order of their first definition.
    /// </summary>
    public static IReadOnlyList<TuningSliderDef> Resolve(IEnumerable<TuningSliderDef> defs, Func<string, bool> has)
    {
        var slots = new List<string>();
        var chosen = new Dictionary<string, TuningSliderDef>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in defs)
        {
            if (!slots.Contains(d.Title))
                slots.Add(d.Title);
            if (chosen.ContainsKey(d.Title) || used.Contains(d.Name) || !has(d.Name))
                continue;
            chosen[d.Title] = d;
            used.Add(d.Name);
        }

        return slots.Where(chosen.ContainsKey).Select(s => chosen[s]).ToList();
    }

    /// <summary>
    /// The values a slider writes, as ConfigSimplePids does: the parameter itself and each related
    /// parameter the vehicle has at value × multiplier.
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
