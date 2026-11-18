using MissionPlanner.App.Core.ViewModels.ConfigScreens;
using MissionPlanner.ArduPilot;

namespace MissionPlanner.App.Tests.ConfigScreens;

/// <summary>Extended Tuning cards matched against parameter sets: aliases, hidden cards, QuadPlane and the roll/pitch lock.</summary>
[TestClass]
public class TuningLayoutTests
{
    private static Func<string, bool> Has(IDictionary<string, double> p) => p.ContainsKey;

    private static Func<string, double?> Get(IDictionary<string, double> p) => n => p.TryGetValue(n, out var v) ? v : null;

    private static ResolvedTuningLayout Resolve(Firmwares fw, Dictionary<string, double> p) =>
        TuningLayouts.Resolve(TuningLayouts.For(fw, Get(p))!, Has(p));

    [TestMethod]
    public void CopterTakesTheFirstExistingAliasAndHidesEmptyCards()
    {
        var p = new Dictionary<string, double>
        {
            ["ATC_RAT_RLL_P"] = 0.135, ["ATC_RAT_RLL_I"] = 0.135, ["ATC_RAT_RLL_D"] = 0.0036, ["ATC_RAT_RLL_FLTD"] = 20,
            ["ATC_RAT_PIT_P"] = 0.135, ["ATC_RAT_PIT_I"] = 0.135, ["ATC_RAT_PIT_D"] = 0.0036, ["ATC_RAT_PIT_FLTD"] = 20,
            ["ATC_ANG_RLL_P"] = 4.5, ["ATC_ACC_R_MAX"] = 110,
            // both the 4.7 name and the older one: the first alias (newest) wins, the other is not shown twice
            ["PSC_D_ACC_P"] = 0.05, ["PSC_ACCZ_P"] = 0.5,
            ["WP_SPD"] = 10, ["WPNAV_SPEED"] = 1000,
            ["INS_HNTCH_ENABLE"] = 1, ["INS_HNTCH_FREQ"] = 80,
            ["RC7_OPTION"] = 0,
        };

        var layout = Resolve(Firmwares.ArduCopter2, p);

        Assert.AreEqual("ATC_RAT_RLL_P", layout.NameOf("rate-rll", "P"));
        Assert.AreEqual("ATC_RAT_RLL_FLTD", layout.NameOf("rate-rll", "FLTD"));
        Assert.IsNull(layout.NameOf("rate-rll", "IMAX"));
        Assert.AreEqual("ATC_ACC_R_MAX", layout.NameOf("stab-rll", "Accel Max"));
        Assert.AreEqual("PSC_D_ACC_P", layout.NameOf("acc-z", "P"));
        Assert.AreEqual("WP_SPD", layout.NameOf("wpnav", "Speed"));
        Assert.AreEqual("RC7_OPTION", layout.NameOf("rc-options", "RC7 Option"));
        Assert.AreEqual(1, layout.Names.Count(n => n == "PSC_D_ACC_P"));
        Assert.IsFalse(layout.Names.Contains("PSC_ACCZ_P"));
        Assert.IsFalse(layout.Names.Contains("WPNAV_SPEED"));

        // cards without any parameter are hidden
        Assert.IsNotNull(layout.Group("hnotch"));
        Assert.IsNull(layout.Group("notch"));
        Assert.IsNull(layout.Group("rate-yaw"));
        Assert.IsNull(layout.Group("tune"));
        Assert.IsFalse(layout.Groups.Any(g => g.Params.Count == 0));
        Assert.IsFalse(layout.Groups.Any(g => g.Section == TuningLayouts.QuadPlaneSection));
    }

    [TestMethod]
    public void CopterOlderNamesResolve()
    {
        var p = new Dictionary<string, double>
        {
            ["RATE_RLL_P"] = 0.15, ["RATE_RLL_IMAX"] = 500, ["RATE_PIT_P"] = 0.15, ["STB_RLL_P"] = 4.5,
            ["THR_ACCEL_P"] = 0.75, ["THR_ACCEL_I"] = 1.5, ["HLD_LAT_P"] = 1, ["LOITER_LAT_P"] = 1,
            ["WPNAV_SPEED"] = 500, ["WPNAV_LOIT_SPEED"] = 1000, ["TUNE"] = 0, ["TUNE_LOW"] = 0, ["TUNE_HIGH"] = 1000,
            ["CH7_OPT"] = 4, ["RC_FEEL_RP"] = 100,
        };

        var layout = Resolve(Firmwares.ArduCopter2, p);

        Assert.AreEqual("RATE_RLL_P", layout.NameOf("rate-rll", "P"));
        Assert.AreEqual("RATE_RLL_IMAX", layout.NameOf("rate-rll", "IMAX"));
        Assert.AreEqual("STB_RLL_P", layout.NameOf("stab-rll", "P"));
        Assert.AreEqual("THR_ACCEL_I", layout.NameOf("acc-z", "I"));
        Assert.AreEqual("HLD_LAT_P", layout.NameOf("pos-xy", "P"));
        Assert.AreEqual("LOITER_LAT_P", layout.NameOf("vel-xy", "P"));
        Assert.AreEqual("WPNAV_LOIT_SPEED", layout.NameOf("wpnav", "Loiter Speed"));
        Assert.AreEqual("TUNE_LOW", layout.NameOf("tune", "Min"));
        Assert.AreEqual("CH7_OPT", layout.NameOf("rc-options", "RC7 Option"));
        Assert.AreEqual("RC_FEEL_RP", layout.NameOf("input", "RC Feel"));
        Assert.AreEqual("RATE_PIT_P", layout.LockPartners["RATE_RLL_P"]);
    }

