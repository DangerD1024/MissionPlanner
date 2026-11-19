using System.Globalization;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Calibration;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.RadioMapping;

public enum RcFunction
{
    Roll,
    Pitch,
    Throttle,
    Yaw,
}

public enum RcMapStep
{
    CenterSticks,
    ThrottleUp,
    ThrottleDown,
    YawRight,
    YawLeft,
    RollRight,
    RollLeft,
    PitchForward,
    PitchBack,
    Review,
}

/// <summary>
/// One step of the sequence. A detect step finds the channel that moves most; the next step
/// confirms it by moving the same channel to the other end. <see cref="ExpectedSign"/> is
/// ArduPilot's convention for the detect step: +1 when that stick position should give a high PWM
/// (throttle up, yaw right, roll right), -1 when low (pitch stick forward = nose down).
/// </summary>
public sealed record RcStepInfo(RcMapStep Step, RcFunction? Function, bool IsDetect, int ExpectedSign, string Title, string Instruction);

/// <summary>A primary control found on a channel (1-based) and whether that channel is reversed.</summary>
public sealed record RcChannelMapping(RcFunction Function, int Channel, bool Reversed, float Center, float First, float? Second);

/// <summary>
/// QGroundControl's RadioComponentController sequence for ArduPilot: sticks centred (throttle
/// down), then throttle up/down, yaw right/left, roll right/left, pitch forward/back. In each
/// detect step the unassigned channel with the largest movement from its centred value beyond
/// <see cref="Threshold"/> wins once it is held still for <see cref="SettleTime"/>; the following
/// step confirms it by moving that channel past the other side of the 1500 µs midpoint.
/// </summary>
public sealed class RadioChannelMapper
{
    /// <summary>Movement that counts as the stick being moved (QGC: 300 µs).</summary>
    public const float DefaultThreshold = 300;

    /// <summary>Jitter allowed while the stick is held (QGC: 20 µs).</summary>
    public const float SettleTolerance = 20;

    /// <summary>The nominal stick centre the confirming move must cross.</summary>
    public const float Midpoint = 1500;

    public static IReadOnlyList<RcStepInfo> Steps { get; } =
    [
        new(RcMapStep.CenterSticks, null, false, 0, "Centre the sticks",
            "Centre the roll, pitch and yaw sticks, move the throttle all the way down and reset the transmitter trims to centre. Then click Next."),
        new(RcMapStep.ThrottleUp, RcFunction.Throttle, true, +1, "Throttle up", "Move the throttle stick all the way up and hold it there."),
        new(RcMapStep.ThrottleDown, RcFunction.Throttle, false, -1, "Throttle down", "Move the throttle stick all the way down and hold it there."),
        new(RcMapStep.YawRight, RcFunction.Yaw, true, +1, "Yaw right", "Move the yaw (rudder) stick all the way to the right and hold it there."),
        new(RcMapStep.YawLeft, RcFunction.Yaw, false, -1, "Yaw left", "Move the yaw stick all the way to the left and hold it there."),
        new(RcMapStep.RollRight, RcFunction.Roll, true, +1, "Roll right", "Move the roll (aileron) stick all the way to the right and hold it there."),
        new(RcMapStep.RollLeft, RcFunction.Roll, false, -1, "Roll left", "Move the roll stick all the way to the left and hold it there."),
        new(RcMapStep.PitchForward, RcFunction.Pitch, true, -1, "Pitch forward",
            "Push the pitch (elevator) stick all the way forward (up, away from you) and hold it there."),
        new(RcMapStep.PitchBack, RcFunction.Pitch, false, +1, "Pitch back", "Pull the pitch stick all the way back (down, towards you) and hold it there."),
        new(RcMapStep.Review, null, false, 0, "Done", "All four controls were found. Check the result and write it."),
    ];

    private readonly int _channels;
    private readonly float[] _center;
    private readonly Dictionary<RcFunction, RcChannelMapping> _mappings = new();
    private int _stepIndex;
    private TimeSpan _settleStart;
    private float _settleValue;

    public RadioChannelMapper(int channels = RadioCalibrator.Channels, TimeSpan? settleTime = null, float threshold = DefaultThreshold)
    {
        _channels = channels;
        _center = new float[channels];
        SettleTime = settleTime ?? TimeSpan.FromSeconds(0.6);
        Threshold = threshold;
    }

    public TimeSpan SettleTime { get; }

    public float Threshold { get; }

    public int StepIndex => _stepIndex;

    public RcStepInfo Info => Steps[_stepIndex];

    public RcMapStep Step => Info.Step;

    public bool IsComplete => Step == RcMapStep.Review;

    /// <summary>The centred values captured in the first step (index 0 = channel 1).</summary>
    public IReadOnlyList<float> Center => _center;

    /// <summary>The channel (1-based) currently moving for this step, if any.</summary>
    public int? Candidate { get; private set; }

    public float CandidateValue { get; private set; }

    /// <summary>How long the candidate has been held still, 0..1.</summary>
    public double SettleProgress { get; private set; }

    public IReadOnlyDictionary<RcFunction, RcChannelMapping> Mappings => _mappings;

    /// <summary>The first step: takes the centred stick positions. False if the receiver is not sending.</summary>
    public bool CaptureCenter(IReadOnlyList<float> values)
    {
        if (Step != RcMapStep.CenterSticks || values.Count == 0 || !RadioCalibrator.IsPlausible(values[0]))
            return false;
        for (var i = 0; i < _channels; i++)
            _center[i] = i < values.Count ? values[i] : 0;
        _mappings.Clear();
        Advance();
        return true;
    }

