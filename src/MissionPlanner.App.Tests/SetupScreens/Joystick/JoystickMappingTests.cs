using System.Text.Json;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Joystick;

namespace MissionPlanner.App.Tests.SetupScreens.Joystick;

[TestClass]
public class JoystickMappingTests
{
    [TestMethod]
    public void AxisRangeMapsToMissionPlannersScale()
    {
        Assert.AreEqual(-500, JoystickMapper.Normalise(short.MinValue), 1e-9);
        Assert.AreEqual(500, JoystickMapper.Normalise(short.MaxValue), 1e-9);
        Assert.AreEqual(0, JoystickMapper.Normalise(0), 0.01);
    }

    [TestMethod]
    public void LinearMappingReverseAndTrim()
    {
        var r = RcRange.Default;
        Assert.AreEqual(1500, JoystickMapper.Pwm(0, false, 0, r));
        Assert.AreEqual(2000, JoystickMapper.Pwm(500, false, 0, r));
        Assert.AreEqual(1000, JoystickMapper.Pwm(-500, false, 0, r));
        Assert.AreEqual(1000, JoystickMapper.Pwm(500, true, 0, r));
        Assert.AreEqual(1750, JoystickMapper.Pwm(JoystickMapper.Normalise(16384), false, 0, r));
        Assert.AreEqual(1550, JoystickMapper.Pwm(0, false, 0, r, trimOffset: 50));
        Assert.AreEqual(2000, JoystickMapper.Pwm(500, false, 0, r, trimOffset: 100), "limited to the channel range");

        // an off-centre trim splits the travel unevenly, like Mission Planner
        var off = new RcRange(1100, 1900, 1600);
        Assert.AreEqual(1600, JoystickMapper.Pwm(0, false, 0, off));
        Assert.AreEqual(1350, JoystickMapper.Pwm(-250, false, 0, off));
        Assert.AreEqual(1750, JoystickMapper.Pwm(250, false, 0, off));
    }

    [TestMethod]
    public void ExpoSoftensTheMiddleAndKeepsTheEnds()
    {
        var r = RcRange.Default;
        Assert.AreEqual(1625, JoystickMapper.Pwm(250, false, 50, r));
        Assert.AreEqual(1375, JoystickMapper.Pwm(-250, false, 50, r));
        Assert.AreEqual(2000, JoystickMapper.Pwm(500, false, 50, r));
        Assert.AreEqual(1000, JoystickMapper.Pwm(-500, false, 50, r));
        Assert.AreEqual(1500, JoystickMapper.Pwm(0, false, 50, r));
        Assert.AreEqual(1875, JoystickMapper.Pwm(250, false, -50, r), "negative expo sharpens");
        Assert.AreEqual(JoystickBaseExpo(250, 30), JoystickMapper.Expo(250, 30, 1000, 2000, 1500), 1e-9);
    }

    // Mission Planner's JoystickBase.Expo, kept here as the reference
    private static double JoystickBaseExpo(double input, double expo) =>
        MissionPlanner.Joystick.JoystickBase.Expo(input, expo, 1000, 2000, 1500);

    [TestMethod]
    public void RangesComeFromRcParametersAndThrottleCentres()
    {
        var p = new Dictionary<string, double>
        {
            ["RC1_MIN"] = 1100, ["RC1_MAX"] = 1900, ["RC1_TRIM"] = 1520,
            ["RC3_MIN"] = 1000, ["RC3_MAX"] = 2000, ["RC3_TRIM"] = 1000,
        };
        double? P(string n) => p.TryGetValue(n, out var v) ? v : null;
        Assert.AreEqual(new RcRange(1100, 1900, 1520), JoystickMapper.RangeFor(1, P));
        Assert.AreEqual(new RcRange(1000, 2000, 1500), JoystickMapper.RangeFor(3, P), "throttle centres on mid-range");
        Assert.AreEqual(RcRange.Default, JoystickMapper.RangeFor(7, P));

        p["RCMAP_THROTTLE"] = 1;
        Assert.AreEqual(new RcRange(1100, 1900, 1500), JoystickMapper.RangeFor(1, P));
        Assert.AreEqual(new RcRange(1000, 2000, 1000), JoystickMapper.RangeFor(3, P));
    }