    [TestMethod]
    public void QuadPlaneCardsFollowQEnable()
    {
        var p = new Dictionary<string, double>
        {
            ["RLL_RATE_P"] = 0.08, ["RLL_RATE_FF"] = 0.35, ["PTCH_RATE_P"] = 0.04, ["NAVL1_PERIOD"] = 17, ["TECS_TIME_CONST"] = 5,
            ["AIRSPEED_CRUISE"] = 22, ["TRIM_THROTTLE"] = 45,
            ["Q_ENABLE"] = 0,
            ["Q_A_RAT_RLL_P"] = 0.25, ["Q_A_RAT_PIT_P"] = 0.25, ["Q_A_RAT_RLL_I"] = 0.25, ["Q_A_RAT_PIT_I"] = 0.2,
            ["Q_A_ANG_RLL_P"] = 4.5, ["Q_P_D_ACC_P"] = 0.03, ["Q_WP_SPD"] = 5, ["Q_LOIT_SPEED_MS"] = 5, ["Q_A_INPUT_TC"] = 0.2,
            ["INS_GYRO_FILTER"] = 20,
        };

        var off = Resolve(Firmwares.ArduPlane, p);
        Assert.AreEqual("RLL_RATE_P", off.NameOf("roll", "P"));
        Assert.AreEqual("RLL_RATE_FF", off.NameOf("roll", "FF"));
        Assert.AreEqual("NAVL1_PERIOD", off.NameOf("l1", "Period"));
        Assert.AreEqual("AIRSPEED_CRUISE", off.NameOf("airspeed", "Cruise"));
        Assert.IsFalse(off.Groups.Any(g => g.Section == TuningLayouts.QuadPlaneSection), "Q_ENABLE=0 hides the VTOL cards");
        Assert.IsFalse(off.HasLocks);
        Assert.IsNull(off.Group("arsp2ptch"), "older controllers hide when the vehicle has none");
        Assert.AreEqual("INS_GYRO_FILTER", off.NameOf("filters", "Gyro"));

        p["Q_ENABLE"] = 1;
        var on = Resolve(Firmwares.ArduPlane, p);
        Assert.AreEqual("Q_A_RAT_RLL_P", on.NameOf("q-rate-rll", "P"));
        Assert.AreEqual("Q_A_ANG_RLL_P", on.NameOf("q-stab-rll", "P"));
        Assert.AreEqual("Q_P_D_ACC_P", on.NameOf("q-acc-z", "P"));
        Assert.AreEqual("Q_WP_SPD", on.NameOf("q-wpnav", "Speed"));
        Assert.AreEqual("Q_LOIT_SPEED_MS", on.NameOf("q-wpnav", "Loiter Speed"));
        Assert.AreEqual("Q_A_INPUT_TC", on.NameOf("q-input", "Input TC"));
        Assert.AreEqual("VTOL Rate Roll", on.Group("q-rate-rll")!.Title);
        Assert.IsTrue(on.Groups.Where(g => g.Id.StartsWith("q-")).All(g => g.Section == TuningLayouts.QuadPlaneSection));

        // the lock pairs the QuadPlane rate controllers
        Assert.AreEqual("Q_A_RAT_PIT_P", on.LockPartners["Q_A_RAT_RLL_P"]);
        Assert.AreEqual("Q_A_RAT_RLL_I", on.LockPartners["Q_A_RAT_PIT_I"]);
        Assert.IsFalse(on.LockByDefault(Get(p), Has(p)), "I terms differ");
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
        Assert.AreEqual("Q_LOIT_SPEED", TuningLayouts.QuadPlaneName("LOIT_SPEED"));
        Assert.AreEqual("Q_M_THST_HOVER", TuningLayouts.QuadPlaneName("MOT_THST_HOVER"));
        Assert.IsNull(TuningLayouts.QuadPlaneName("RATE_RLL_P"));
        Assert.IsNull(TuningLayouts.QuadPlaneName("INS_GYRO_FILTER"));

        // older copter names have no twin: those values are dropped from the VTOL cards
        var rate = TuningLayouts.QuadPlane.Groups.Single(g => g.Id == "q-rate-rll");
        CollectionAssert.AreEqual(new[] { "Q_A_RAT_RLL_P" }, rate.Params.Single(x => x.Label == "P").Aliases.ToArray());
    }

