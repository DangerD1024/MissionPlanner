using MissionPlanner.ArduPilot;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>
/// One value on a tuning card: Mission Planner's label and the names the parameter has had across
/// firmware versions. The first name the vehicle has is used.
/// </summary>
public sealed record TuningParamDef(string Label, IReadOnlyList<string> Aliases, double? Increment = null);

/// <summary>A tuning card: one of Mission Planner's group boxes and its values, in its order.</summary>
public sealed record TuningGroupDef(string Id, string Title, IReadOnlyList<TuningParamDef> Params)
{
    /// <summary>Values side by side in this many columns, filled top to bottom (the Harmonic Notch Filter box has two).</summary>
    public int Columns { get; init; } = 1;

    /// <summary>The title when the vehicle uses one of <see cref="MetricNames"/> ("WPNav (m/s)" for Copter 4.7's names).</summary>
    public string? MetricTitle { get; init; }

    public IReadOnlyList<string> MetricNames { get; init; } = [];
}

/// <summary>Cards stacked in one column of a row; <see cref="Span"/> columns wide.</summary>
public sealed record TuningCellDef(IReadOnlyList<TuningGroupDef> Groups, int Span = 1);

/// <summary>A row of the page; <see cref="LockAbove"/> puts "Lock Pitch and Roll Values" above it, as Mission Planner does.</summary>
public sealed record TuningRowDef(IReadOnlyList<TuningCellDef> Cells, bool LockAbove = false);

/// <summary>
/// Two cards whose values (matched by label; all of them when <see cref="Labels"/> is null) "Lock
/// Pitch and Roll Values" keeps equal.
/// </summary>
public sealed record TuningLockDef(string RollGroup, string PitchGroup, IReadOnlyList<string>? Labels = null);

/// <summary>
/// The cards of a tuning page in Mission Planner's arrangement: rows of columns of cards, each card
/// <see cref="CardWidth"/> wide with labels <see cref="LabelWidth"/> wide.
/// </summary>
public sealed record TuningLayout(string Name, double CardWidth, double LabelWidth, IReadOnlyList<TuningRowDef> Rows, IReadOnlyList<TuningLockDef> Locks)
{
    public IEnumerable<TuningGroupDef> Groups => Rows.SelectMany(r => r.Cells).SelectMany(c => c.Groups);
}

/// <summary>A card value bound to the parameter name this vehicle uses.</summary>
public sealed record ResolvedTuningParam(TuningParamDef Def, string Name)
{
    public string Label => Def.Label;
}

/// <summary>A card with the values this vehicle has (never empty).</summary>
public sealed record ResolvedTuningGroup(TuningGroupDef Def, IReadOnlyList<ResolvedTuningParam> Params)
{
    public string Id => Def.Id;

    /// <summary>The card title, in the units of the names found.</summary>
    public string Title => Def.MetricTitle != null && Params.Any(p => Def.MetricNames.Contains(p.Name)) ? Def.MetricTitle : Def.Title;
}

/// <summary>A column of cards the vehicle has (never empty).</summary>
public sealed record ResolvedTuningCell(IReadOnlyList<ResolvedTuningGroup> Groups, int Span);

/// <summary>A row with at least one card.</summary>
public sealed record ResolvedTuningRow(IReadOnlyList<ResolvedTuningCell> Cells, bool LockAbove);

/// <summary>A layout matched against a vehicle's parameters.</summary>
public sealed class ResolvedTuningLayout(TuningLayout? layout, IReadOnlyList<ResolvedTuningRow> rows, IReadOnlyDictionary<string, string> lockPartners)
{
    public static ResolvedTuningLayout Empty { get; } = new(null, [], new Dictionary<string, string>());

    /// <summary>The layout matched (null when the page has none for the vehicle).</summary>
    public TuningLayout? Layout { get; } = layout;

    public IReadOnlyList<ResolvedTuningRow> Rows { get; } = rows;

    public IReadOnlyList<ResolvedTuningGroup> Groups { get; } = rows.SelectMany(r => r.Cells).SelectMany(c => c.Groups).ToList();

    /// <summary>Roll parameter to its pitch twin and back, for every locked pair the vehicle has.</summary>
    public IReadOnlyDictionary<string, string> LockPartners { get; } = lockPartners;