    /// <summary>One sample of all channels at time <paramref name="now"/>. True when the step completed.</summary>
    public bool Sample(IReadOnlyList<float> values, TimeSpan now)
    {
        var info = Info;
        if (info.Function is not { } function)
            return false;

        int channel;
        float value;
        if (info.IsDetect)
        {
            var best = -1;
            var bestMove = 0f;
            for (var i = 0; i < _channels && i < values.Count; i++)
            {
                if (IsAssigned(i + 1) || !RadioCalibrator.IsPlausible(values[i]) || !RadioCalibrator.IsPlausible(_center[i]))
                    continue;
                var move = Math.Abs(values[i] - _center[i]);
                if (move > bestMove)
                {
                    best = i;
                    bestMove = move;
                }
            }

            if (best < 0 || bestMove < Threshold)
            {
                ClearCandidate();
                return false;
            }

            channel = best + 1;
            value = values[best];
        }
        else
        {
            var m = _mappings[function];
            channel = m.Channel;
            value = channel <= values.Count ? values[channel - 1] : 0;
            // past the midpoint on the other side from the first position
            var side = Math.Sign(m.First - Midpoint);
            if (side == 0 || (value - Midpoint) * side > -Threshold)
            {
                ClearCandidate();
                return false;
            }
        }

        if (Candidate != channel || Math.Abs(value - _settleValue) > SettleTolerance)
        {
            Candidate = channel;
            CandidateValue = value;
            _settleStart = now;
            _settleValue = value;
            SettleProgress = 0;
            return false;
        }

        CandidateValue = value;
        var held = now - _settleStart;
        SettleProgress = Math.Clamp(held.TotalMilliseconds / Math.Max(1, SettleTime.TotalMilliseconds), 0, 1);
        if (held < SettleTime)
            return false;

        if (info.IsDetect)
        {
            var center = _center[channel - 1];
            var reversed = Math.Sign(value - center) != info.ExpectedSign;
            _mappings[function] = new RcChannelMapping(function, channel, reversed, center, value, null);
        }
        else
        {
            _mappings[function] = _mappings[function] with { Second = value };
        }

        Advance();
        return true;
    }

    /// <summary>The channel already holds another control.</summary>
    public bool IsAssigned(int channel) => _mappings.Values.Any(m => m.Channel == channel);

    /// <summary>Goes back one step (redoing a detect step forgets that control). False at the start.</summary>
    public bool Back()
    {
        if (_stepIndex == 0)
            return false;
        _stepIndex--;
        ClearCandidate();
        var info = Info;
        if (info.Step == RcMapStep.CenterSticks)
            _mappings.Clear();
        else if (info.IsDetect && info.Function is { } f)
            _mappings.Remove(f);
        else if (info.Function is { } g && _mappings.TryGetValue(g, out var m))
            _mappings[g] = m with { Second = null };
        return true;
    }

    public void Restart()
    {
        _stepIndex = 0;
        _mappings.Clear();
        Array.Clear(_center);
        ClearCandidate();
    }

    /// <summary>The RCMAP channels and reversal found (complete once <see cref="IsComplete"/>).</summary>
    public RadioMapResult Result()
    {
        if (!IsComplete)
            throw new InvalidOperationException("The mapping sequence is not finished.");
        return new RadioMapResult(_mappings[RcFunction.Roll], _mappings[RcFunction.Pitch], _mappings[RcFunction.Throttle], _mappings[RcFunction.Yaw]);
    }

    private void Advance()
    {
        if (_stepIndex < Steps.Count - 1)
            _stepIndex++;
        ClearCandidate();
    }

    private void ClearCandidate()
    {
        Candidate = null;
        SettleProgress = 0;
        _settleValue = float.NaN;
    }
}

/// <summary>The result of the mapping sequence and the parameters that express it.</summary>
public sealed record RadioMapResult(RcChannelMapping Roll, RcChannelMapping Pitch, RcChannelMapping Throttle, RcChannelMapping Yaw)
{
    public IEnumerable<RcChannelMapping> All => [Roll, Pitch, Throttle, Yaw];

    /// <summary>
    /// The writes that differ from the vehicle: RCMAP_ROLL/PITCH/THROTTLE/YAW (take effect after a
    /// reboot) and RCn_REVERSED of the four channels (RCn_REV -1/1 on old firmware).
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, double>> Writes(Func<string, double?> param)
    {
        var list = new List<KeyValuePair<string, double>>();

        void Map(string name, int channel)
        {
            if (param(name) is { } now && (int)Math.Round(now) != channel)
                list.Add(new(name, channel));
        }

        Map("RCMAP_ROLL", Roll.Channel);
        Map("RCMAP_PITCH", Pitch.Channel);
        Map("RCMAP_THROTTLE", Throttle.Channel);
        Map("RCMAP_YAW", Yaw.Channel);

        foreach (var m in All.OrderBy(m => m.Channel))
        {
            var n = m.Channel.ToString(CultureInfo.InvariantCulture);
            if (param("RC" + n + "_REVERSED") is { } rev)
            {
                var want = m.Reversed ? 1 : 0;
                if ((int)Math.Round(rev) != want)
                    list.Add(new("RC" + n + "_REVERSED", want));
            }
            else if (param("RC" + n + "_REV") is { } old)
            {
                var want = m.Reversed ? -1 : 1;
                if ((int)Math.Round(old) != want)
                    list.Add(new("RC" + n + "_REV", want));
            }
        }

        return list;
    }
}