    [TestMethod]
    public void RoverAndOtherVehicles()
    {
        var p = new Dictionary<string, double>
        {
            ["ATC_STR_RAT_P"] = 0.2, ["ATC_STR_RAT_FF"] = 0.2, ["ATC_SPEED_P"] = 0.2, ["CRUISE_SPEED"] = 5, ["MOT_THR_MAX"] = 100,
            ["WP_SPEED"] = 2, ["RNGFND_TRIGGR_CM"] = 100, ["RC7_OPTION"] = 0,
        };

        var layout = Resolve(Firmwares.ArduRover, p);
        Assert.AreEqual("ATC_STR_RAT_P", layout.NameOf("steer-rate", "P"));
        Assert.AreEqual("ATC_SPEED_P", layout.NameOf("speed", "P"));
        Assert.AreEqual("MOT_THR_MAX", layout.NameOf("motors", "Throttle Max"));
        Assert.AreEqual("RNGFND_TRIGGR_CM", layout.NameOf("avoid", "Trigger Dist"));
        Assert.AreEqual("RC7_OPTION", layout.NameOf("rover-rc-options", "RC7 Option"));
        Assert.IsFalse(layout.HasLocks);

        Assert.IsNull(TuningLayouts.For(Firmwares.ArduSub, Get(p)));
        Assert.IsNull(TuningLayouts.For(Firmwares.ArduTracker, Get(p)));
    }

    [TestMethod]
    public void LayoutsAreConsistent()
    {
        foreach (var layout in new[] { TuningLayouts.Copter, TuningLayouts.Plane, TuningLayouts.PlaneWithQuadPlane, TuningLayouts.Rover })
        {
            var ids = layout.Groups.Select(g => g.Id).ToList();
            Assert.AreEqual(ids.Count, ids.Distinct().Count(), "card ids are unique");
            foreach (var l in layout.Locks)
            {
                CollectionAssert.Contains(ids, l.RollGroup);
                CollectionAssert.Contains(ids, l.PitchGroup);
            }

            foreach (var g in layout.Groups)
            {
                Assert.IsTrue(g.Params.Count > 0, g.Id);
                Assert.IsTrue(g.Params.All(x => x.Aliases.Count > 0), g.Id);
                Assert.AreEqual(g.Params.Count, g.Params.Select(x => x.Label).Distinct().Count(), "labels are unique in " + g.Id);
            }
        }
    }

    [TestMethod]
    public void LockStartsOnOnlyWhenRollAndPitchAgreeAndNotOnAHeli()
    {
        var p = new Dictionary<string, double>
        {
            ["ATC_RAT_RLL_P"] = 0.135, ["ATC_RAT_PIT_P"] = 0.135, ["ATC_RAT_RLL_I"] = 0.135, ["ATC_RAT_PIT_I"] = 0.135,
            ["ATC_RAT_YAW_P"] = 0.18, ["ATC_ANG_RLL_P"] = 4.5, ["ATC_ANG_PIT_P"] = 4.5,
        };

        var layout = Resolve(Firmwares.ArduCopter2, p);
        Assert.AreEqual("ATC_RAT_PIT_P", layout.LockPartners["ATC_RAT_RLL_P"]);
        Assert.AreEqual("ATC_RAT_RLL_I", layout.LockPartners["ATC_RAT_PIT_I"]);
        Assert.AreEqual("ATC_ANG_PIT_P", layout.LockPartners["ATC_ANG_RLL_P"]);
        Assert.IsFalse(layout.LockPartners.ContainsKey("ATC_RAT_YAW_P"));
        Assert.IsTrue(layout.LockByDefault(Get(p), Has(p)));

        p["ATC_ANG_PIT_P"] = 6;
        Assert.IsFalse(layout.LockByDefault(Get(p), Has(p)), "already different");

        p["ATC_ANG_PIT_P"] = 4.5;
        p["H_SWASH_TYPE"] = 0;
        Assert.IsFalse(layout.LockByDefault(Get(p), Has(p)), "helicopter");
    }
}