    public bool HasLocks => LockPartners.Count > 0;

    public IEnumerable<string> Names => Groups.SelectMany(g => g.Params).Select(p => p.Name);

    public ResolvedTuningGroup? Group(string id) => Groups.FirstOrDefault(g => g.Id == id);

    public string? NameOf(string groupId, string label) => Group(groupId)?.Params.FirstOrDefault(p => p.Label == label)?.Name;

    /// <summary>
    /// Whether "Lock Pitch and Roll Values" starts ticked: as in Mission Planner, unless locked
    /// values already differ or the vehicle is a helicopter (H_SWASH_TYPE).
    /// </summary>
    public bool LockByDefault(Func<string, double?> get, Func<string, bool> has)
    {
        if (!HasLocks || has("H_SWASH_TYPE"))
            return false;
        foreach (var (a, b) in LockPartners)
        {
            if (get(a) is { } va && get(b) is { } vb && !ParamValues.Same(va, vb))
                return false;
        }

        return true;
    }
}

/// <summary>
/// Mission Planner's tuning pages, card for card: ConfigArducopter (Copter "Extended Tuning", and
/// "QP Extended Tuning" for QuadPlanes through the Q_ names), ConfigArduplane (Plane "Basic
/// Tuning") and ConfigArdurover (Rover "Basic Tuning"). Labels, titles and order are the
/// WinForms designer's; old parameter names are mapped to the current ones and cards the vehicle
/// has no parameter for are hidden.
/// </summary>
public static class TuningLayouts
{
    public const string BasicTitle = "Basic Tuning";
    public const string ExtendedTitle = "Extended Tuning";
    public const string QuadPlaneExtendedTitle = "QP Extended Tuning";

    private static TuningParamDef P(string label, params string[] aliases) => new(label, aliases);

    private static TuningParamDef Pi(string label, double increment, params string[] aliases) => new(label, aliases, increment);

    private static TuningGroupDef G(string id, string title, params TuningParamDef[] ps) => new(id, title, ps);

    private static TuningCellDef Cell(params TuningGroupDef[] groups) => new(groups);

    private static TuningCellDef Wide(int span, params TuningGroupDef[] groups) => new(groups, span);

    private static TuningRowDef Row(params TuningCellDef[] cells) => new(cells);

    // ---- copter (ConfigArducopter), also the QuadPlane page ----

    /// <summary>
    /// The QuadPlane name of a copter parameter: ATC_ → Q_A_, PSC_ → Q_P_, WPNAV_/WP_ → Q_WP_,
    /// LOIT_ → Q_LOIT_. Names older than these have no QuadPlane twin (null).
    /// </summary>
    public static string? QuadPlaneName(string copterName)
    {
        if (copterName.StartsWith("ATC_", StringComparison.Ordinal))
            return "Q_A_" + copterName[4..];
        if (copterName.StartsWith("PSC_", StringComparison.Ordinal))
            return "Q_P_" + copterName[4..];
        if (copterName.StartsWith("WPNAV_", StringComparison.Ordinal))
            return "Q_WP_" + copterName[6..];
        if (copterName.StartsWith("WP_", StringComparison.Ordinal) || copterName.StartsWith("LOIT_", StringComparison.Ordinal))
            return "Q_" + copterName;
        return null;
    }

    /// <summary>
    /// A copter value with its QuadPlane twins after the copter names, as Mission Planner's alias
    /// lists have them (a copter has no Q_ parameters and a plane none of these copter ones).
    /// </summary>
    private static TuningParamDef C(string label, double? increment, params string[] aliases) =>
        new(label, aliases.Concat(aliases.Select(QuadPlaneName).OfType<string>()).Distinct().ToList(), increment);

    private static TuningGroupDef Stabilize(string axis, string accel, string title) => G("stab-" + axis.ToLowerInvariant(), title,
        C("P", 0.001, $"STB_{axis}_P", $"ATC_ANG_{axis}_P"),
        C("ACCEL MAX", null, $"ATC_ACCEL_{accel}_MAX", $"ATC_ACC_{accel}_MAX"));

