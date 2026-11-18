using MissionPlanner.ArduPilot;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>
/// One value on a tuning card: a short label and the names the parameter has had across firmware
/// versions. The first name the vehicle has is used.
/// </summary>
public sealed record TuningParamDef(string Label, IReadOnlyList<string> Aliases, double? Increment = null);

/// <summary>A tuning card: a controller or feature and its parameters.</summary>
public sealed record TuningGroupDef(string Id, string Title, string Section, IReadOnlyList<TuningParamDef> Params);

/// <summary>Two cards whose values (matched by label) "Lock pitch and roll values" keeps equal.</summary>
public sealed record TuningLockDef(string RollGroup, string PitchGroup);

/// <summary>The cards of an Extended Tuning page, in display order.</summary>
public sealed record TuningLayout(IReadOnlyList<TuningGroupDef> Groups, IReadOnlyList<TuningLockDef> Locks)
{
    public static TuningLayout Join(params TuningLayout[] parts) =>
        new(parts.SelectMany(p => p.Groups).ToList(), parts.SelectMany(p => p.Locks).ToList());
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

    public string Title => Def.Title;

    public string Section => Def.Section;
}

/// <summary>A layout matched against a vehicle's parameters.</summary>
public sealed class ResolvedTuningLayout(IReadOnlyList<ResolvedTuningGroup> groups, IReadOnlyDictionary<string, string> lockPartners)
{
    public IReadOnlyList<ResolvedTuningGroup> Groups { get; } = groups;

    /// <summary>Roll parameter to its pitch twin and back, for every locked pair the vehicle has.</summary>
    public IReadOnlyDictionary<string, string> LockPartners { get; } = lockPartners;

    public bool HasLocks => LockPartners.Count > 0;

    public IEnumerable<string> Names => Groups.SelectMany(g => g.Params).Select(p => p.Name);

    public ResolvedTuningGroup? Group(string id) => Groups.FirstOrDefault(g => g.Id == id);

    public string? NameOf(string groupId, string label) => Group(groupId)?.Params.FirstOrDefault(p => p.Label == label)?.Name;

    /// <summary>
    /// Whether the roll/pitch lock starts on: as Mission Planner, unless locked values already
    /// differ or the vehicle is a helicopter (H_SWASH_TYPE).
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
/// What the Extended Tuning page shows for each vehicle type, after Mission Planner's
/// ConfigArducopter (also QuadPlane, with Q_ names), ConfigArduplane and ConfigArdurover.
/// Cards whose parameters the vehicle does not have are hidden.
/// </summary>
public static class TuningLayouts
{
    public const string RateSection = "Rate Controllers";
    public const string AttitudeSection = "Attitude";
    public const string PositionSection = "Altitude and Position";
    public const string NavigationSection = "Navigation";
    public const string TuningSection = "Transmitter Tuning and RC Options";
    public const string FilterSection = "Filters";
    public const string QuadPlaneSection = "QuadPlane (VTOL)";
    public const string PlaneAttitudeSection = "Attitude Controllers";
    public const string PlaneNavigationSection = "Navigation and TECS";
    public const string PlaneSpeedSection = "Airspeed, Throttle and Limits";
    public const string LegacySection = "Older Firmware Controllers";
    public const string SteeringSection = "Steering";
    public const string SpeedSection = "Speed and Throttle";

    private static TuningParamDef P(string label, params string[] aliases) => new(label, aliases);

    private static TuningGroupDef G(string id, string title, string section, params TuningParamDef[] ps) => new(id, title, section, ps);

    // ---- copter (and the multicopter half of a QuadPlane) ----

    private static TuningGroupDef CopterRate(string axis, string title) => G("rate-" + axis.ToLowerInvariant(), title, RateSection,
        P("P", $"ATC_RAT_{axis}_P", $"RATE_{axis}_P"),
        P("I", $"ATC_RAT_{axis}_I", $"RATE_{axis}_I"),
        P("D", $"ATC_RAT_{axis}_D", $"RATE_{axis}_D"),
        P("FF", $"ATC_RAT_{axis}_FF"),
        P("IMAX", $"ATC_RAT_{axis}_IMAX", $"RATE_{axis}_IMAX"),
        P("FLTE", $"ATC_RAT_{axis}_FLTE", $"ATC_RAT_{axis}_FILT", $"RATE_{axis}_FILT"),
        P("FLTD", $"ATC_RAT_{axis}_FLTD"),
        P("FLTT", $"ATC_RAT_{axis}_FLTT"));