    [TestMethod]
    public void OverridePacketIgnoresUnmappedChannelsAndReleasesMapped()
    {
        var pwm = new ushort[16];
        pwm[0] = 1600;
        pwm[2] = 1200;
        pwm[9] = 1800;
        var rc = JoystickMapper.Override(pwm, 7, 1);
        Assert.AreEqual(7, rc.target_system);
        Assert.AreEqual(1, rc.target_component);
        Assert.AreEqual(1600, rc.chan1_raw);
        Assert.AreEqual(ushort.MaxValue, rc.chan2_raw, "1-8: UINT16_MAX = ignore");
        Assert.AreEqual(1200, rc.chan3_raw);
        Assert.AreEqual(1800, rc.chan10_raw);
        Assert.AreEqual(0, rc.chan9_raw, "9-18: 0 = ignore");
        Assert.AreEqual(0, rc.chan17_raw);

        var release = JoystickMapper.Override(pwm, 7, 1, release: true);
        Assert.AreEqual(0, release.chan1_raw, "1-8: 0 = back to the receiver");
        Assert.AreEqual(ushort.MaxValue, release.chan2_raw);
        Assert.AreEqual(ushort.MaxValue - 1, release.chan10_raw, "9-18: UINT16_MAX-1 = back to the receiver");
        Assert.AreEqual(0, release.chan11_raw);
    }

    [TestMethod]
    public void ManualControlFollowsTheMavlinkAxes()
    {
        var pwm = new ushort[16];
        pwm[0] = 2000; // roll right
        pwm[1] = 1000; // pitch stick forward (low PWM)
        pwm[2] = 1250; // throttle quarter
        var mc = JoystickMapper.ManualControl(pwm, _ => RcRange.Default, 1, 2, 3, 4, 1, 0b101);
        Assert.AreEqual(1000, mc.y);
        Assert.AreEqual(1000, mc.x, "forward is positive x");
        Assert.AreEqual(250, mc.z);
        Assert.AreEqual(short.MaxValue, mc.r, "unmapped yaw is INT16_MAX");
        Assert.AreEqual(0b101, mc.buttons);
        Assert.AreEqual(1, mc.target);
    }

    [TestMethod]
    public void ElevonMixing()
    {
        Assert.AreEqual(((ushort)1500, (ushort)1500), JoystickMapper.Elevons(1500, 1500, false, false));
        // full right roll: one elevon up, the other down
        Assert.AreEqual(((ushort)1250, (ushort)1750), JoystickMapper.Elevons(2000, 1500, false, false));
        // full pitch: both the same way
        Assert.AreEqual(((ushort)1750, (ushort)1750), JoystickMapper.Elevons(1500, 2000, false, false));
        Assert.AreEqual(((ushort)1250, (ushort)1750), JoystickMapper.Elevons(1500, 2000, true, false));
    }

    [TestMethod]
    public void HatsAccumulateAndHoldTheirValue()
    {
        var hat = new HatAccumulator();
        for (var i = 0; i < 10; i++)
            hat.Update(1);
        Assert.AreEqual(10 * HatAccumulator.Step, hat.Value, 1e-9);
        hat.Update(0);
        Assert.AreEqual(10 * HatAccumulator.Step, hat.Value, 1e-9);
        for (var i = 0; i < 1000; i++)
            hat.Update(-1);
        Assert.AreEqual(-500, hat.Value);
    }

    [TestMethod]
    public void InputsHaveAStableTextForm()
    {
        foreach (var input in new[]
                 {
                     JoystickInput.None, JoystickInput.Axis(3), new JoystickInput(JoystickInputKind.HatVertical, 1),
                     new JoystickInput(JoystickInputKind.HatHorizontal, 0), new JoystickInput(JoystickInputKind.ButtonAxis, 1),
                     new JoystickInput(JoystickInputKind.Trim),
                 })
            Assert.AreEqual(input, JoystickInput.Parse(input.ToString()));

        Assert.AreEqual(new JoystickInput(JoystickInputKind.Trim), JoystickInput.Parse("Pass"));
        Assert.AreEqual(JoystickInput.None, JoystickInput.Parse("rubbish"));
        Assert.AreEqual("Axis 4", JoystickInput.Axis(3).Label);
    }

