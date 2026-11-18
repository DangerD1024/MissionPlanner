using MissionPlanner.App.Core.ViewModels.ConfigScreens;
using MissionPlanner.ArduPilot;

namespace MissionPlanner.App.Tests.ConfigScreens;

/// <summary>
/// Mission Planner's tuning cards matched against real parameter lists (dumped from 4.7.2 SITL) and
/// older names: which card shows which parameter, which cards hide, the QuadPlane page and the lock.
/// </summary>
[TestClass]
public class TuningLayoutTests
{
    private static Func<string, bool> Has(IDictionary<string, double> p) => p.ContainsKey;

    private static Func<string, double?> Get(IDictionary<string, double> p) => n => p.TryGetValue(n, out var v) ? v : null;

    /// <summary>"Card title: LABEL=NAME, ..." per card in display order (rows, then columns, then cards).</summary>
    private static string[] Describe(ResolvedTuningLayout layout) =>
        layout.Groups.Select(g => g.Title + ": " + string.Join(", ", g.Params.Select(p => p.Label + "=" + p.Name))).ToArray();

    [TestMethod]
    public void CopterSitlExtendedTuningIsMissionPlannersPage()
    {
        var p = TuningFixtures.Load(TuningFixtures.Copter);
        var layout = TuningLayouts.Resolve(TuningLayouts.Extended(Firmwares.ArduCopter2, Get(p))!, Has(p));

        CollectionAssert.AreEqual(new[]
        {
            "Stabilize Roll (Error to Rate): P=ATC_ANG_RLL_P, ACCEL MAX=ATC_ACC_R_MAX",
            "Stabilize Pitch (Error to Rate): P=ATC_ANG_PIT_P, ACCEL MAX=ATC_ACC_P_MAX",
            "Stabilize Yaw (Error to Rate): P=ATC_ANG_YAW_P, ACCEL MAX=ATC_ACC_Y_MAX",
            "Position XY (Dist to Speed): P=PSC_NE_POS_P, INPUT TC=ATC_INPUT_TC",
            "Rate Roll: P=ATC_RAT_RLL_P, I=ATC_RAT_RLL_I, D=ATC_RAT_RLL_D, IMAX=ATC_RAT_RLL_IMAX, FLTE=ATC_RAT_RLL_FLTE, FLTD=ATC_RAT_RLL_FLTD, FLTT=ATC_RAT_RLL_FLTT",
            "Rate Pitch: P=ATC_RAT_PIT_P, I=ATC_RAT_PIT_I, D=ATC_RAT_PIT_D, IMAX=ATC_RAT_PIT_IMAX, FLTE=ATC_RAT_PIT_FLTE, FLTD=ATC_RAT_PIT_FLTD, FLTT=ATC_RAT_PIT_FLTT",
            "Rate Yaw: P=ATC_RAT_YAW_P, I=ATC_RAT_YAW_I, D=ATC_RAT_YAW_D, IMAX=ATC_RAT_YAW_IMAX, FLTE=ATC_RAT_YAW_FLTE, FLTD=ATC_RAT_YAW_FLTD, FLTT=ATC_RAT_YAW_FLTT",
            "Velocity XY (Vel to Accel): P=PSC_NE_VEL_P, I=PSC_NE_VEL_I, D=PSC_NE_VEL_D, IMAX=PSC_NE_VEL_IMAX",
            "Basic Filters: Gyro=INS_GYRO_FILTER, Accel=INS_ACCEL_FILTER",
            "Throttle Accel (Accel to motor): P=PSC_D_ACC_P, I=PSC_D_ACC_I, D=PSC_D_ACC_D, IMAX=PSC_D_ACC_IMAX",
            "Throttle Rate (VSpd to accel): P=PSC_D_VEL_P",
            "Transmitter Tuning: Tune=TUNE, Min=TUNE_MIN, Max=TUNE_MAX",
            "Altitude Hold (Alt to climbrate): P=PSC_D_POS_P",
            "RC Options: RC6 Opt=RC6_OPTION, RC7 Opt=RC7_OPTION, RC8 Opt=RC8_OPTION, RC9 Opt=RC9_OPTION, RC10 Opt=RC10_OPTION",
            "WPNav (m/s): Speed=WP_SPD, Radius=WP_RADIUS_M, Speed Up=WP_SPD_UP, Speed Dn=WP_SPD_DN, Loiter Speed=LOIT_SPEED_MS",
            "Filter Logs: Mask=INS_LOG_BAT_MASK, Options=INS_LOG_BAT_OPT",
            // 4.7 has no static notch; the harmonic notch shows its other values once enabled
            "Harmonic Notch Filter: Enabled=INS_HNTCH_ENABLE",
        }, Describe(layout));

        // Mission Planner's arrangement: four columns, the lock above the rate controllers
        CollectionAssert.AreEqual(new[] { 4, 4, 4, 1, 1 }, layout.Rows.Select(r => r.Cells.Count).ToArray());
        CollectionAssert.AreEqual(new[] { false, true, false, false, false }, layout.Rows.Select(r => r.LockAbove).ToArray());
        Assert.AreEqual(2, layout.Rows[3].Cells[0].Span, "Filter Logs spans two columns");
        Assert.AreEqual(2, layout.Group("hnotch")!.Def.Columns, "the harmonic notch box has two columns of values");

        // the lock pairs every rate value and the stabilize P
        Assert.HasCount(16, layout.LockPartners);
        Assert.AreEqual("ATC_RAT_PIT_FLTT", layout.LockPartners["ATC_RAT_RLL_FLTT"]);
        Assert.AreEqual("ATC_ANG_RLL_P", layout.LockPartners["ATC_ANG_PIT_P"]);
        Assert.IsFalse(layout.LockPartners.ContainsKey("ATC_ACC_R_MAX"));
        Assert.IsTrue(layout.LockByDefault(Get(p), Has(p)), "SITL's roll and pitch agree");

        // a copter has no Basic Tuning cards (it has sliders) and no QuadPlane names
        Assert.IsNull(TuningLayouts.Basic(Firmwares.ArduCopter2));
        Assert.IsFalse(layout.Names.Any(n => n.StartsWith("Q_", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void CopterWithTheHarmonicNotchEnabledShowsItsTwoColumns()
    {
        var p = TuningFixtures.Load(TuningFixtures.Copter);
        foreach (var n in new[] { "MODE", "REF", "FREQ", "BW", "ATT", "OPTS", "HMNCS" })
            p["INS_HNTCH_" + n] = 1;
        p["INS_NOTCH_ENABLE"] = 0;

        var layout = TuningLayouts.Resolve(TuningLayouts.Copter, Has(p));
        CollectionAssert.AreEqual(new[] { "Enabled", "Mode", "Reference", "Frequency", "Attenuation", "Bandwidth", "Options", "Harmonics" },
            layout.Group("hnotch")!.Params.Select(x => x.Label).ToArray());
        Assert.AreEqual("Static Notch Filter: Enabled=INS_NOTCH_ENABLE", Describe(layout)[^2]);
        CollectionAssert.AreEqual(new[] { 1, 2 }, layout.Rows[4].Cells.Select(c => c.Span).ToArray());
    }

    [TestMethod]
    public void PlaneSitlBasicTuningIsMissionPlannersPage()
    {
        var p = TuningFixtures.Load(TuningFixtures.Plane);
        var layout = TuningLayouts.Resolve(TuningLayouts.Basic(Firmwares.ArduPlane)!, Has(p));

        CollectionAssert.AreEqual(new[]
        {
            "Servo Roll Pid: P=RLL_RATE_P, I=RLL_RATE_I, D=RLL_RATE_D, INT_MAX=RLL_RATE_IMAX",
            "Servo Pitch Pid: P=PTCH_RATE_P, I=PTCH_RATE_I, D=PTCH_RATE_D, INT_MAX=PTCH_RATE_IMAX",
            "Servo Yaw: Yaw 2 roll=YAW2SRV_RLL, Integral=YAW2SRV_INT, Dampening=YAW2SRV_DAMP, Integrator Max=YAW2SRV_IMAX",
            "L1 Control - Turn Control: Period=NAVL1_PERIOD, Damping=NAVL1_DAMPING",
            "TECS: Climb Max (m/s)=TECS_CLMB_MAX, Sink Min (m/s)=TECS_SINK_MIN, Sink Max (m/s)=TECS_SINK_MAX, Pitch Dampening=TECS_PTCH_DAMP, Time Const=TECS_TIME_CONST",
            "Other Mix's: P to T=KFF_THR2PTCH, Rudder Mix=KFF_RDDRMIX",
            "Navigation Angles: Bank Max=ROLL_LIMIT_DEG, Pitch Max=PTCH_LIM_MAX_DEG, Pitch Min=PTCH_LIM_MIN_DEG",
            "Throttle 0-100%: Cruise=TRIM_THROTTLE, Min=THR_MIN, Max=THR_MAX, SlewRate=THR_SLEWRATE",
            "Airspeed m/s: Cruise=AIRSPEED_CRUISE, Min=AIRSPEED_MIN, Max=AIRSPEED_MAX, Ratio=ARSPD_RATIO",
        }, Describe(layout));
        CollectionAssert.AreEqual(new[] { 3, 3 }, layout.Rows.Select(r => r.Cells.Count).ToArray());
        Assert.IsFalse(layout.HasLocks);

        // without QuadPlane a plane has no Extended Tuning
        Assert.AreEqual(0, p["Q_ENABLE"]);
        Assert.IsNull(TuningLayouts.Extended(Firmwares.ArduPlane, Get(p)));
    }

    [TestMethod]
    public void QuadPlaneSitlGetsTheCopterPageWithQNames()
    {
        var p = TuningFixtures.Load(TuningFixtures.QuadPlane);
        Assert.IsTrue(TuningLayouts.QuadPlaneEnabled(Get(p)));
        Assert.AreEqual(TuningLayouts.QuadPlaneExtendedTitle, TuningLayouts.ExtendedTitleFor(Firmwares.ArduPlane));
        var layout = TuningLayouts.Resolve(TuningLayouts.Extended(Firmwares.ArduPlane, Get(p))!, Has(p));

        CollectionAssert.AreEqual(new[]
        {
            "Stabilize Roll (Error to Rate): P=Q_A_ANG_RLL_P, ACCEL MAX=Q_A_ACC_R_MAX",
            "Stabilize Pitch (Error to Rate): P=Q_A_ANG_PIT_P, ACCEL MAX=Q_A_ACC_P_MAX",
            "Stabilize Yaw (Error to Rate): P=Q_A_ANG_YAW_P, ACCEL MAX=Q_A_ACC_Y_MAX",
            "Position XY (Dist to Speed): P=Q_P_NE_POS_P, INPUT TC=Q_A_INPUT_TC",
            "Rate Roll: P=Q_A_RAT_RLL_P, I=Q_A_RAT_RLL_I, D=Q_A_RAT_RLL_D, IMAX=Q_A_RAT_RLL_IMAX, FLTE=Q_A_RAT_RLL_FLTE, FLTD=Q_A_RAT_RLL_FLTD, FLTT=Q_A_RAT_RLL_FLTT",
            "Rate Pitch: P=Q_A_RAT_PIT_P, I=Q_A_RAT_PIT_I, D=Q_A_RAT_PIT_D, IMAX=Q_A_RAT_PIT_IMAX, FLTE=Q_A_RAT_PIT_FLTE, FLTD=Q_A_RAT_PIT_FLTD, FLTT=Q_A_RAT_PIT_FLTT",
            "Rate Yaw: P=Q_A_RAT_YAW_P, I=Q_A_RAT_YAW_I, D=Q_A_RAT_YAW_D, IMAX=Q_A_RAT_YAW_IMAX, FLTE=Q_A_RAT_YAW_FLTE, FLTD=Q_A_RAT_YAW_FLTD, FLTT=Q_A_RAT_YAW_FLTT",
            "Velocity XY (Vel to Accel): P=Q_P_NE_VEL_P, I=Q_P_NE_VEL_I, D=Q_P_NE_VEL_D, IMAX=Q_P_NE_VEL_IMAX",
            "Basic Filters: Gyro=INS_GYRO_FILTER, Accel=INS_ACCEL_FILTER",
            "Throttle Accel (Accel to motor): P=Q_P_D_ACC_P, I=Q_P_D_ACC_I, D=Q_P_D_ACC_D, IMAX=Q_P_D_ACC_IMAX",
            "Throttle Rate (VSpd to accel): P=Q_P_D_VEL_P",
            "Altitude Hold (Alt to climbrate): P=Q_P_D_POS_P",
            "RC Options: RC6 Opt=RC6_OPTION, RC7 Opt=RC7_OPTION, RC8 Opt=RC8_OPTION, RC9 Opt=RC9_OPTION, RC10 Opt=RC10_OPTION",
            "WPNav (m/s): Speed=Q_WP_SPD, Radius=Q_WP_RADIUS_M, Speed Up=Q_WP_SPD_UP, Speed Dn=Q_WP_SPD_DN, Loiter Speed=Q_LOIT_SPEED_MS",
            "Filter Logs: Mask=INS_LOG_BAT_MASK, Options=INS_LOG_BAT_OPT",
            "Harmonic Notch Filter: Enabled=INS_HNTCH_ENABLE",
        }, Describe(layout));

        Assert.AreEqual("Q_A_RAT_PIT_P", layout.LockPartners["Q_A_RAT_RLL_P"]);
        Assert.IsTrue(layout.LockByDefault(Get(p), Has(p)));

        // its Basic Tuning is still the plane page
        Assert.AreEqual("RLL_RATE_P", TuningLayouts.Resolve(TuningLayouts.Basic(Firmwares.ArduPlane)!, Has(p)).NameOf("roll", "P"));
    }

    [TestMethod]
    public void CopterOlderNamesResolveAsMissionPlannerListsThem()
    {
        var p = new Dictionary<string, double>
        {
            ["RATE_RLL_P"] = 0.15, ["RATE_RLL_IMAX"] = 500, ["RATE_RLL_FILT"] = 20, ["RATE_PIT_P"] = 0.15, ["STB_RLL_P"] = 4.5,
            ["ATC_ACCEL_R_MAX"] = 110000, ["THR_ACCEL_P"] = 0.75, ["THR_ACCEL_I"] = 1.5, ["HLD_LAT_P"] = 1, ["LOITER_LAT_P"] = 1,
            ["THR_RATE_P"] = 6, ["THR_ALT_P"] = 1, ["WPNAV_SPEED"] = 500, ["WPNAV_LOIT_SPEED"] = 1000,
            ["TUNE"] = 0, ["TUNE_LOW"] = 0, ["TUNE_HIGH"] = 1000, ["CH7_OPT"] = 4, ["INS_NOTCH_ENABLE"] = 0,
        };

        var layout = TuningLayouts.Resolve(TuningLayouts.Copter, Has(p));

        Assert.AreEqual("RATE_RLL_P", layout.NameOf("rate-rll", "P"));
        Assert.AreEqual("RATE_RLL_IMAX", layout.NameOf("rate-rll", "IMAX"));
        Assert.AreEqual("RATE_RLL_FILT", layout.NameOf("rate-rll", "FLTE"));
        Assert.AreEqual("STB_RLL_P", layout.NameOf("stab-rll", "P"));
        Assert.AreEqual("ATC_ACCEL_R_MAX", layout.NameOf("stab-rll", "ACCEL MAX"));
        Assert.AreEqual("THR_ACCEL_I", layout.NameOf("acc-z", "I"));
        Assert.AreEqual("THR_RATE_P", layout.NameOf("vel-z", "P"));
        Assert.AreEqual("THR_ALT_P", layout.NameOf("pos-z", "P"));
        Assert.AreEqual("HLD_LAT_P", layout.NameOf("pos-xy", "P"));
        Assert.AreEqual("LOITER_LAT_P", layout.NameOf("vel-xy", "P"));
        Assert.AreEqual("WPNAV_LOIT_SPEED", layout.NameOf("wpnav", "Loiter Speed"));
        Assert.AreEqual("WPNav (cm's)", layout.Group("wpnav")!.Title, "centimetre names keep Mission Planner's title");
        Assert.AreEqual("TUNE_LOW", layout.NameOf("tune", "Min"));
        Assert.AreEqual("CH7_OPT", layout.NameOf("rc-options", "RC7 Opt"));
        Assert.AreEqual("INS_NOTCH_ENABLE", layout.NameOf("notch", "Enabled"));
        Assert.AreEqual("RATE_PIT_P", layout.LockPartners["RATE_RLL_P"]);
        Assert.IsFalse(layout.LockPartners.ContainsKey("STB_RLL_P"), "no pitch twin on this vehicle");

        // cards without any parameter are hidden, and so are rows left empty
        Assert.IsNull(layout.Group("rate-yaw"));
        Assert.IsNull(layout.Group("hnotch"));
        Assert.IsFalse(layout.Groups.Any(g => g.Params.Count == 0));
        Assert.IsFalse(layout.Rows.Any(r => r.Cells.Count == 0));
    }

    [TestMethod]
    public void RoverBasicTuningIsConfigArdurover()
    {
        var p = new Dictionary<string, double>
        {
            ["ATC_STR_RAT_P"] = 0.2, ["ATC_STR_RAT_I"] = 0.2, ["ATC_STR_RAT_FF"] = 0.2, ["TURN_RADIUS"] = 0.9,
            ["ATC_SPEED_P"] = 0.2, ["ATC_ACCEL_MAX"] = 1, ["ATC_BRAKE"] = 1, ["CRUISE_SPEED"] = 5, ["CRUISE_THROTTLE"] = 50,
            ["MOT_PWM_TYPE"] = 0, ["MOT_THR_MIN"] = 0, ["MOT_THR_MAX"] = 100,
            ["WP_SPEED"] = 2, ["WP_RADIUS"] = 2, ["ATC_TURN_MAX_G"] = 0.6, ["RNGFND_TRIGGR_CM"] = 100,
            ["RC7_OPTION"] = 0, ["RC8_OPTION"] = 0,
        };

        var layout = TuningLayouts.Resolve(TuningLayouts.Basic(Firmwares.ArduRover)!, Has(p));
        CollectionAssert.AreEqual(new[]
        {
            "Steering Rate: P=ATC_STR_RAT_P, I=ATC_STR_RAT_I, FF=ATC_STR_RAT_FF",
            "Steering Mode: Turn Radius=TURN_RADIUS",
            "RC Options: RC7 Opt=RC7_OPTION, RC8 Opt=RC8_OPTION",
            "Speed/Throttle: P=ATC_SPEED_P, Accel Max (m/s/s)=ATC_ACCEL_MAX, Brake=ATC_BRAKE, Cruise Speed=CRUISE_SPEED, Cruise Throttle=CRUISE_THROTTLE",
            "Throttle and Motors: Motor Type=MOT_PWM_TYPE, Throttle Min (%)=MOT_THR_MIN, Throttle Max (%)=MOT_THR_MAX",
            "Navigation: WP Speed=WP_SPEED, WP Radius=WP_RADIUS, Turn G Max=ATC_TURN_MAX_G",
            "Avoidance: Trigger Dist (cm)=RNGFND_TRIGGR_CM",
        }, Describe(layout));
        Assert.AreEqual(3, layout.Rows.Single().Cells.Count);
        Assert.IsFalse(layout.HasLocks);

        // older rover names come first in Mission Planner's lists
        p["STEER2SRV_P"] = 1.8;
        Assert.AreEqual("STEER2SRV_P", TuningLayouts.Resolve(TuningLayouts.Rover, Has(p)).NameOf("steer-rate", "P"));

        Assert.IsNull(TuningLayouts.Extended(Firmwares.ArduRover, Get(p)), "rovers have no Extended Tuning");
        Assert.IsNull(TuningLayouts.Basic(Firmwares.ArduSub));
        Assert.IsNull(TuningLayouts.Extended(Firmwares.ArduTracker, Get(p)));
    }

    [TestMethod]
    public void QuadPlaneNamesComeFromTheCopterNames()
    {
        Assert.AreEqual("Q_A_RAT_RLL_P", TuningLayouts.QuadPlaneName("ATC_RAT_RLL_P"));
        Assert.AreEqual("Q_A_ACCEL_R_MAX", TuningLayouts.QuadPlaneName("ATC_ACCEL_R_MAX"));
        Assert.AreEqual("Q_P_D_ACC_P", TuningLayouts.QuadPlaneName("PSC_D_ACC_P"));
        Assert.AreEqual("Q_P_VELXY_P", TuningLayouts.QuadPlaneName("PSC_VELXY_P"));
        Assert.AreEqual("Q_WP_SPEED", TuningLayouts.QuadPlaneName("WPNAV_SPEED"));
        Assert.AreEqual("Q_WP_SPD", TuningLayouts.QuadPlaneName("WP_SPD"));
        Assert.AreEqual("Q_LOIT_SPEED_MS", TuningLayouts.QuadPlaneName("LOIT_SPEED_MS"));
        Assert.IsNull(TuningLayouts.QuadPlaneName("RATE_RLL_P"));
        Assert.IsNull(TuningLayouts.QuadPlaneName("INS_GYRO_FILTER"));

        // each copter value lists its QuadPlane twins after the copter names, as Mission Planner does
        var rate = TuningLayouts.Copter.Groups.Single(g => g.Id == "rate-rll");
        CollectionAssert.AreEqual(new[] { "RATE_RLL_P", "ATC_RAT_RLL_P", "Q_A_RAT_RLL_P" }, rate.Params[0].Aliases.ToArray());
    }

    [TestMethod]
    public void LayoutsAreConsistent()
    {
        foreach (var layout in new[] { TuningLayouts.Copter, TuningLayouts.Plane, TuningLayouts.Rover })
        {
            var ids = layout.Groups.Select(g => g.Id).ToList();
            Assert.AreEqual(ids.Count, ids.Distinct().Count(), "card ids are unique in " + layout.Name);
            foreach (var l in layout.Locks)
            {
                CollectionAssert.Contains(ids, l.RollGroup);
                CollectionAssert.Contains(ids, l.PitchGroup);
            }

            var aliases = new HashSet<string>();
            foreach (var g in layout.Groups)
            {
                Assert.IsNotEmpty(g.Params, g.Id);
                Assert.IsTrue(g.Params.All(x => x.Aliases.Count > 0), g.Id);
                Assert.AreEqual(g.Params.Count, g.Params.Select(x => x.Label).Distinct().Count(), "labels are unique in " + g.Id);
                foreach (var a in g.Params.SelectMany(x => x.Aliases))
                    Assert.IsTrue(aliases.Add(a), a + " is listed twice in " + layout.Name);
            }

            Assert.IsTrue(layout.Rows.SelectMany(r => r.Cells).All(c => c.Span >= 1 && c.Groups.Count > 0));
        }
    }

    [TestMethod]
    public void LockStartsOnOnlyWhenRollAndPitchAgreeAndNotOnAHeli()
    {
        var p = TuningFixtures.Load(TuningFixtures.Copter);
        var layout = TuningLayouts.Resolve(TuningLayouts.Copter, Has(p));
        Assert.IsTrue(layout.LockByDefault(Get(p), Has(p)));

        p["ATC_ANG_PIT_P"] = 6;
        Assert.IsFalse(layout.LockByDefault(Get(p), Has(p)), "already different");

        p["ATC_ANG_PIT_P"] = 4.5;
        p["H_SWASH_TYPE"] = 0;
        Assert.IsFalse(layout.LockByDefault(Get(p), Has(p)), "helicopter");
    }
}