    private static TuningGroupDef CopterAngle(string axis, string accelAxis, string title) => G("stab-" + axis.ToLowerInvariant(), title, AttitudeSection,
        P("P", $"ATC_ANG_{axis}_P", $"STB_{axis}_P"),
        P("Accel Max", $"ATC_ACC_{accelAxis}_MAX", $"ATC_ACCEL_{accelAxis}_MAX"));

    private static TuningParamDef[] Pid(string label, params string[] prefixes) =>
    [
        P(label == "" ? "P" : label + " P", prefixes.Select(p => p + "_P").ToArray()),
        P(label == "" ? "I" : label + " I", prefixes.Select(p => p + "_I").ToArray()),
        P(label == "" ? "D" : label + " D", prefixes.Select(p => p + "_D").ToArray()),
        P(label == "" ? "IMAX" : label + " IMAX", prefixes.Select(p => p + "_IMAX").ToArray()),
    ];

    /// <summary>Controllers a QuadPlane has too (as Q_ parameters).</summary>
    private static readonly TuningLayout CopterVtol = new(
    [
        CopterRate("RLL", "Rate Roll"),
        CopterRate("PIT", "Rate Pitch"),
        CopterRate("YAW", "Rate Yaw"),
        CopterAngle("RLL", "R", "Stabilize Roll"),
        CopterAngle("PIT", "P", "Stabilize Pitch"),
        CopterAngle("YAW", "Y", "Stabilize Yaw"),
        G("input", "Pilot Input", AttitudeSection,
            P("Input TC", "ATC_INPUT_TC"),
            P("RC Feel", "RC_FEEL_RP")),
        G("pos-z", "Altitude Hold (Position Z)", PositionSection,
            P("P", "PSC_D_POS_P", "PSC_POSZ_P", "POS_Z_P", "THR_ALT_P")),
        G("vel-z", "Climb Rate (Velocity Z)", PositionSection,
            P("P", "PSC_D_VEL_P", "PSC_VELZ_P", "VEL_Z_P", "THR_RATE_P"),
            P("I", "PSC_D_VEL_I", "PSC_VELZ_I"),
            P("D", "PSC_D_VEL_D", "PSC_VELZ_D")),
        G("acc-z", "Throttle Accel (Accel Z)", PositionSection,
            Pid("", "PSC_D_ACC", "PSC_ACCZ", "ACCEL_Z", "THR_ACCEL")),
        G("pos-xy", "Position XY", PositionSection,
            P("P", "PSC_NE_POS_P", "PSC_POSXY_P", "POS_XY_P", "HLD_LAT_P")),
        G("vel-xy", "Velocity XY", PositionSection,
            Pid("", "PSC_NE_VEL", "PSC_VELXY", "VEL_XY", "LOITER_LAT")),
        G("wpnav", "Waypoint Navigation", NavigationSection,
            P("Speed", "WP_SPD", "WPNAV_SPEED"),
            P("Speed Up", "WP_SPD_UP", "WPNAV_SPEED_UP"),
            P("Speed Down", "WP_SPD_DN", "WPNAV_SPEED_DN"),
            P("Radius", "WP_RADIUS_M", "WPNAV_RADIUS"),
            P("Loiter Speed", "LOIT_SPEED_MS", "LOIT_SPEED", "WPNAV_LOIT_SPEED")),
    ],
    [
        new TuningLockDef("rate-rll", "rate-pit"),
        new TuningLockDef("stab-rll", "stab-pit"),
    ]);

    private static readonly TuningLayout CopterOptions = new(
    [
        G("tune", "Transmitter Tuning", TuningSection,
            P("Tune", "TUNE"),
            P("Min", "TUNE_MIN", "TUNE_LOW"),
            P("Max", "TUNE_MAX", "TUNE_HIGH"),
            P("Tune 2", "TUNE2"),
            P("Min 2", "TUNE2_MIN"),
            P("Max 2", "TUNE2_MAX")),
        G("rc-options", "RC Options", TuningSection, RcOptions(6, 10)),
    ], []);

    private static TuningParamDef[] RcOptions(int first, int last) =>
        Enumerable.Range(first, last - first + 1)
            .Select(n => P($"RC{n} Option", $"RC{n}_OPTION", $"CH{n}_OPT", $"CH{n}_OPTION"))
            .ToArray();

