using MissionPlanner.App.Core.ViewModels.SetupScreens.RadioMapping;

namespace MissionPlanner.App.Tests.SetupScreens.RadioMapping;

/// <summary>A scripted transmitter: sticks are set per channel and sampled every 50 ms.</summary>
internal sealed class ScriptedRadio(RadioChannelMapper mapper)
{
    private readonly float[] _ch = Enumerable.Repeat(1500f, 16).ToArray();
    private TimeSpan _now;

    public float this[int channel]
    {
        get => _ch[channel - 1];
        set => _ch[channel - 1] = value;
    }

    public float[] Values => _ch.ToArray();

    /// <summary>Holds the sticks for a while; returns how many steps completed.</summary>
    public int Hold(TimeSpan duration)
    {
        var advanced = 0;
        for (var t = TimeSpan.Zero; t <= duration; t += TimeSpan.FromMilliseconds(50))
        {
            _now += TimeSpan.FromMilliseconds(50);
            if (mapper.Sample(_ch, _now))
                advanced++;
        }

        return advanced;
    }

    public int Hold(double seconds = 1) => Hold(TimeSpan.FromSeconds(seconds));

    /// <summary>Moves channels and holds them.</summary>
    public int Set(double seconds, params (int Channel, float Pwm)[] sticks)
    {
        foreach (var (c, v) in sticks)
            this[c] = v;
        return Hold(seconds);
    }
}

[TestClass]
public sealed class RadioChannelMapperTests
{
    private static Func<string, double?> Vehicle(Dictionary<string, double> p) => n => p.TryGetValue(n, out var v) ? v : null;

    private static Dictionary<string, double> DefaultParams() => new()
    {
        ["RCMAP_ROLL"] = 1, ["RCMAP_PITCH"] = 2, ["RCMAP_THROTTLE"] = 3, ["RCMAP_YAW"] = 4,
        ["RC1_REVERSED"] = 0, ["RC2_REVERSED"] = 0, ["RC3_REVERSED"] = 0, ["RC4_REVERSED"] = 0,
        ["RC5_REVERSED"] = 0, ["RC6_REVERSED"] = 0,
    };

    /// <summary>Runs the whole QGC sequence with each control on the given channel and sign (+1 = ArduPilot's normal).</summary>
    private static RadioChannelMapper Run(int roll, int rollSign, int pitch, int pitchSign, int throttle, int throttleSign, int yaw, int yawSign)
    {
        var m = new RadioChannelMapper();
        var radio = new ScriptedRadio(m);
        float Hi(int sign) => sign > 0 ? 1950 : 1050;
        float Lo(int sign) => sign > 0 ? 1050 : 1950;

        radio[throttle] = Lo(throttleSign);
        Assert.IsTrue(m.CaptureCenter(radio.Values));
        Assert.AreEqual(RcMapStep.ThrottleUp, m.Step);

        Assert.AreEqual(1, radio.Set(1, (throttle, Hi(throttleSign))));
        Assert.AreEqual(1, radio.Set(1, (throttle, Lo(throttleSign))));
        Assert.AreEqual(RcMapStep.YawRight, m.Step);
        Assert.AreEqual(1, radio.Set(1, (yaw, Hi(yawSign))));
        Assert.AreEqual(1, radio.Set(1, (yaw, Lo(yawSign))));
        radio[yaw] = 1500;
        Assert.AreEqual(1, radio.Set(1, (roll, Hi(rollSign))));
        Assert.AreEqual(1, radio.Set(1, (roll, Lo(rollSign))));
        radio[roll] = 1500;
        // ArduPilot: pitch forward is LOW pwm on a normal channel
        Assert.AreEqual(1, radio.Set(1, (pitch, Lo(pitchSign))));
        Assert.AreEqual(1, radio.Set(1, (pitch, Hi(pitchSign))));
        Assert.IsTrue(m.IsComplete);
        return m;
    }

    [TestMethod]
    public void StandardRadioMapsToOneTwoThreeFourWithNothingToWrite()
    {
        var m = Run(1, +1, 2, +1, 3, +1, 4, +1);
        var r = m.Result();
        Assert.AreEqual((1, 2, 3, 4), (r.Roll.Channel, r.Pitch.Channel, r.Throttle.Channel, r.Yaw.Channel));
        Assert.IsFalse(r.All.Any(x => x.Reversed));
        Assert.IsEmpty(r.Writes(Vehicle(DefaultParams())));
    }

