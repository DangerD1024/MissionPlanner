using MissionPlanner.App.Core.ViewModels.SetupScreens.OptionalHardware;

namespace MissionPlanner.App.Tests.SetupScreens.OptionalHardware;

[TestClass]
public sealed class InitialParamsCalculatorTests
{
    private static readonly string[] Copter46 =
    [
        "ACRO_YAW_P", "ATC_ACCEL_P_MAX", "ATC_ACCEL_R_MAX", "ATC_ACCEL_Y_MAX", "ATC_RAT_PIT_FLTD", "ATC_RAT_PIT_FLTE",
        "ATC_RAT_PIT_FLTT", "ATC_THR_MIX_MAN", "INS_ACCEL_FILTER", "INS_GYRO_FILTER", "MOT_THST_EXPO", "MOT_THST_HOVER",
        "BATT_ARM_VOLT", "BATT_CRT_VOLT", "BATT_LOW_VOLT", "MOT_BAT_VOLT_MAX", "MOT_BAT_VOLT_MIN", "PSC_ACCZ_P",
    ];

    private static InitialParamsVehicle Vehicle(bool plane = false, int major = 4, params string[] names) =>
        new(plane, major, names.ToHashSet().Contains);

    private static Dictionary<string, double> Calc(InitialParamsInput input, InitialParamsVehicle vehicle) =>
        InitialParamsCalculator.Calculate(input, vehicle).ToDictionary(v => v.Name, v => v.Value);

    [TestMethod]
    public void NineInchFourCellLipo_MatchesMissionPlanner()
    {
        var (max, min) = InitialParamsCalculator.CellVoltages(BatteryChemistry.LiPo);
        var p = Calc(new InitialParamsInput(9, 4, max, min), Vehicle(names: Copter46));

        // ATC_ACCEL_Y_MAX = max(8000, round(-900*9 + 36000, -2)); ACRO_YAW_P = 0.5 * that / 4500
        Assert.AreEqual(27900, p["ATC_ACCEL_Y_MAX"]);
        Assert.AreEqual(3.1, p["ACRO_YAW_P"], 1e-9);
        // cubic in prop size, rounded to hundreds
        Assert.AreEqual(125900, p["ATC_ACCEL_P_MAX"]);
        Assert.AreEqual(125900, p["ATC_ACCEL_R_MAX"]);
        // 289.22 * 9^-0.838 = 45.9
        Assert.AreEqual(46, p["INS_GYRO_FILTER"]);
        Assert.AreEqual(23, p["ATC_RAT_PIT_FLTD"]);
        Assert.AreEqual(0, p["ATC_RAT_PIT_FLTE"]);
        Assert.AreEqual(23, p["ATC_RAT_RLL_FLTT"]);
        Assert.AreEqual(0, p["ATC_RAT_YAW_FLTD"]);
        Assert.AreEqual(2, p["ATC_RAT_YAW_FLTE"]);
        Assert.AreEqual(0.1, p["ATC_THR_MIX_MAN"]);
        Assert.AreEqual(10, p["INS_ACCEL_FILTER"]);
        Assert.AreEqual(0.58, p["MOT_THST_EXPO"]);
        Assert.AreEqual(0.2, p["MOT_THST_HOVER"]);
        Assert.AreEqual(14.7, p["BATT_ARM_VOLT"]);
        Assert.AreEqual(14.0, p["BATT_CRT_VOLT"]);
        Assert.AreEqual(14.4, p["BATT_LOW_VOLT"]);
        Assert.AreEqual(16.8, p["MOT_BAT_VOLT_MAX"]);
        Assert.AreEqual(13.2, p["MOT_BAT_VOLT_MIN"]);
        Assert.IsFalse(p.ContainsKey("MOT_PWM_MIN"));
        Assert.IsFalse(p.ContainsKey("FENCE_ENABLE"));
    }

    [TestMethod]
    public void LargePropsAndSmallProps_HitTheFloors()
    {
        var big = Calc(new InitialParamsInput(40, 12, 4.2, 3.3), Vehicle(names: Copter46));
        Assert.AreEqual(8000, big["ATC_ACCEL_Y_MAX"], "yaw accel never below 8000");
        Assert.AreEqual(20, big["INS_GYRO_FILTER"], "gyro filter never below 20");
        Assert.AreEqual(10, big["ATC_RAT_PIT_FLTD"], "rate filters never below 10");

        var tiny = Calc(new InitialParamsInput(3, 1, 4.2, 3.3), Vehicle(names: Copter46));
        Assert.AreEqual(Math.Min(Math.Round(0.15686 * Math.Log(3) + 0.23693, 2), 0.8), tiny["MOT_THST_EXPO"]);
        Assert.AreEqual(3.6, tiny["BATT_ARM_VOLT"], 1e-9, "one cell: no per-cell margin");
    }

    [TestMethod]
    public void Copter47_UsesTheRenamedAccelerationLimitsInDegrees()
    {
        var p = Calc(new InitialParamsInput(9, 4, 4.2, 3.3), Vehicle(names: ["ATC_RAT_PIT_FLTD", "PSC_D_ACC_P"]));
        Assert.IsFalse(p.ContainsKey("ATC_ACCEL_P_MAX"));
        Assert.AreEqual(1259, p["ATC_ACC_P_MAX"]);
        Assert.AreEqual(1259, p["ATC_ACC_R_MAX"]);
        Assert.AreEqual(279, p["ATC_ACC_Y_MAX"]);
    }