    [TestMethod]
    public void ProfilesRoundTripPerDevice()
    {
        var store = new JoystickProfileStore(Path.Combine(TestEnvironment.DataRoot, "joy-" + Guid.NewGuid().ToString("N")));
        var p = JoystickProfile.CreateDefault("Logitech Extreme 3D / USB", 4);
        p.ManualControl = true;
        p.Channel(1).Input = JoystickInput.Axis(0);
        p.Channel(1).Reverse = true;
        p.Channel(1).Expo = 30;
        p.Channel(3).Input = new JoystickInput(JoystickInputKind.HatVertical, 0);
        p.Channel(3).Trim = -20;
        p.Buttons[0].Button = 2;
        p.Buttons[0].Function = JoystickButtonFunction.ChangeMode;
        p.Buttons[0].Mode = "Loiter";
        p.Buttons[1].Function = JoystickButtonFunction.Do_Set_Servo;
        p.Buttons[1].P1 = 9;
        p.Buttons[1].P2 = 1900;
        store.Save(p);

        StringAssert.EndsWith(store.PathFor(p.Device), "Logitech_Extreme_3D___USB.json");
        var json = File.ReadAllText(store.PathFor(p.Device));
        StringAssert.Contains(json, "\"axis:0\"");
        StringAssert.Contains(json, "\"ChangeMode\"");

        var back = store.Load("Logitech Extreme 3D / USB")!;
        Assert.IsTrue(back.ManualControl);
        Assert.HasCount(16, back.Channels);
        Assert.AreEqual(JoystickInput.Axis(0), back.Channel(1).Input);
        Assert.IsTrue(back.Channel(1).Reverse);
        Assert.AreEqual(30, back.Channel(1).Expo);
        Assert.AreEqual(new JoystickInput(JoystickInputKind.HatVertical, 0), back.Channel(3).Input);
        Assert.AreEqual(-20, back.Channel(3).Trim);
        Assert.AreEqual("Loiter", back.Buttons[0].Mode);
        Assert.AreEqual(JoystickButtonFunction.Do_Set_Servo, back.Buttons[1].Function);
        Assert.AreEqual(1900f, back.Buttons[1].P2);
        Assert.IsNull(store.Load("Another pad"));
        Assert.AreEqual("joystick", JoystickProfileStore.FileName("///"));
    }

    [TestMethod]
    public void JoydevEventsDecode()
    {
        var state = new JoydevState(2, 2);
        static byte[] Ev(short value, byte type, byte number)
        {
            var b = new byte[8];
            BitConverter.GetBytes(value).CopyTo(b, 4);
            b[6] = type;
            b[7] = number;
            return b;
        }

        state.Apply(Ev(0, JoydevState.EventAxis | JoydevState.EventInit, 0));
        state.Apply(Ev(-12000, JoydevState.EventAxis, 1));
        state.Apply(Ev(1, JoydevState.EventButton, 1));
        state.Apply(Ev(1, JoydevState.EventButton, 5)); // more buttons than the driver said
        var s = state.Snapshot();
        Assert.AreEqual(-12000, s.Axis(1));
        Assert.IsTrue(s.Button(1));
        Assert.IsTrue(s.Button(5));
        Assert.IsFalse(s.Button(0));
        state.Apply(Ev(0, JoydevState.EventButton, 1));
        Assert.IsFalse(state.Snapshot().Button(1));
    }

    [TestMethod]
    public void BackendChoiceExplainsMissingSdl()
    {
        var prev = Environment.GetEnvironmentVariable("MP_JOYSTICK_BACKEND");
        try
        {
            Environment.SetEnvironmentVariable("MP_JOYSTICK_BACKEND", "none");
            using var none = JoystickBackends.Create();
            Assert.IsInstanceOfType<NoJoystickBackend>(none);
            Assert.IsEmpty(none.GetDevices());

            if (OperatingSystem.IsLinux())
            {
                Environment.SetEnvironmentVariable("MP_JOYSTICK_BACKEND", "joydev");
                using var joydev = JoystickBackends.Create();
                Assert.IsInstanceOfType<LinuxJoydevBackend>(joydev);
            }

            StringAssert.Contains(JoystickBackends.MissingSdlHint(), "SDL2");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MP_JOYSTICK_BACKEND", prev);
        }
    }

    [TestMethod]
    public void Sdl2LoadsWhenInstalled()
    {
        // the real native library, if this machine has it: SDL starts and lists (probably no) devices
        if (!Sdl2JoystickBackend.TryCreate(out var sdl, out var error))
            Assert.Inconclusive("SDL2 not available: " + error);
        using (sdl)
        {
            StringAssert.StartsWith(sdl!.Status, "SDL 2.");
            Assert.IsNotNull(sdl.GetDevices());
        }
    }
}