    [TestMethod]
    public void SwappedAndReversedChannelsAreFound()
    {
        // roll on 2 (moving the wrong way), pitch on 1, throttle on 3, yaw on 4 reversed
        var m = Run(2, -1, 1, +1, 3, +1, 4, -1);
        var r = m.Result();
        Assert.AreEqual(2, r.Roll.Channel);
        Assert.IsTrue(r.Roll.Reversed);
        Assert.AreEqual(1, r.Pitch.Channel);
        Assert.IsFalse(r.Pitch.Reversed);
        Assert.IsTrue(r.Yaw.Reversed);
        Assert.IsFalse(r.Throttle.Reversed);

        var writes = r.Writes(Vehicle(DefaultParams())).ToDictionary(w => w.Key, w => w.Value);
        CollectionAssert.AreEquivalent(new[] { "RCMAP_ROLL", "RCMAP_PITCH", "RC2_REVERSED", "RC4_REVERSED" }, writes.Keys.ToArray());
        Assert.AreEqual(2, writes["RCMAP_ROLL"]);
        Assert.AreEqual(1, writes["RCMAP_PITCH"]);
        Assert.AreEqual(1, writes["RC2_REVERSED"]);
        Assert.AreEqual(1, writes["RC4_REVERSED"]);
    }

    [TestMethod]
    public void PitchForwardHighMeansReversed()
    {
        var r = Run(1, +1, 2, -1, 3, +1, 4, +1).Result();
        Assert.IsTrue(r.Pitch.Reversed);
        Assert.IsFalse(r.Roll.Reversed);
    }

    [TestMethod]
    public void AReversedThrottleIsFoundFromTheDownPosition()
    {
        var r = Run(1, +1, 2, +1, 5, -1, 4, +1).Result();
        Assert.AreEqual(5, r.Throttle.Channel);
        Assert.IsTrue(r.Throttle.Reversed);
        var writes = r.Writes(Vehicle(DefaultParams())).ToDictionary(w => w.Key, w => w.Value);
        Assert.AreEqual(5, writes["RCMAP_THROTTLE"]);
        Assert.AreEqual(1, writes["RC5_REVERSED"]);
        Assert.IsFalse(writes.ContainsKey("RC3_REVERSED"), "channels that lost a control keep their setting");
    }

    [TestMethod]
    public void TheLargestMovementWinsOverCrosstalk()
    {
        var m = new RadioChannelMapper();
        var radio = new ScriptedRadio(m);
        radio[3] = 1050;
        m.CaptureCenter(radio.Values);
        radio.Set(1, (3, 1950));
        radio.Set(1, (3, 1050));
        // yaw right on channel 4 drags channel 1 by 350 µs
        Assert.AreEqual(1, radio.Set(1, (4, 1950), (1, 1850)));
        Assert.AreEqual(4, m.Mappings[RcFunction.Yaw].Channel);
    }

    [TestMethod]
    public void TheStickMustBeHeldStill()
    {
        var m = new RadioChannelMapper();
        var radio = new ScriptedRadio(m);
        radio[3] = 1050;
        m.CaptureCenter(radio.Values);

        // a flick shorter than the settle time
        Assert.AreEqual(0, radio.Set(0.3, (3, 1950)));
        Assert.AreEqual(0, radio.Set(0.5, (3, 1050)));
        Assert.AreEqual(RcMapStep.ThrottleUp, m.Step);

        // moving steadily keeps restarting the hold
        for (var v = 1400f; v <= 1950; v += 50)
            Assert.AreEqual(0, radio.Set(0.1, (3, v)));
        Assert.IsGreaterThan(0.0, m.SettleProgress + 0.0001);
        Assert.AreEqual(3, m.Candidate);
        Assert.AreEqual(1, radio.Hold(1));
    }

