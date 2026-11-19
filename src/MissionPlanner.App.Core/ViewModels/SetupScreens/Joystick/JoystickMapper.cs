namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Joystick;

/// <summary>An RC channel's PWM range on the vehicle (RCn_MIN/MAX/TRIM).</summary>
public readonly record struct RcRange(int Min, int Max, int Trim)
{
    public static readonly RcRange Default = new(1000, 2000, 1500);
}

/// <summary>
/// Joystick values to PWM, ported from Mission Planner's JoystickBase.pickchannel and Expo: an
/// axis maps to -500..500, is reversed if asked, shaped by expo around the channel's trim, offset
/// by the user trim and limited to the channel's range.
/// </summary>
public static class JoystickMapper
{
    /// <summary>UINT16_MAX in RC_CHANNELS_OVERRIDE channels 1-8: leave the channel alone.</summary>
    public const ushort Ignore = ushort.MaxValue;

    /// <summary>UINT16_MAX - 1 in channels 9-18: give the channel back to the RC receiver.</summary>
    public const ushort ReleaseHigh = ushort.MaxValue - 1;

    /// <summary>Axis value -32768..32767 to Mission Planner's -500..500.</summary>
    public static double Normalise(short raw) => (raw + 32768) / 65535.0 * 1000.0 - 500.0;

    /// <summary>Mission Planner's expo: input -500..500 to PWM, pulled towards the middle by expo/100.</summary>
    public static double Expo(double input, double expo, double min, double max, double mid)
    {
        var mult = expo / 100.0;
        if (input >= 0)
        {
            var linear = Map(input, 0, 500, mid, max);
            var factor = input > 250 ? 250 - (input - 250) : input;
            return linear - factor * mult;
        }
        else
        {
            var linear = Map(input, -500, 0, min, mid);
            var factor = input < -250 ? -250 - (input + 250) : input;
            return linear - factor * mult;
        }
    }

    /// <summary>PWM for a -500..500 input.</summary>
    public static ushort Pwm(double input, bool reverse, int expo, RcRange range, int trimOffset = 0)
    {
        var x = Math.Clamp(reverse ? -input : input, -500, 500);
        var v = Expo(x, expo, range.Min, range.Max, range.Trim) + trimOffset;
        return (ushort)Math.Clamp((int)Math.Round(v), range.Min, range.Max);
    }

    /// <summary>
    /// The channel's range: RCn_MIN/MAX/TRIM when the vehicle has them, else 1000-2000. The
    /// throttle channel centres on mid-range, as in Mission Planner, so a sprung stick hovers.
    /// </summary>
    public static RcRange RangeFor(int channel, Func<string, double?> param)
    {
        var min = param($"RC{channel}_MIN");
        var max = param($"RC{channel}_MAX");
        var trim = param($"RC{channel}_TRIM");
        var r = min is > 0 && max is > 0 && max > min
            ? new RcRange((int)min.Value, (int)max.Value, (int)Math.Clamp(trim ?? (min.Value + max.Value) / 2, min.Value, max.Value))
            : RcRange.Default;
        var throttle = (int)(param("RCMAP_THROTTLE") ?? 3);
        if (channel == throttle)
            r = r with { Trim = (r.Min + r.Max) / 2 };
        return r;
    }

    static double Map(double x, double inMin, double inMax, double outMin, double outMax) =>
        (x - inMin) * (outMax - outMin) / (inMax - inMin) + outMin;

    /// <summary>
    /// Elevon mixing as Mission Planner does it: channel 1 and 2 carry (pitch - roll)/2 and
    /// (pitch + roll)/2 around 1500, each optionally reversed.
    /// </summary>
    public static (ushort Ch1, ushort Ch2) Elevons(ushort roll, ushort pitch, bool reverse1, bool reverse2)
    {
        var s1 = reverse1 ? -1 : 1;
        var s2 = reverse2 ? -1 : 1;
        var ch1 = s1 * ((pitch - 1500) - (roll - 1500)) / 2 + 1500;
        var ch2 = s2 * ((pitch - 1500) + (roll - 1500)) / 2 + 1500;
        return ((ushort)Math.Clamp(ch1, 800, 2200), (ushort)Math.Clamp(ch2, 800, 2200));
    }