    /// <summary>Gyro/accel filters and notches: every vehicle has them.</summary>
    public static TuningLayout Filters { get; } = new(
    [
        G("filters", "Basic Filters", FilterSection,
            P("Gyro", "INS_GYRO_FILTER"),
            P("Accel", "INS_ACCEL_FILTER")),
        G("hnotch", "Harmonic Notch Filter", FilterSection,
            P("Enabled", "INS_HNTCH_ENABLE"),
            P("Mode", "INS_HNTCH_MODE"),
            P("Reference", "INS_HNTCH_REF"),
            P("Frequency", "INS_HNTCH_FREQ"),
            P("Bandwidth", "INS_HNTCH_BW"),
            P("Attenuation", "INS_HNTCH_ATT"),
            P("Options", "INS_HNTCH_OPTS"),
            P("Harmonics", "INS_HNTCH_HMNCS")),
        G("hnotch2", "Harmonic Notch Filter 2", FilterSection,
            P("Enabled", "INS_HNTC2_ENABLE"),
            P("Mode", "INS_HNTC2_MODE"),
            P("Reference", "INS_HNTC2_REF"),
            P("Frequency", "INS_HNTC2_FREQ"),
            P("Bandwidth", "INS_HNTC2_BW"),
            P("Attenuation", "INS_HNTC2_ATT"),
            P("Options", "INS_HNTC2_OPTS"),
            P("Harmonics", "INS_HNTC2_HMNCS")),
        G("notch", "Static Notch Filter", FilterSection,
            P("Enabled", "INS_NOTCH_ENABLE"),
            P("Frequency", "INS_NOTCH_FREQ"),
            P("Bandwidth", "INS_NOTCH_BW"),
            P("Attenuation", "INS_NOTCH_ATT")),
        G("filter-logs", "Filter Logs", FilterSection,
            P("Mask", "INS_LOG_BAT_MASK"),
            P("Options", "INS_LOG_BAT_OPT")),
    ], []);

    public static TuningLayout Copter { get; } = TuningLayout.Join(CopterVtol, CopterOptions, Filters);

    // ---- QuadPlane ----

    /// <summary>
    /// The QuadPlane name of a copter parameter: ATC_ → Q_A_, PSC_ → Q_P_, WPNAV_ → Q_WP_,
    /// WP_/LOIT_ → Q_WP_/Q_LOIT_, MOT_ → Q_M_. Older copter names have no QuadPlane twin (null).
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
        if (copterName.StartsWith("MOT_", StringComparison.Ordinal))
            return "Q_M_" + copterName[4..];
        return null;
    }

    private static TuningLayout ToQuadPlane(TuningLayout copter) => new(
        copter.Groups
            .Select(g => new TuningGroupDef("q-" + g.Id, "VTOL " + g.Title, QuadPlaneSection,
                g.Params
                    .Select(p => p with { Aliases = p.Aliases.Select(QuadPlaneName).OfType<string>().ToList() })
                    .Where(p => p.Aliases.Count > 0)
                    .ToList()))
            .Where(g => g.Params.Count > 0)
            .ToList(),
        copter.Locks.Select(l => new TuningLockDef("q-" + l.RollGroup, "q-" + l.PitchGroup)).ToList());

    /// <summary>The multicopter controllers of a QuadPlane (Q_A_, Q_P_, Q_WP_ names).</summary>
    public static TuningLayout QuadPlane { get; } = ToQuadPlane(CopterVtol);

    /// <summary>QuadPlane cards show when Q_ENABLE is set, as Mission Planner's "QP Extended Tuning".</summary>
    public static bool QuadPlaneEnabled(Func<string, double?> get) => get("Q_ENABLE") is > 0;

    // ---- plane ----

