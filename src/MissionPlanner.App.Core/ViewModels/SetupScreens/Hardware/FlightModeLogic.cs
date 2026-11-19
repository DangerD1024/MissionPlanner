using MissionPlanner.ArduPilot;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;

/// <summary>Flight mode switch rules (ArduPilot's RC_Channel::read_6pos_switch and Mission Planner's ConfigFlightModes).</summary>
public static class FlightModeLogic
{
    public const int SlotCount = 6;

    /// <summary>Upper PWM bound of slots 1-5 (slot 6 is everything above the last).</summary>
    private static readonly int[] UpperBounds = [1230, 1360, 1490, 1620, 1749];

    public static IReadOnlyList<string> BandText { get; } =
    [
        "PWM 0 - 1230",
        "PWM 1231 - 1360",
        "PWM 1361 - 1490",
        "PWM 1491 - 1620",
        "PWM 1621 - 1749",
        "PWM 1750 +",
    ];

    /// <summary>
    /// The 0-based slot a mode-switch PWM selects: ≤1230 → 0, 1231–1360 → 1, 1361–1490 → 2,
    /// 1491–1620 → 3, 1621–1749 → 4, ≥1750 → 5. Like the firmware, readings at or outside
    /// 800/2200 (no signal) select nothing (-1).
    /// </summary>
    public static int PwmToSlot(double pwm)
    {
        if (double.IsNaN(pwm) || pwm <= Channels.PwmMin || pwm >= Channels.PwmMax)
            return -1;
        var p = (int)pwm;
        for (var i = 0; i < UpperBounds.Length; i++)
        {
            if (p <= UpperBounds[i])
                return i;
        }

        return SlotCount - 1;
    }

    /// <summary>Copter modes that can use Simple / Super Simple (Mission Planner's list).</summary>
    public static bool SupportsSimple(string? modeName)
    {
        if (string.IsNullOrEmpty(modeName))
            return false;
        var m = modeName.ToLowerInvariant().Replace(" ", "").Replace("_", "");
        string[] simple = ["althold", "auto", "autotune", "land", "loiter", "ofloiter", "poshold", "rtl", "sport", "stabilize", "flowhold", "zigzag"];
        return simple.Any(m.Contains);
    }

    /// <summary>FLTMODEn (Copter, Plane), MODEn (Rover) or COM_FLTMODEn (PX4): whichever the vehicle has.</summary>
    public static string? ModeParamPrefix(Func<string, bool> has)
    {
        if (has("FLTMODE1"))
            return "FLTMODE";
        if (has("MODE1"))
            return "MODE";
        if (has("COM_FLTMODE1"))
            return "COM_FLTMODE";
        return null;
    }

    /// <summary>The parameter holding the mode switch channel: FLTMODE_CH, else MODE_CH.</summary>
    public static string? SwitchChannelParam(Func<string, bool> has) =>
        has("FLTMODE_CH") ? "FLTMODE_CH" : has("MODE_CH") ? "MODE_CH" : null;

    /// <summary>
    /// The choices for the mode switch channel: the parameter's documented values when it has them
    /// (Copter: 0 None, 5..16), else channels 1..16 (Plane and Rover document only a range).
    /// </summary>
    public static IReadOnlyList<ParamOption> ChannelOptions(IReadOnlyList<KeyValuePair<double, string>>? documented)
    {
        if (documented is { Count: > 0 })
            return documented.Select(v => new ParamOption(v.Key, v.Value)).ToList();
        return Enumerable.Range(1, 16).Select(c => new ParamOption(c, "Channel " + c)).ToList();
    }

    /// <summary>SIMPLE / SUPER_SIMPLE: bit i is slot i+1.</summary>
    public static double SlotMask(IReadOnlyList<bool> slots) =>
        BitmaskCodec.FromBits(Enumerable.Range(0, slots.Count).Where(i => slots[i]));

    public static bool SlotBit(double mask, int slot) => BitmaskCodec.IsSet(mask, slot);

    /// <summary>
    /// The mode list: documented values of the first slot's parameter (Plane adds INITIALISING, as
    /// Common.getModesList does), else the library's built-in lists (PX4, Tracker).
    /// </summary>
    public static IReadOnlyList<ParamOption> Modes(Firmwares firmware, IReadOnlyList<KeyValuePair<double, string>>? documented)
    {
        var list = new List<ParamOption>();
        if (documented is { Count: > 0 })
        {
            list.AddRange(documented.Select(v => new ParamOption(v.Key, v.Value)));
            if (firmware == Firmwares.ArduPlane && list.All(o => o.Value != 16))
                list.Add(new ParamOption(16, "INITIALISING"));
            return list;
        }

        if (firmware is Firmwares.PX4 or Firmwares.ArduTracker)
        {
            try
            {
                var builtIn = Common.getModesList(firmware);
                if (builtIn != null)
                    list.AddRange(builtIn.Select(v => new ParamOption(v.Key, v.Value)));
            }
            catch (Exception)
            {
                // fall through to an empty list; the page shows raw numbers
            }
        }

        return list;
    }
}