    /// <summary>
    /// RC_CHANNELS_OVERRIDE for the computed outputs (0 = channel not driven). Undriven channels
    /// 1-8 are UINT16_MAX and 9-18 zero, which ArduPilot ignores, as Mission Planner sends them.
    /// </summary>
    public static MAVLink.mavlink_rc_channels_override_t Override(IReadOnlyList<ushort> pwm, uint sysid, byte compid, bool release = false)
    {
        ushort V(int ch)
        {
            var value = ch - 1 < pwm.Count ? pwm[ch - 1] : (ushort)0;
            if (release)
                return value == 0 ? (ch <= 8 ? Ignore : (ushort)0) : (ch <= 8 ? (ushort)0 : ReleaseHigh);
            return value != 0 ? value : ch <= 8 ? Ignore : (ushort)0;
        }

        return new MAVLink.mavlink_rc_channels_override_t
        {
            target_system = (byte)sysid,
            target_component = compid,
            chan1_raw = V(1), chan2_raw = V(2), chan3_raw = V(3), chan4_raw = V(4),
            chan5_raw = V(5), chan6_raw = V(6), chan7_raw = V(7), chan8_raw = V(8),
            chan9_raw = V(9), chan10_raw = V(10), chan11_raw = V(11), chan12_raw = V(12),
            chan13_raw = V(13), chan14_raw = V(14), chan15_raw = V(15), chan16_raw = V(16),
            chan17_raw = V(17), chan18_raw = V(18),
        };
    }

    /// <summary>
    /// MANUAL_CONTROL from the same outputs, per the MAVLink definition ArduPilot follows:
    /// x = pitch (forward positive, so the pitch PWM is inverted), y = roll, z = throttle 0..1000,
    /// r = yaw; undriven axes are INT16_MAX. The channel roles come from RCMAP_*.
    /// </summary>
    public static MAVLink.mavlink_manual_control_t ManualControl(IReadOnlyList<ushort> pwm, Func<int, RcRange> range,
        int rollCh, int pitchCh, int throttleCh, int yawCh, byte target, ushort buttons)
    {
        ushort P(int ch) => ch >= 1 && ch - 1 < pwm.Count ? pwm[ch - 1] : (ushort)0;

        short Centred(int ch, bool invert)
        {
            var v = P(ch);
            if (v == 0)
                return short.MaxValue;
            var r = range(ch);
            var half = v >= r.Trim ? Math.Max(1, r.Max - r.Trim) : Math.Max(1, r.Trim - r.Min);
            var n = Math.Clamp((v - r.Trim) * 1000.0 / half, -1000, 1000);
            return (short)Math.Round(invert ? -n : n);
        }

        short Throttle(int ch)
        {
            var v = P(ch);
            if (v == 0)
                return short.MaxValue;
            var r = range(ch);
            return (short)Math.Round(Math.Clamp((v - r.Min) * 1000.0 / Math.Max(1, r.Max - r.Min), 0, 1000));
        }

        return new MAVLink.mavlink_manual_control_t
        {
            target = target,
            x = Centred(pitchCh, invert: true),
            y = Centred(rollCh, invert: false),
            z = Throttle(throttleCh),
            r = Centred(yawCh, invert: false),
            buttons = buttons,
        };
    }
}

/// <summary>A hat-driven channel: holding the hat moves the value; it stays where it was left (-500..500).</summary>
public sealed class HatAccumulator
{
    /// <summary>Change per 25 Hz step: a full sweep takes 5 s, about Mission Planner's pace.</summary>
    public const double Step = 8;

    public double Value { get; private set; }

    public void Update(int direction)
    {
        Value = Math.Clamp(Value + direction * Step, -500, 500);
    }

    public void Reset() => Value = 0;
}