    [TestMethod]
    public void Copter3x_UsesSingleRateFilters()
    {
        var p = Calc(new InitialParamsInput(9, 4, 4.2, 3.3), Vehicle(major: 3, names: ["ATC_ACCEL_P_MAX", "ATC_RAT_PIT_FILT"]));
        Assert.AreEqual(23, p["ATC_RAT_PIT_FILT"]);
        Assert.AreEqual(23, p["ATC_RAT_RLL_FILT"]);
        Assert.AreEqual(2, p["ATC_RAT_YAW_FILT"]);
        Assert.IsFalse(p.ContainsKey("ATC_RAT_PIT_FLTD"));
    }

    [TestMethod]
    public void QuadPlane_UsesQPrefixes_AndNeverAddsTheCopterSuggestions()
    {
        var vehicle = Vehicle(plane: true, names: ["Q_A_RAT_PIT_FLTD"]);
        var p = Calc(new InitialParamsInput(12, 6, 4.2, 3.3, AddSuggested: true), vehicle);
        Assert.IsTrue(p.ContainsKey("Q_A_ACC_P_MAX"));
        Assert.IsTrue(p.ContainsKey("Q_A_RAT_RLL_FLTD"));
        Assert.IsTrue(p.ContainsKey("Q_M_THST_EXPO"));
        Assert.IsTrue(p.ContainsKey("Q_M_BAT_VOLT_MAX"));
        Assert.IsFalse(p.Keys.Any(k => k.StartsWith("ATC_") || k.StartsWith("MOT_")));
        Assert.IsFalse(p.ContainsKey("FENCE_ENABLE"));
        Assert.IsFalse(InitialParamsCalculator.SuggestedApplies(vehicle));
        StringAssert.Contains(InitialParamsCalculator.AfterFlightAdvice(vehicle), "Q_A_THR_MIX_MAN");
        StringAssert.Contains(InitialParamsCalculator.AfterFlightAdvice(vehicle), "Q_P_ACCZ_P to Q_M_THST_HOVER");
    }

    [TestMethod]
    public void TMotorAndSuggested_AddTheirParameters()
    {
        var p = Calc(new InitialParamsInput(9, 4, 4.2, 3.3, TMotorFlameEsc: true, AddSuggested: true), Vehicle(names: Copter46));
        Assert.AreEqual(0.2, p["MOT_THST_EXPO"], "T-Motor Flame ESCs are linear");
        Assert.AreEqual(1100, p["MOT_PWM_MIN"]);
        Assert.AreEqual(1940, p["MOT_PWM_MAX"]);
        Assert.AreEqual(1, p["BATT_FS_CRT_ACT"]);
        Assert.AreEqual(2, p["BATT_FS_LOW_ACT"]);
        Assert.AreEqual(3, p["FENCE_ACTION"]);
        Assert.AreEqual(120, p["FENCE_ALT_MAX"]);
        Assert.AreEqual(1, p["FENCE_ENABLE"]);
        Assert.AreEqual(150, p["FENCE_RADIUS"]);
        Assert.AreEqual(7, p["FENCE_TYPE"]);
    }

    [TestMethod]
    public void ChemistryPresets()
    {
        Assert.AreEqual((4.2, 3.3), InitialParamsCalculator.CellVoltages(BatteryChemistry.LiPo));
        Assert.AreEqual((4.35, 3.3), InitialParamsCalculator.CellVoltages(BatteryChemistry.LiPoHV));
        Assert.AreEqual((4.1, 2.8), InitialParamsCalculator.CellVoltages(BatteryChemistry.LiIon));
    }

    [TestMethod]
    public void RoundTo_NegativePrecisionRoundsHalfUpToTensAndHundreds()
    {
        Assert.AreEqual(27900, InitialParamsCalculator.RoundTo(27900, -2));
        Assert.AreEqual(1300, InitialParamsCalculator.RoundTo(1250, -2));
        Assert.AreEqual(1200, InitialParamsCalculator.RoundTo(1249.9, -2));
        Assert.AreEqual(130, InitialParamsCalculator.RoundTo(125, -1));
        Assert.AreEqual(1.23, InitialParamsCalculator.RoundTo(1.2345, 2));
    }

    [TestMethod]
    public void Validate_RejectsUnusableInputs()
    {
        Assert.IsNull(InitialParamsCalculator.Validate(new InitialParamsInput(9, 4, 4.2, 3.3)));
        Assert.IsNotNull(InitialParamsCalculator.Validate(new InitialParamsInput(0, 4, 4.2, 3.3)));
        Assert.IsNotNull(InitialParamsCalculator.Validate(new InitialParamsInput(9, 0, 4.2, 3.3)));
        Assert.IsNotNull(InitialParamsCalculator.Validate(new InitialParamsInput(9, 4, 3.3, 4.2)));
        Assert.IsNotNull(InitialParamsCalculator.Validate(new InitialParamsInput(double.NaN, 4, 4.2, 3.3)));
    }

    [TestMethod]
    public void AfterFlightAdvice_FollowsTheAccelerationGainNames()
    {
        StringAssert.Contains(InitialParamsCalculator.AfterFlightAdvice(Vehicle(names: ["PSC_ACCZ_P"])), "Set PSC_ACCZ_I to 2 × MOT_THST_HOVER");
        StringAssert.Contains(InitialParamsCalculator.AfterFlightAdvice(Vehicle(names: ["PSC_D_ACC_P"])), "Set PSC_D_ACC_P to 0.1 × MOT_THST_HOVER");
    }
}
