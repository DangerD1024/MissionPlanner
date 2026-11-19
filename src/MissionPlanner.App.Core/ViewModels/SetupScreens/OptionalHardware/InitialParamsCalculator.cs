namespace MissionPlanner.App.Core.ViewModels.SetupScreens.OptionalHardware;

public enum BatteryChemistry
{
    LiPo,
    LiPoHV,
    LiIon,
}

/// <summary>What the initial tune is calculated from (ConfigInitialParams' inputs).</summary>
public sealed record InitialParamsInput(
    double PropInches,
    int Cells,
    double CellMaxVolts,
    double CellMinVolts,
    bool TMotorFlameEsc = false,
    bool AddSuggested = false);

/// <summary>The vehicle facts the parameter names depend on.</summary>
/// <param name="IsPlane">QuadPlane: Q_A_ / Q_M_ instead of ATC_ / MOT_.</param>
/// <param name="VersionMajor">Firmware major version (filter names and the suggested settings depend on it).</param>
/// <param name="Has">Whether the vehicle has a parameter.</param>
public sealed record InitialParamsVehicle(bool IsPlane, int VersionMajor, Func<string, bool> Has);

/// <summary>One calculated parameter value.</summary>
public sealed record InitialParamValue(string Name, double Value);

/// <summary>
/// Mission Planner's copter initial-tune calculator (ConfigInitialParams.calc_values), from the
/// ArduPilot "Tuning Process Instructions": filters, acceleration limits and thrust expo from the
/// propeller size, battery voltages from the cell count and chemistry.
/// </summary>
public static class InitialParamsCalculator
{
    public const string TuningGuideUrl = "https://ardupilot.org/copter/docs/tuning-process-instructions.html";

    /// <summary>Fully charged / fully discharged cell voltage of a chemistry (Mission Planner's presets).</summary>
    public static (double Max, double Min) CellVoltages(BatteryChemistry chemistry) => chemistry switch
    {
        BatteryChemistry.LiPoHV => (4.35, 3.3),
        BatteryChemistry.LiIon => (4.1, 2.8),
        _ => (4.2, 3.3),
    };

    /// <summary>
    /// Rounds to <paramref name="precision"/> decimals; a negative precision rounds to tens,
    /// hundreds... (half up), as Mission Planner's RoundTo.
    /// </summary>
    public static double RoundTo(double value, int precision)
    {
        if (precision >= 0)
            return Math.Round(value, precision);
        var step = (int)Math.Pow(10, Math.Abs(precision));
        value += 5 * step / 10;
        return Math.Round(value - value % step, 0);
    }

    /// <summary>Checks the inputs; null when they are usable, otherwise a message for the user.</summary>
    public static string? Validate(InitialParamsInput input)
    {
        if (!(input.PropInches > 0) || double.IsInfinity(input.PropInches))
            return "Prop size must be larger than zero.";
        if (input.Cells < 1)
            return "Battery cell count must be at least 1.";
        if (!(input.CellMaxVolts > 0) || !(input.CellMinVolts > 0))
            return "Cell voltages must be larger than zero.";
        if (input.CellMinVolts >= input.CellMaxVolts)
            return "The discharged cell voltage must be lower than the fully charged one.";
        return null;
    }

