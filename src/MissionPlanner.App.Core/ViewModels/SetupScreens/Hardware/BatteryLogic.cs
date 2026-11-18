namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;

/// <summary>An analog power sensor: its amps-per-volt and voltage multiplier (null for "Other").</summary>
public sealed record BatterySensorPreset(int Id, string Name, double? AmpPerVolt, double? VoltMult)
{
    public override string ToString() => Name;
}

/// <summary>A board's analog voltage and current pins.</summary>
public sealed record BatteryBoardPreset(int Id, string Name, int VoltPin, int CurrPin)
{
    public override string ToString() => Name;
}

/// <summary>Battery monitor presets and calibration maths from Mission Planner's ConfigBatteryMonitoring.</summary>
public static class BatteryLogic
{
    public const int MonitorDisabled = 0;
    public const int MonitorVoltageOnly = 3;
    public const int MonitorVoltageAndCurrent = 4;

    public static readonly string[] VoltMultNames = ["BATT_VOLT_MULT", "VOLT_DIVIDER"];
    public static readonly string[] AmpPerVoltNames = ["BATT_AMP_PERVLT", "BATT_AMP_PERVOLT", "AMP_PER_VOLT"];

    public static IReadOnlyList<BatterySensorPreset> Sensors { get; } =
    [
        new(0, "Other", null, null),
        new(1, "AttoPilot 45A", 13.6612, 4.127115),
        new(2, "AttoPilot 90A", 27.3224, 15.70105),
        new(3, "AttoPilot 180A", 54.64481, 15.70105),
        new(4, "3DR Power Module", 18.0018, 10.10101),
        new(5, "3DR 4 in 1 ESC", 17, 12.02),
        new(6, "3DR HV Power Module APM", 24, 12.02),
        new(7, "Cube HV Power Module", 39.877, 12.02),
        new(8, "CUAV HV PM (Pixhack)", 24, 18),
        new(9, "Holybro Power Module (Pixhawk4)", 36.364, 18.182),
    ];

    public static IReadOnlyList<BatteryBoardPreset> Boards { get; } =
    [
        new(0, "CUAV V5/Pixhawk4 or APM1", 0, 1),
        new(1, "APM2 - 2.5 non 3DR", 1, 2),
        new(2, "APM2.5+/ZealotF427 - 3DR Power Module", 13, 12),
        new(3, "PX4", 100, 101),
        new(4, "The Cube or Pixhawk", 2, 3),
        new(5, "VR Brain 4.5 - 5", 10, 11),
        new(6, "VR Micro Brain 5", 10, -1),
        new(7, "VR Brain 4", 6, 7),
        new(8, "Cube Orange", 14, 15),
        new(9, "Durandal/ZealotH743", 16, 17),
        new(10, "Pixhawk 6C/Pix32 v6", 8, 4),
    ];

    /// <summary>
    /// New voltage multiplier from a meter reading: <c>measured × old / reported</c>.
    /// Null when the autopilot reports no voltage (nothing to scale from) or an input is not positive.
    /// </summary>
    public static double? CalibrateVoltMult(double measuredVolts, double reportedVolts, double currentMult) =>
        Scale(measuredVolts, reportedVolts, currentMult);

    /// <summary>New amps-per-volt from a clamp-meter reading: <c>measured × old / reported</c>.</summary>
    public static double? CalibrateAmpPerVolt(double measuredAmps, double reportedAmps, double currentAmpPerVolt) =>
        Scale(measuredAmps, reportedAmps, currentAmpPerVolt);

    private static double? Scale(double measured, double reported, double factor)
    {
        if (!double.IsFinite(measured) || !double.IsFinite(reported) || !double.IsFinite(factor))
            return null;
        if (measured <= 0 || reported <= 0 || factor <= 0)
            return null;
        return measured * factor / reported;
    }

    /// <summary>The preset whose values match the parameters (to float precision), else "Other".</summary>
    public static BatterySensorPreset DetectSensor(double? ampPerVolt, double? voltMult)
    {
        if (ampPerVolt is { } a && voltMult is { } v)
        {
            foreach (var s in Sensors)
            {
                if (s.AmpPerVolt is { } sa && s.VoltMult is { } sv && Close(a, sa) && Close(v, sv))
                    return s;
            }
        }

        return Sensors[0];
    }

    /// <summary>The board whose pins match (VR Brain 5 and VR Micro Brain 5 share the voltage pin).</summary>
    public static BatteryBoardPreset? DetectBoard(double? voltPin, double? currPin)
    {
        if (voltPin is not { } vp)
            return null;
        var candidates = Boards.Where(b => b.VoltPin == (int)Math.Round(vp)).ToList();
        if (candidates.Count <= 1)
            return candidates.FirstOrDefault();
        var cp = currPin is { } c ? (int)Math.Round(c) : int.MinValue;
        return candidates.FirstOrDefault(b => b.CurrPin == cp) ?? candidates.Last();
    }

    /// <summary>Analog monitors (3 voltage only, 4 voltage and current) use pins, multipliers and calibration.</summary>
    public static bool IsAnalog(double monitor) => monitor is MonitorVoltageOnly or MonitorVoltageAndCurrent;

    public static bool MeasuresCurrent(double monitor) => monitor != MonitorDisabled && monitor != MonitorVoltageOnly;

    private static bool Close(double a, double b) => Math.Abs(a - b) <= 1e-4 * Math.Max(1, Math.Abs(b));
}
