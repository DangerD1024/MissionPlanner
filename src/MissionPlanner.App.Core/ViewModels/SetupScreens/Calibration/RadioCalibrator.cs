using System.Globalization;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Calibration;

/// <summary>One channel's calibration result.</summary>
public sealed record RadioChannelCal(int Channel, float Min, float Max, float Trim, bool Valid);

/// <summary>Which RC input channel (1-based) drives each primary control, from RCMAP_*.</summary>
public sealed record RadioMap(int Roll, int Pitch, int Throttle, int Yaw)
{
    public static readonly RadioMap Default = new(1, 2, 3, 4);

    /// <summary>RCMAP_ROLL/PITCH/THROTTLE/YAW, or 1/2/3/4 when the vehicle has no RCMAP (or a value is out of range).</summary>
    public static RadioMap From(Func<string, double?> param)
    {
        if (param("RCMAP_ROLL") is null)
            return Default;

        static int Ch(double? v, int fallback) =>
            v is { } d && d >= 1 && d <= RadioCalibrator.Channels ? (int)d : fallback;

        return new RadioMap(Ch(param("RCMAP_ROLL"), 1), Ch(param("RCMAP_PITCH"), 2),
            Ch(param("RCMAP_THROTTLE"), 3), Ch(param("RCMAP_YAW"), 4));
    }
}

/// <summary>
/// Radio calibration as Mission Planner's ConfigRadioInput does it: while capturing, every
/// sample with a plausible channel 1 (800-2200 µs) widens each channel's min/max; the trims are
/// then the centred stick positions clamped to that range; only channels with a sane result
/// (min &lt; max, nothing zero, min ≤ trim ≤ max) are written to RCn_MIN/MAX/TRIM.
/// </summary>
public sealed class RadioCalibrator
{
    public const int Channels = 16;

    private readonly float[] _min = new float[Channels];
    private readonly float[] _max = new float[Channels];
    private readonly float[] _trim = new float[Channels];

    public RadioCalibrator() => Reset();

    public IReadOnlyList<float> Min => _min;

    public IReadOnlyList<float> Max => _max;

    public IReadOnlyList<float> Trim => _trim;

    /// <summary>Samples taken since <see cref="Reset"/>.</summary>
    public int Samples { get; private set; }

    public void Reset()
    {
        for (var i = 0; i < Channels; i++)
        {
            _min[i] = 3000;
            _max[i] = 0;
            _trim[i] = 1500;
        }

        Samples = 0;
    }

    /// <summary>A channel 1 value from a receiver that is connected and sending (not 0, not a failsafe value).</summary>
    public static bool IsPlausible(float pwm) => pwm > 800 && pwm < 2200;

    /// <summary>Takes one sample of all channels (index 0 = channel 1). Ignored unless channel 1 is plausible.</summary>
    public bool Capture(IReadOnlyList<float> channels)
    {
        if (channels.Count == 0 || !IsPlausible(channels[0]))
            return false;

        for (var i = 0; i < Channels && i < channels.Count; i++)
        {
            _min[i] = Math.Min(_min[i], channels[i]);
            _max[i] = Math.Max(_max[i], channels[i]);
        }

        Samples++;
        return true;
    }

    /// <summary>Mission Planner cancels when channel 1 never produced a plausible minimum.</summary>
    public bool Channel1Valid => IsPlausible(_min[0]);

    /// <summary>Takes the trims from the centred sticks, each clamped to its channel's range.</summary>
    public void SetTrims(IReadOnlyList<float> centred)
    {
        for (var i = 0; i < Channels; i++)
        {
            var v = i < centred.Count ? centred[i] : 0;
            _trim[i] = Math.Min(Math.Max(v, _min[i]), _max[i]);
        }
    }

    public static bool IsValid(float min, float max, float trim) =>
        min < max && min != 0 && max != 0 && trim <= max && trim >= min && trim != 0;

    public IReadOnlyList<RadioChannelCal> Results() =>
        Enumerable.Range(0, Channels)
            .Select(i => new RadioChannelCal(i + 1, _min[i], _max[i], _trim[i], IsValid(_min[i], _max[i], _trim[i])))
            .ToList();

    /// <summary>The value written for a captured PWM: the RCn_* parameters are whole microseconds.</summary>
    public static double Pwm(float value) => Math.Round(value);

    /// <summary>RCn_MIN, RCn_MAX and RCn_TRIM for every valid channel, in channel order.</summary>
    public IReadOnlyList<KeyValuePair<string, double>> ParamsToWrite()
    {
        var list = new List<KeyValuePair<string, double>>();
        foreach (var r in Results().Where(r => r.Valid))
        {
            var n = r.Channel.ToString(CultureInfo.InvariantCulture);
            list.Add(new("RC" + n + "_MIN", Pwm(r.Min)));
            list.Add(new("RC" + n + "_MAX", Pwm(r.Max)));
            list.Add(new("RC" + n + "_TRIM", Pwm(r.Trim)));
        }

        return list;
    }

    /// <summary>Mission Planner's summary lines: "CH1 1100 | 1900" for each channel that will be written.</summary>
    public string Summary() =>
        string.Join("\n", Results().Where(r => r.Valid).Select(r =>
            string.Create(CultureInfo.InvariantCulture, $"CH{r.Channel} {Pwm(r.Min)} | {Pwm(r.Max)}")));
}