    private static readonly TuningLayout PlaneCore = new(
    [
        G("roll", "Roll Rate", PlaneAttitudeSection,
            P("FF", "RLL_RATE_FF", "RLL2SRV_FF"),
            P("P", "RLL_RATE_P", "RLL2SRV_P"),
            P("I", "RLL_RATE_I", "RLL2SRV_I"),
            P("D", "RLL_RATE_D", "RLL2SRV_D"),
            P("IMAX", "RLL_RATE_IMAX", "RLL2SRV_IMAX"),
            P("Time Const", "RLL2SRV_TCONST"),
            P("Max Rate", "RLL2SRV_RMAX")),
        G("pitch", "Pitch Rate", PlaneAttitudeSection,
            P("FF", "PTCH_RATE_FF", "PTCH2SRV_FF"),
            P("P", "PTCH_RATE_P", "PTCH2SRV_P"),
            P("I", "PTCH_RATE_I", "PTCH2SRV_I"),
            P("D", "PTCH_RATE_D", "PTCH2SRV_D"),
            P("IMAX", "PTCH_RATE_IMAX", "PTCH2SRV_IMAX"),
            P("Time Const", "PTCH2SRV_TCONST"),
            P("Max Up", "PTCH2SRV_RMAX_UP"),
            P("Max Down", "PTCH2SRV_RMAX_DN")),
        G("yaw-damper", "Yaw Damper", PlaneAttitudeSection,
            P("Yaw to Roll", "YAW2SRV_RLL"),
            P("Integral", "YAW2SRV_INT"),
            P("Damping", "YAW2SRV_DAMP"),
            P("Integrator Max", "YAW2SRV_IMAX"),
            P("Sideslip", "YAW2SRV_SLIP")),
        G("yaw-rate", "Yaw Rate", PlaneAttitudeSection,
            P("Enabled", "YAW_RATE_ENABLE"),
            P("FF", "YAW_RATE_FF"),
            P("P", "YAW_RATE_P"),
            P("I", "YAW_RATE_I"),
            P("D", "YAW_RATE_D"),
            P("IMAX", "YAW_RATE_IMAX")),
        G("l1", "L1 Navigation", PlaneNavigationSection,
            P("Period", "NAVL1_PERIOD"),
            P("Damping", "NAVL1_DAMPING")),
        G("tecs", "TECS", PlaneNavigationSection,
            P("Climb Max", "TECS_CLMB_MAX"),
            P("Sink Min", "TECS_SINK_MIN"),
            P("Sink Max", "TECS_SINK_MAX"),
            P("Pitch Damping", "TECS_PTCH_DAMP"),
            P("Time Const", "TECS_TIME_CONST"),
            P("Speed Weight", "TECS_SPDWEIGHT")),
        G("nav-angles", "Navigation Angles", PlaneNavigationSection,
            P("Bank Max", "ROLL_LIMIT_DEG", "LIM_ROLL_CD"),
            P("Pitch Max", "PTCH_LIM_MAX_DEG", "LIM_PITCH_MAX"),
            P("Pitch Min", "PTCH_LIM_MIN_DEG", "LIM_PITCH_MIN")),
        G("airspeed", "Airspeed", PlaneSpeedSection,
            P("Cruise", "AIRSPEED_CRUISE", "TRIM_ARSPD_CM"),
            P("Min", "AIRSPEED_MIN", "ARSPD_FBW_MIN"),
            P("Max", "AIRSPEED_MAX", "ARSPD_FBW_MAX"),
            P("Ratio", "ARSPD_RATIO")),
        G("throttle", "Throttle", PlaneSpeedSection,
            P("Cruise", "TRIM_THROTTLE"),
            P("Min", "THR_MIN"),
            P("Max", "THR_MAX"),
            P("Slew Rate", "THR_SLEWRATE")),
        G("mix", "Other Mixes", PlaneSpeedSection,
            P("Rudder Mix", "KFF_RDDRMIX"),
            P("Throttle to Pitch", "KFF_THR2PTCH", "KFF_PTCH2THR")),
        G("arsp2ptch", "Nav Pitch Airspeed PID", LegacySection, Pid("", "ARSP2PTCH")),
        G("alt2ptch", "Nav Pitch Altitude PID", LegacySection, Pid("", "ALT2PTCH")),
        G("enrgy2thr", "Energy/Altitude PID", LegacySection, Pid("", "ENRGY2THR")),
    ], []);

    public static TuningLayout Plane { get; } = TuningLayout.Join(PlaneCore, Filters);

    public static TuningLayout PlaneWithQuadPlane { get; } = TuningLayout.Join(PlaneCore, QuadPlane, Filters);

    // ---- rover ----