    private static TuningGroupDef Rate(string axis, string title) => G("rate-" + axis.ToLowerInvariant(), title,
        C("P", 0.001, $"RATE_{axis}_P", $"ATC_RAT_{axis}_P"),
        C("I", 0.001, $"RATE_{axis}_I", $"ATC_RAT_{axis}_I"),
        C("D", 0.0001, $"RATE_{axis}_D", $"ATC_RAT_{axis}_D"),
        C("IMAX", null, $"ATC_RAT_{axis}_IMAX", $"RATE_{axis}_IMAX"),
        C("FLTE", null, $"RATE_{axis}_FILT", $"ATC_RAT_{axis}_FILT", $"ATC_RAT_{axis}_FLTE"),
        C("FLTD", 1, $"ATC_RAT_{axis}_FLTD"),
        C("FLTT", 1, $"ATC_RAT_{axis}_FLTT"));

    private static TuningParamDef[] RcOptions(int first, int last) =>
        Enumerable.Range(first, last - first + 1)
            .Select(n => P($"RC{n} Opt", $"CH{n}_OPT", $"CH{n}_OPTION", $"RC{n}_OPTION"))
            .ToArray();

    /// <summary>Copter "Extended Tuning" and QuadPlane "QP Extended Tuning" (ConfigArducopter).</summary>
    public static TuningLayout Copter { get; } = new("ConfigArducopter", 236, 84,
    [
        Row(
            Cell(Stabilize("RLL", "R", "Stabilize Roll (Error to Rate)")),
            Cell(Stabilize("PIT", "P", "Stabilize Pitch (Error to Rate)")),
            Cell(Stabilize("YAW", "Y", "Stabilize Yaw (Error to Rate)")),
            Cell(G("pos-xy", "Position XY (Dist to Speed)",
                C("P", 0.001, "HLD_LAT_P", "POS_XY_P", "PSC_POSXY_P", "PSC_NE_POS_P"),
                C("INPUT TC", 0.001, "ATC_INPUT_TC")))),
        new TuningRowDef(
        [
            Cell(Rate("RLL", "Rate Roll")),
            Cell(Rate("PIT", "Rate Pitch")),
            Cell(Rate("YAW", "Rate Yaw")),
            Cell(
                G("vel-xy", "Velocity XY (Vel to Accel)",
                    C("P", 0.001, "LOITER_LAT_P", "VEL_XY_P", "PSC_VELXY_P", "PSC_NE_VEL_P"),
                    C("I", 0.001, "LOITER_LAT_I", "VEL_XY_I", "PSC_VELXY_I", "PSC_NE_VEL_I"),
                    C("D", 0.001, "LOITER_LAT_D", "VEL_XY_D", "PSC_VELXY_D", "PSC_NE_VEL_D"),
                    C("IMAX", null, "LOITER_LAT_IMAX", "VEL_XY_IMAX", "PSC_VELXY_IMAX", "PSC_NE_VEL_IMAX")),
                G("filters", "Basic Filters",
                    Pi("Gyro", 1, "INS_GYRO_FILTER"),
                    Pi("Accel", 1, "INS_ACCEL_FILTER"))),
        ], LockAbove: true),
        Row(
            Cell(G("acc-z", "Throttle Accel (Accel to motor)",
                C("P", 0.001, "THR_ACCEL_P", "ACCEL_Z_P", "PSC_ACCZ_P", "PSC_D_ACC_P"),
                C("I", 0.001, "THR_ACCEL_I", "ACCEL_Z_I", "PSC_ACCZ_I", "PSC_D_ACC_I"),
                C("D", 0.001, "THR_ACCEL_D", "ACCEL_Z_D", "PSC_ACCZ_D", "PSC_D_ACC_D"),
                C("IMAX", null, "THR_ACCEL_IMAX", "ACCEL_Z_IMAX", "PSC_ACCZ_IMAX", "PSC_D_ACC_IMAX"))),
            Cell(
                G("vel-z", "Throttle Rate (VSpd to accel)",
                    C("P", 0.001, "THR_RATE_P", "VEL_Z_P", "PSC_VELZ_P", "PSC_D_VEL_P")),
                G("tune", "Transmitter Tuning",
                    P("Tune", "TUNE"),
                    Pi("Min", 0.01, "TUNE_LOW", "TUNE_MIN"),
                    Pi("Max", 0.01, "TUNE_HIGH", "TUNE_MAX"))),
            Cell(
                G("pos-z", "Altitude Hold (Alt to climbrate)",
                    C("P", 0.001, "THR_ALT_P", "POS_Z_P", "PSC_POSZ_P", "PSC_D_POS_P")),
                G("rc-options", "RC Options", RcOptions(6, 10))),
            Cell(new TuningGroupDef("wpnav", "WPNav (cm's)",
            [
                C("Speed", null, "WPNAV_SPEED", "WP_SPD"),
                C("Radius", null, "WPNAV_RADIUS", "WP_RADIUS_M"),
                C("Speed Up", null, "WPNAV_SPEED_UP", "WP_SPD_UP"),
                C("Speed Dn", null, "WPNAV_SPEED_DN", "WP_SPD_DN"),
                C("Loiter Speed", null, "WPNAV_LOIT_SPEED", "LOIT_SPEED", "LOIT_SPEED_MS"),
            ])
            {
                MetricTitle = "WPNav (m/s)",
                MetricNames = ["WP_SPD", "Q_WP_SPD", "WP_RADIUS_M", "Q_WP_RADIUS_M", "LOIT_SPEED_MS", "Q_LOIT_SPEED_MS"],
            })),
        Row(
            Wide(2, new TuningGroupDef("filter-logs", "Filter Logs",
            [
                P("Mask", "INS_LOG_BAT_MASK"),
                Pi("Options", 1, "INS_LOG_BAT_OPT"),
            ]) { Columns = 2 })),
        Row(
            Cell(G("notch", "Static Notch Filter",
                P("Enabled", "INS_NOTCH_ENABLE"),
                Pi("Frequency", 1, "INS_NOTCH_FREQ"),
                Pi("BandWidth", 1, "INS_NOTCH_BW"),
                Pi("Attenuation", 1, "INS_NOTCH_ATT"))),
            Wide(2, new TuningGroupDef("hnotch", "Harmonic Notch Filter",
            [
                P("Enabled", "INS_HNTCH_ENABLE"),
                Pi("Mode", 1, "INS_HNTCH_MODE"),
                Pi("Reference", 1, "INS_HNTCH_REF"),
                Pi("Frequency", 1, "INS_HNTCH_FREQ"),
                Pi("Attenuation", 1, "INS_HNTCH_ATT"),
                Pi("Bandwidth", 1, "INS_HNTCH_BW"),
                Pi("Options", 1, "INS_HNTCH_OPTS"),
                Pi("Harmonics", 1, "INS_HNTCH_HMNCS"),
            ]) { Columns = 2 })),
    ],
    [
        new TuningLockDef("rate-rll", "rate-pit"),
        new TuningLockDef("stab-rll", "stab-pit", ["P"]),
    ]);