    /// <summary>The parameters to set, in Mission Planner's order. Call <see cref="Validate"/> first.</summary>
    public static IReadOnlyList<InitialParamValue> Calculate(InitialParamsInput input, InitialParamsVehicle vehicle)
    {
        var prop = input.PropInches;
        double cells = input.Cells;
        var cellMax = input.CellMaxVolts;
        var cellMin = input.CellMinVolts;

        var accelYMax = Math.Max(8000, RoundTo(-900 * prop + 36000, -2));
        var acroYawP = 0.5 * accelYMax / 4500;
        var accelPMax = Math.Max(10000, RoundTo(-2.613267 * Math.Pow(prop, 3) + 343.39216 * Math.Pow(prop, 2) - 15083.7121 * prop + 235771, -2));
        var accelRMax = accelPMax;
        var gyroFilter = Math.Max(20, Math.Round(289.22 * Math.Pow(prop, -0.838), 0));
        var rateFilter = Math.Max(10, gyroFilter / 2);
        const double thrMixMan = 0.1;
        const double accelFilter = 10;
        var thrustExpo = input.TMotorFlameEsc ? 0.2 : Math.Min(Math.Round(0.15686 * Math.Log(prop) + 0.23693, 2), 0.80);
        const double thrustHover = 0.2;

        var battArm = (cells - 1) * 0.1 + (cellMin + 0.3) * cells;
        var battCrt = (cellMin + 0.2) * cells;
        var battLow = (cellMin + 0.3) * cells;
        var motBatMax = cellMax * cells;
        var motBatMin = cellMin * cells;

        var atc = vehicle.IsPlane ? "Q_A" : "ATC";
        var mot = vehicle.IsPlane ? "Q_M" : "MOT";

        var list = new List<InitialParamValue>();
        void Add(string name, double value) => list.Add(new InitialParamValue(name, Clean(value)));

        Add("ACRO_YAW_P", acroYawP);
        if (vehicle.Has(atc + "_ACCEL_P_MAX"))
        {
            Add(atc + "_ACCEL_P_MAX", accelPMax);
            Add(atc + "_ACCEL_R_MAX", accelRMax);
            Add(atc + "_ACCEL_Y_MAX", accelYMax);
        }
        else
        {
            // 4.7 renamed the limits and changed them to deg/s/s
            Add(atc + "_ACC_P_MAX", accelPMax / 100.0);
            Add(atc + "_ACC_R_MAX", accelRMax / 100.0);
            Add(atc + "_ACC_Y_MAX", accelYMax / 100.0);
        }

        if (UsesSplitRateFilters(atc, vehicle))
        {
            Add(atc + "_RAT_PIT_FLTD", rateFilter);
            Add(atc + "_RAT_PIT_FLTE", 0);
            Add(atc + "_RAT_PIT_FLTT", rateFilter);
            Add(atc + "_RAT_RLL_FLTD", rateFilter);
            Add(atc + "_RAT_RLL_FLTE", 0);
            Add(atc + "_RAT_RLL_FLTT", rateFilter);
            Add(atc + "_RAT_YAW_FLTD", 0);
            Add(atc + "_RAT_YAW_FLTE", 2);
            Add(atc + "_RAT_YAW_FLTT", rateFilter);
        }
        else
        {
            Add(atc + "_RAT_PIT_FILT", rateFilter);
            Add(atc + "_RAT_RLL_FILT", rateFilter);
            Add(atc + "_RAT_YAW_FILT", 2);
        }

        Add(atc + "_THR_MIX_MAN", thrMixMan);
        Add("INS_ACCEL_FILTER", accelFilter);
        Add("INS_GYRO_FILTER", gyroFilter);
        Add(mot + "_THST_EXPO", thrustExpo);
        Add(mot + "_THST_HOVER", thrustHover);
        Add("BATT_ARM_VOLT", battArm);
        Add("BATT_CRT_VOLT", battCrt);
        Add("BATT_LOW_VOLT", battLow);
        Add(mot + "_BAT_VOLT_MAX", motBatMax);
        Add(mot + "_BAT_VOLT_MIN", motBatMin);

        if (input.TMotorFlameEsc)
        {
            Add(mot + "_PWM_MIN", 1100);
            Add(mot + "_PWM_MAX", 1940);
        }

        if (input.AddSuggested && SuggestedApplies(vehicle))
        {
            Add("BATT_FS_CRT_ACT", 1);
            Add("BATT_FS_LOW_ACT", 2);
            Add("FENCE_ACTION", 3);
            Add("FENCE_ALT_MAX", 120);
            Add("FENCE_ENABLE", 1);
            Add("FENCE_RADIUS", 150);
            Add("FENCE_TYPE", 7);
        }

        return list;
    }

    /// <summary>The battery failsafe and fence suggestions are for Copter 4.0 and later.</summary>
    public static bool SuggestedApplies(InitialParamsVehicle vehicle) => !vehicle.IsPlane && vehicle.VersionMajor >= 4;

    /// <summary>
    /// What to do after the first test flight (Mission Planner's closing message): raise the manual
    /// throttle mix and set the vertical acceleration gains from the learned hover throttle.
    /// </summary>
    public static string AfterFlightAdvice(InitialParamsVehicle vehicle)
    {
        var atc = vehicle.IsPlane ? "Q_A" : "ATC";
        var mot = vehicle.IsPlane ? "Q_M" : "MOT";
        var psc = vehicle.IsPlane ? "Q_P" : "PSC";
        var hover = mot + "_THST_HOVER";
        // 4.7 renamed the vertical acceleration gains and made them 10x smaller
        var accel = vehicle.Has(psc + "_D_ACC_P")
            ? $"Set {psc}_D_ACC_P to 0.1 × {hover}\nSet {psc}_D_ACC_I to 0.2 × {hover}"
            : $"Set {psc}_ACCZ_P to {hover}\nSet {psc}_ACCZ_I to 2 × {hover}";
        return $"After a test flight (land and disarm first):\nSet {atc}_THR_MIX_MAN to 0.5\n{accel}";
    }

    // Copter 4.0 split the rate filters into FLTD/FLTE/FLTT; earlier versions had one FILT each
    private static bool UsesSplitRateFilters(string atc, InitialParamsVehicle vehicle)
    {
        if (vehicle.Has(atc + "_RAT_PIT_FLTD"))
            return true;
        if (vehicle.Has(atc + "_RAT_PIT_FILT"))
            return false;
        return vehicle.VersionMajor >= 4;
    }

    // float arithmetic noise (14.700000000000001) would show and compare as a change
    private static double Clean(double value) => Math.Round(value, 6);
}