    public static TuningLayout Rover { get; } = TuningLayout.Join(new TuningLayout(
    [
        G("steer-rate", "Steering Rate", SteeringSection,
            P("FF", "ATC_STR_RAT_FF"),
            P("P", "ATC_STR_RAT_P", "STEER2SRV_P"),
            P("I", "ATC_STR_RAT_I", "STEER2SRV_I"),
            P("D", "ATC_STR_RAT_D", "STEER2SRV_D"),
            P("IMAX", "ATC_STR_RAT_IMAX", "STEER2SRV_IMAX"),
            P("Rate Max", "ATC_STR_RAT_MAX"),
            P("Accel Max", "ATC_STR_ACC_MAX")),
        G("steer-mode", "Steering Mode", SteeringSection,
            P("Angle P", "ATC_STR_ANG_P"),
            P("Turn Radius", "TURN_RADIUS"),
            P("Turn G Max", "ATC_TURN_MAX_G", "TURN_MAX_G")),
        G("speed", "Speed/Throttle", SpeedSection,
            P("FF", "ATC_SPEED_FF"),
            P("P", "ATC_SPEED_P", "SPEED2THR_P"),
            P("I", "ATC_SPEED_I", "SPEED2THR_I"),
            P("D", "ATC_SPEED_D", "SPEED2THR_D"),
            P("IMAX", "ATC_SPEED_IMAX", "SPEED2THR_IMAX"),
            P("Accel Max", "ATC_ACCEL_MAX"),
            P("Decel Max", "ATC_DECEL_MAX"),
            P("Brake", "ATC_BRAKE")),
        G("motors", "Throttle and Motors", SpeedSection,
            P("Cruise Speed", "CRUISE_SPEED"),
            P("Cruise Throttle", "CRUISE_THROTTLE"),
            P("Throttle Min", "MOT_THR_MIN", "THR_MIN"),
            P("Throttle Max", "MOT_THR_MAX", "THR_MAX"),
            P("Speed Max", "SPEED_MAX"),
            P("Motor Type", "MOT_PWM_TYPE")),
        G("rover-nav", "Navigation", NavigationSection,
            P("WP Speed", "WP_SPEED"),
            P("WP Radius", "WP_RADIUS"),
            P("WP Overshoot", "WP_OVERSHOOT"),
            P("WP Accel", "WP_ACCEL"),
            P("L1 Period", "NAVL1_PERIOD"),
            P("L1 Damping", "NAVL1_DAMPING")),
        G("avoid", "Avoidance", NavigationSection,
            P("Trigger Dist", "RNGFND_TRIGGR_CM", "SONAR_TRIGGER_CM"),
            P("Turn Angle", "RNGFND_TURN_ANGL", "SONAR_TURN_ANGLE"),
            P("Turn Time", "RNGFND_TURN_TIME", "SONAR_TURN_TIME"),
            P("Debounce", "RNGFND_DEBOUNCE", "SONAR_DEBOUNCE")),
        G("rover-rc-options", "RC Options", TuningSection, RcOptions(7, 10)),
    ], []), Filters);

    /// <summary>The Extended Tuning layout for a vehicle, or null when the page has none.</summary>
    public static TuningLayout? For(Firmwares firmware, Func<string, double?> get) => firmware switch
    {
        Firmwares.ArduCopter2 => Copter,
        Firmwares.ArduPlane => QuadPlaneEnabled(get) ? PlaneWithQuadPlane : Plane,
        Firmwares.ArduRover => Rover,
        _ => null,
    };

    /// <summary>
    /// Matches a layout against the vehicle: each value takes its first alias the vehicle has
    /// (and no other card uses); cards left empty are dropped.
    /// </summary>
    public static ResolvedTuningLayout Resolve(TuningLayout layout, Func<string, bool> has)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var groups = new List<ResolvedTuningGroup>();
        foreach (var g in layout.Groups)
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

        var partners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var l in layout.Locks)
        {
            var roll = groups.FirstOrDefault(g => g.Id == l.RollGroup);
            var pitch = groups.FirstOrDefault(g => g.Id == l.PitchGroup);
            if (roll == null || pitch == null)
                continue;
            foreach (var r in roll.Params)
            {
                var p = pitch.Params.FirstOrDefault(x => x.Label == r.Label);
                if (p == null)
                    continue;
                partners[r.Name] = p.Name;
                partners[p.Name] = r.Name;
            }
        }

        return new ResolvedTuningLayout(groups, partners);
    }
}