    // ---- plane (ConfigArduplane) ----

    /// <summary>
    /// Plane "Basic Tuning" (ConfigArduplane). Its legacy boxes (Nav Pitch AS/Alt Pid, Energy/Alt
    /// Pid) are hidden in Mission Planner and left out here.
    /// </summary>
    public static TuningLayout Plane { get; } = new("ConfigArduplane", 272, 112,
    [
        Row(
            Cell(G("roll", "Servo Roll Pid",
                P("P", "RLL2SRV_P", "RLL_RATE_P"),
                P("I", "RLL2SRV_I", "RLL_RATE_I"),
                P("D", "RLL2SRV_D", "RLL_RATE_D"),
                P("INT_MAX", "RLL2SRV_IMAX", "RLL_RATE_IMAX"))),
            Cell(G("pitch", "Servo Pitch Pid",
                P("P", "PTCH2SRV_P", "PTCH_RATE_P"),
                P("I", "PTCH2SRV_I", "PTCH_RATE_I"),
                P("D", "PTCH2SRV_D", "PTCH_RATE_D"),
                P("INT_MAX", "PTCH2SRV_IMAX", "PTCH_RATE_IMAX"))),
            Cell(G("yaw", "Servo Yaw",
                P("Yaw 2 roll", "YAW2SRV_RLL"),
                P("Integral", "YAW2SRV_INT"),
                P("Dampening", "YAW2SRV_DAMP"),
                P("Integrator Max", "YAW2SRV_IMAX")))),
        Row(
            Cell(
                G("l1", "L1 Control - Turn Control",
                    P("Period", "NAVL1_PERIOD"),
                    P("Damping", "NAVL1_DAMPING")),
                G("tecs", "TECS",
                    P("Climb Max (m/s)", "TECS_CLMB_MAX"),
                    P("Sink Min (m/s)", "TECS_SINK_MIN"),
                    P("Sink Max (m/s)", "TECS_SINK_MAX"),
                    P("Pitch Dampening", "TECS_PTCH_DAMP"),
                    P("Time Const", "TECS_TIME_CONST"))),
            Cell(
                G("mix", "Other Mix's",
                    P("P to T", "KFF_THR2PTCH", "KFF_PTCH2THR"),
                    P("Rudder Mix", "KFF_RDDRMIX")),
                G("nav-angles", "Navigation Angles",
                    P("Bank Max", "ROLL_LIMIT_DEG", "LIM_ROLL_CD"),
                    P("Pitch Max", "PTCH_LIM_MAX_DEG", "LIM_PITCH_MAX"),
                    P("Pitch Min", "PTCH_LIM_MIN_DEG", "LIM_PITCH_MIN"))),
            Cell(
                G("throttle", "Throttle 0-100%",
                    P("Cruise", "TRIM_THROTTLE"),
                    P("Min", "THR_MIN"),
                    P("Max", "THR_MAX"),
                    P("SlewRate", "THR_SLEWRATE")),
                G("airspeed", "Airspeed m/s",
                    P("Cruise", "AIRSPEED_CRUISE", "TRIM_ARSPD_CM"),
                    P("Min", "AIRSPEED_MIN", "ARSPD_FBW_MIN"),
                    P("Max", "AIRSPEED_MAX", "ARSPD_FBW_MAX"),
                    Pi("Ratio", 0.005, "ARSPD_RATIO")))),
    ], []);