    [TestMethod]
    public void TheConfirmingMoveMustCrossTheMiddle()
    {
        var m = new RadioChannelMapper();
        var radio = new ScriptedRadio(m);
        radio[3] = 1050;
        m.CaptureCenter(radio.Values);
        radio.Set(1, (3, 1950));
        Assert.AreEqual(RcMapStep.ThrottleDown, m.Step);
        Assert.AreEqual(0, radio.Set(2, (3, 1450)), "only half way down");
        Assert.AreEqual(0, radio.Set(2, (1, 1050)), "another channel does not count");
        Assert.AreEqual(1, radio.Set(1, (3, 1050)));
    }

    [TestMethod]
    public void AChannelAlreadyMappedIsNotTakenAgain()
    {
        var m = new RadioChannelMapper();
        var radio = new ScriptedRadio(m);
        radio[3] = 1050;
        m.CaptureCenter(radio.Values);
        radio.Set(1, (3, 1950));
        radio.Set(1, (3, 1050));
        Assert.AreEqual(RcMapStep.YawRight, m.Step);
        Assert.AreEqual(0, radio.Set(2, (3, 1950)), "throttle again is not yaw");
        Assert.IsNull(m.Candidate);
    }

    [TestMethod]
    public void BackRedoesASteps()
    {
        var m = new RadioChannelMapper();
        var radio = new ScriptedRadio(m);
        radio[3] = 1050;
        m.CaptureCenter(radio.Values);
        radio.Set(1, (3, 1950));
        radio.Set(1, (3, 1050));
        radio.Set(1, (4, 1950));
        Assert.AreEqual(RcMapStep.YawLeft, m.Step);
        Assert.IsTrue(m.Mappings.ContainsKey(RcFunction.Yaw));

        Assert.IsTrue(m.Back());
        Assert.AreEqual(RcMapStep.YawRight, m.Step);
        Assert.IsFalse(m.Mappings.ContainsKey(RcFunction.Yaw), "redoing the detect step forgets it");
        Assert.IsTrue(m.Mappings.ContainsKey(RcFunction.Throttle));

        radio[4] = 1500;
        radio.Hold(0.2);
        Assert.AreEqual(1, radio.Set(1, (6, 1950)));
        Assert.AreEqual(6, m.Mappings[RcFunction.Yaw].Channel);

        while (m.Back())
        {
        }

        Assert.AreEqual(RcMapStep.CenterSticks, m.Step);
        Assert.IsEmpty(m.Mappings);
    }

    [TestMethod]
    public void NoSignalNoStart()
    {
        var m = new RadioChannelMapper();
        Assert.IsFalse(m.CaptureCenter(new float[16]));
        Assert.AreEqual(RcMapStep.CenterSticks, m.Step);
        Assert.ThrowsExactly<InvalidOperationException>(() => m.Result());
    }

    [TestMethod]
    public void OldFirmwareUsesRcRev()
    {
        var r = Run(1, -1, 2, +1, 3, +1, 4, +1).Result();
        var p = new Dictionary<string, double> { ["RCMAP_ROLL"] = 1, ["RCMAP_PITCH"] = 2, ["RCMAP_THROTTLE"] = 3, ["RCMAP_YAW"] = 4, ["RC1_REV"] = 1, ["RC2_REV"] = 1 };
        var writes = r.Writes(Vehicle(p));
        Assert.AreEqual(new KeyValuePair<string, double>("RC1_REV", -1), writes.Single());
    }

    [TestMethod]
    public void StepsFollowQGroundControl()
    {
        CollectionAssert.AreEqual(new[]
        {
            RcMapStep.CenterSticks, RcMapStep.ThrottleUp, RcMapStep.ThrottleDown, RcMapStep.YawRight, RcMapStep.YawLeft,
            RcMapStep.RollRight, RcMapStep.RollLeft, RcMapStep.PitchForward, RcMapStep.PitchBack, RcMapStep.Review,
        }, RadioChannelMapper.Steps.Select(s => s.Step).ToArray());
        // ArduPilot: throttle up, yaw right, roll right = high PWM; pitch forward = low PWM
        Assert.AreEqual(+1, RadioChannelMapper.Steps[1].ExpectedSign);
        Assert.AreEqual(+1, RadioChannelMapper.Steps[3].ExpectedSign);
        Assert.AreEqual(+1, RadioChannelMapper.Steps[5].ExpectedSign);
        Assert.AreEqual(-1, RadioChannelMapper.Steps[7].ExpectedSign);
    }
}