    // ---- rover (ConfigArdurover) ----

    /// <summary>Rover "Basic Tuning" (ConfigArdurover).</summary>
    public static TuningLayout Rover { get; } = new("ConfigArdurover", 280, 124,
    [
        Row(
            Cell(
                G("steer-rate", "Steering Rate",
                    Pi("P", 0.1, "STEER2SRV_P", "ATC_STR_RAT_P"),
                    Pi("I", 0.1, "STEER2SRV_I", "ATC_STR_RAT_I"),
                    Pi("D", 0.1, "STEER2SRV_D", "ATC_STR_RAT_D"),
                    Pi("IMAX", 0.1, "STEER2SRV_IMAX", "ATC_STR_RAT_IMAX"),
                    Pi("FF", 0.1, "ATC_STR_RAT_FF")),
                G("steer-mode", "Steering Mode",
                    Pi("Turn Radius", 0.1, "TURN_RADIUS")),
                G("rc-options", "RC Options",
                    Enumerable.Range(7, 4).Select(n => P($"RC{n} Opt", $"CH{n}_OPTION", $"RC{n}_OPTION")).ToArray())),
            Cell(
                G("speed", "Speed/Throttle",
                    Pi("P", 0.1, "SPEED2THR_P", "ATC_SPEED_P"),
                    Pi("I", 0.1, "SPEED2THR_I", "ATC_SPEED_I"),
                    Pi("D", 0.1, "SPEED2THR_D", "ATC_SPEED_D"),
                    Pi("IMAX", 0.1, "SPEED2THR_IMAX", "ATC_SPEED_IMAX"),
                    Pi("Accel Max (m/s/s)", 0.1, "ATC_ACCEL_MAX"),
                    P("Brake", "ATC_BRAKE"),
                    Pi("Cruise Speed", 0.1, "CRUISE_SPEED"),
                    Pi("Cruise Throttle", 1, "CRUISE_THROTTLE")),
                G("motors", "Throttle and Motors",
                    P("Motor Type", "MOT_PWM_TYPE"),
                    Pi("Throttle Min (%)", 1, "THR_MIN", "MOT_THR_MIN"),
                    Pi("Throttle Max (%)", 1, "THR_MAX", "MOT_THR_MAX"))),
            Cell(
                G("nav", "Navigation",
                    Pi("WP Speed", 0.1, "WP_SPEED"),
                    Pi("WP Radius", 0.1, "WP_RADIUS"),
                    Pi("WP Overshoot", 0.1, "WP_OVERSHOOT"),
                    Pi("Turn G Max", 0.1, "TURN_MAX_G", "ATC_TURN_MAX_G"),
                    Pi("Lat Acc Cntl Period", 1, "NAVL1_PERIOD"),
                    Pi("Lat Acc Cntl Damp", 0.05, "NAVL1_DAMPING")),
                G("avoid", "Avoidance",
                    Pi("Trigger Dist (cm)", 1, "SONAR_TRIGGER_CM", "RNGFND_TRIGGR_CM"),
                    Pi("Turn Angle", 1, "SONAR_TURN_ANGLE", "RNGFND_TURN_ANGL"),
                    Pi("Turn Time", 1, "SONAR_TURN_TIME", "RNGFND_TURN_TIME"),
                    Pi("Sonar Debounce", 1, "SONAR_DEBOUNCE", "RNGFND_DEBOUNCE")))),
    ], []);

    /// <summary>A plane with QuadPlane enabled (Q_ENABLE set) gets Mission Planner's "QP Extended Tuning".</summary>
    public static bool QuadPlaneEnabled(Func<string, double?> get) => get("Q_ENABLE") is { } q && q != 0;

    /// <summary>The cards of Basic Tuning: planes and rovers (copters have sliders instead).</summary>
    public static TuningLayout? Basic(Firmwares firmware) => firmware switch
    {
        Firmwares.ArduPlane => Plane,
        Firmwares.ArduRover => Rover,
        _ => null,
    };

    /// <summary>The cards of Extended Tuning: copters, and planes with QuadPlane enabled.</summary>
    public static TuningLayout? Extended(Firmwares firmware, Func<string, double?> get) => firmware switch
    {
        Firmwares.ArduCopter2 => Copter,
        Firmwares.ArduPlane when QuadPlaneEnabled(get) => Copter,
        _ => null,
    };

    /// <summary>The Extended Tuning page title for a vehicle ("QP Extended Tuning" on planes).</summary>
    public static string ExtendedTitleFor(Firmwares firmware) =>
        firmware == Firmwares.ArduPlane ? QuadPlaneExtendedTitle : ExtendedTitle;

    /// <summary>
    /// Matches a layout against the vehicle: each value takes its first alias the vehicle has (and
    /// no other card uses); cards, columns and rows left empty are dropped.
    /// </summary>
    public static ResolvedTuningLayout Resolve(TuningLayout layout, Func<string, bool> has)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<ResolvedTuningRow>();
        foreach (var row in layout.Rows)
        {
            var cells = new List<ResolvedTuningCell>();
            foreach (var cell in row.Cells)
            {
                var groups = new List<ResolvedTuningGroup>();
                foreach (var g in cell.Groups)
                {
                    var found = new List<ResolvedTuningParam>();
                    foreach (var p in g.Params)
                    {
                        var name = p.Aliases.FirstOrDefault(a => !used.Contains(a) && has(a));
                        if (name == null)
                            continue;
                        used.Add(name);
                        found.Add(new ResolvedTuningParam(p, name));
                    }

                    if (found.Count > 0)
                        groups.Add(new ResolvedTuningGroup(g, found));
                }

                if (groups.Count > 0)
                    cells.Add(new ResolvedTuningCell(groups, cell.Span));
            }

            if (cells.Count > 0)
                rows.Add(new ResolvedTuningRow(cells, row.LockAbove));
        }

        var all = rows.SelectMany(r => r.Cells).SelectMany(c => c.Groups).ToList();
        var partners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var l in layout.Locks)
        {
            var roll = all.FirstOrDefault(g => g.Id == l.RollGroup);
            var pitch = all.FirstOrDefault(g => g.Id == l.PitchGroup);
            if (roll == null || pitch == null)
                continue;
            foreach (var r in roll.Params)
            {
                if (l.Labels != null && !l.Labels.Contains(r.Label))
                    continue;
                var p = pitch.Params.FirstOrDefault(x => x.Label == r.Label);
                if (p == null)
                    continue;
                partners[r.Name] = p.Name;
                partners[p.Name] = r.Name;
            }
        }

        return new ResolvedTuningLayout(layout, rows, partners);
    }
}
