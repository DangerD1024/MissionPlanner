using MissionPlanner.App.Core.ViewModels.SetupScreens;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Joystick;
using MissionPlanner.App.Tests.SetupScreens.Rtk;

namespace MissionPlanner.App.Tests.SetupScreens.Joystick;

[TestClass]
public class JoystickControllerTests
{
    private static (JoystickController C, FakeJoystickDevice D, FakeJoystickVehicle V) Setup()
    {
        var v = new FakeJoystickVehicle();
        var c = new JoystickController(v);
        var d = new FakeJoystickDevice(new JoystickDeviceInfo("fake:0", "Pad", "fake"));
        var p = JoystickProfile.CreateDefault("Pad", 4);
        p.Channel(1).Input = JoystickInput.Axis(0);
        p.Channel(2).Input = JoystickInput.Axis(1);
        p.Channel(2).Reverse = true;
        p.Channel(3).Input = JoystickInput.Axis(2);
        p.Channel(4).Input = JoystickInput.Axis(3);
        p.Channel(5).Input = new JoystickInput(JoystickInputKind.HatVertical, 0);
        p.Channel(6).Input = new JoystickInput(JoystickInputKind.ButtonAxis, 0);
        p.Channel(7).Input = new JoystickInput(JoystickInputKind.Trim);
        p.Channel(7).Trim = 100;
        p.Buttons[0].Button = 0;
        p.Buttons[0].Function = JoystickButtonFunction.Arm;
        p.Buttons[1].Button = 1;
        p.Buttons[1].Function = JoystickButtonFunction.Do_Set_Relay;
        p.Buttons[1].P1 = 2;
        p.Buttons[2].Button = 2;
        p.Buttons[2].Function = JoystickButtonFunction.Button_axis0;
        p.Buttons[2].P1 = 1100;
        p.Buttons[2].P2 = 1900;
        p.Buttons[3].Button = 3;
        p.Buttons[3].Function = JoystickButtonFunction.ChangeMode;
        p.Buttons[3].Mode = "Loiter";
        c.Profile = p;
        c.Attach(d);
        return (c, d, v);
    }

    [TestMethod]
    public async Task OutputsFollowTheMappingAndNothingIsSentUntilEnabled()
    {
        var (c, d, v) = Setup();
        await using (c)
        {
            d.AxisValues[0] = short.MaxValue;
            d.AxisValues[1] = short.MaxValue;
            d.AxisValues[2] = short.MinValue;
            c.Step();

            var o = c.Outputs;
            Assert.AreEqual(2000, o[0]);
            Assert.AreEqual(1000, o[1], "reversed");
            Assert.AreEqual(1000, o[2]);
            Assert.AreEqual(1500, o[3]);
            Assert.AreEqual(1500, o[4], "hat starts centred");
            Assert.AreEqual(1100, o[5], "button axis rests at its off PWM");
            Assert.AreEqual(1600, o[6], "trim input plus offset");
            Assert.AreEqual(0, o[7], "unmapped");
            Assert.IsEmpty(v.Overrides);

            // buttons do nothing while not sending, but the button axis previews
            d.ButtonValues[0] = true;
            d.ButtonValues[2] = true;
            c.Step();
            Assert.AreEqual(1900, c.Outputs[5]);
            await Task.Delay(100);
            Assert.IsEmpty(v.Actions);
        }
    }

    [TestMethod]
    public async Task SendingStreamsOverridesAndButtonsActOncePerPress()
    {
        var (c, d, v) = Setup();
        await using (c)
        {
            c.SetSending(true);
            d.AxisValues[0] = 16384;
            c.Step();
            Assert.HasCount(1, v.Overrides);
            var rc = v.Overrides.Last();
            Assert.AreEqual(1750, rc.chan1_raw);
            Assert.AreEqual(1500, rc.chan2_raw);
            Assert.AreEqual(ushort.MaxValue, rc.chan8_raw);
            Assert.AreEqual(0, rc.chan9_raw);
            Assert.AreEqual(1, rc.target_system);

            // Arm on press only, not while held
            d.ButtonValues[0] = true;
            c.Step();
            c.Step();
            c.Step();
            // the relay follows the button both ways
            d.ButtonValues[1] = true;
            c.Step();
            d.ButtonValues[1] = false;
            c.Step();
            d.ButtonValues[3] = true;
            c.Step();

            Assert.IsTrue(await Wait.Until(() => v.Actions.Count >= 4, TimeSpan.FromSeconds(5)));
            await Task.Delay(100);
            CollectionAssert.AreEqual(new[] { "arm", "DO_SET_RELAY 2 1 0 0 0 0 0", "DO_SET_RELAY 2 0 0 0 0 0 0", "mode Loiter" }, v.Actions.ToArray());
            Assert.AreEqual("Mode Loiter requested", c.LastAction);
            Assert.AreEqual(7, c.PacketsSent);

            // the hat moves channel 5 while held and leaves it there
            d.HatValues[0] = HatDirection.Up;
            for (var i = 0; i < 10; i++)
                c.Step();
            d.HatValues[0] = HatDirection.Centered;
            c.Step();
            Assert.AreEqual(1500 + (int)(10 * HatAccumulator.Step), c.Outputs[4]);

            // disabling hands the mapped channels back to the receiver
            var before = v.Overrides.Count;
            c.SetSending(false);
            var release = v.Overrides.Skip(before).ToList();
            Assert.HasCount(3, release);
            Assert.AreEqual(0, release[0].chan1_raw);
            Assert.AreEqual(ushort.MaxValue, release[0].chan8_raw, "unmapped stays ignored");
            c.Step();
            Assert.AreEqual(before + 3, v.Overrides.Count);
        }
    }

    [TestMethod]
    public async Task NothingIsSentWithoutAVehicle()
    {
        var (c, d, v) = Setup();
        await using (c)
        {
            v.IsConnected = false;
            c.SetSending(true);
            d.ButtonValues[0] = true;
            c.Step();
            c.Step();
            await Task.Delay(100);
            Assert.IsEmpty(v.Overrides);
            Assert.IsEmpty(v.Actions);
        }
    }

    [TestMethod]
    public async Task ManualControlOption()
    {
        var (c, d, v) = Setup();
        await using (c)
        {
            c.Profile.ManualControl = true;
            c.SetSending(true);
            d.AxisValues[0] = short.MaxValue; // roll right
            d.AxisValues[1] = short.MinValue; // reversed: pitch PWM 2000
            d.ButtonValues[2] = true;
            c.Step();
            Assert.IsEmpty(v.Overrides);
            var mc = v.ManualControls.Single();
            Assert.AreEqual(1000, mc.y);
            Assert.AreEqual(-1000, mc.x, "pitch PWM high = stick back = negative x");
            Assert.AreEqual(500, mc.z);
            Assert.AreEqual(0, mc.r);
            Assert.AreEqual(0b100, mc.buttons);
        }
    }

    [TestMethod]
    public async Task UnpluggingStopsSendingAndReleases()
    {
        var (c, d, v) = Setup();
        await using (c)
        {
            var lost = false;
            c.DeviceLost += (_, _) => lost = true;
            c.SetSending(true);
            c.Step();
            d.IsAttached = false;
            c.Step();
            c.Step();
            Assert.IsTrue(lost);
            Assert.IsFalse(c.IsSending);
            StringAssert.Contains(c.Lost, "Pad");
            Assert.HasCount(4, v.Overrides, "one stream packet, three release packets");
            Assert.AreEqual(0, v.Overrides.Last().chan1_raw);
        }
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task LoopSendsAt25Hz()
    {
        var (c, _, v) = Setup();
        await using (c)
        {
            c.Start();
            await Task.Delay(300);
            Assert.IsEmpty(v.Overrides);
            c.SetSending(true);
            var start = v.Overrides.Count;
            await Task.Delay(2000);
            var sent = v.Overrides.Count - start;
            Assert.IsGreaterThan(40, sent, $"{sent} packets in 2 s");
            Assert.IsLessThan(60, sent, $"{sent} packets in 2 s");
            await c.StopAsync();
        }
    }

    [TestMethod]
    public async Task ButtonFunctionsMapToMissionPlannersCommands()
    {
        var v = new FakeJoystickVehicle { IsCopter = true };
        await using var c = new JoystickController(v);
        string Run(JoystickButtonFunction f, float p1 = 0, float p2 = 0, float p3 = 0, float p4 = 0) =>
            c.Run(new ButtonMapping { Function = f, P1 = p1, P2 = p2, P3 = p3, P4 = p4 }, true);

        Run(JoystickButtonFunction.Disarm);
        Run(JoystickButtonFunction.Do_Set_Servo, 9, 1900);
        Run(JoystickButtonFunction.Do_Repeat_Servo, 9, 1900, 3, 2);
        Run(JoystickButtonFunction.Do_Repeat_Relay, 1, 4, 2);
        Run(JoystickButtonFunction.Digicam_Control);
        Run(JoystickButtonFunction.TakeOff);
        Run(JoystickButtonFunction.Mount_Mode, 3);
        v.Params["MNT_MODE"] = 0;
        Run(JoystickButtonFunction.Mount_Mode, 2);
        Assert.Contains("MNT_STAB_PAN not", Run(JoystickButtonFunction.Toggle_Pan_Stab));
        v.Params["MNT_STAB_PAN"] = 1;
        Run(JoystickButtonFunction.Toggle_Pan_Stab);
        Run(JoystickButtonFunction.Gimbal_pnt_track);
        v.CommandResult = false;
        Assert.AreEqual("Arm: refused", Run(JoystickButtonFunction.Arm));

        CollectionAssert.AreEqual(new[]
        {
            "disarm",
            "DO_SET_SERVO 9 1900 0 0 0 0 0",
            "DO_REPEAT_SERVO 9 1900 3 2 0 0 0",
            "DO_REPEAT_RELAY 1 4 2 0 0 0 0",
            "DO_DIGICAM_CONTROL 0 0 0 0 1 0 0",
            "mode Guided",
            "TAKEOFF 0 0 0 0 0 0 2",
            "DO_MOUNT_CONFIGURE 3 0 0 0 0 0 0",
            "param MNT_MODE=2",
            "param MNT_STAB_PAN=0",
            "roi",
            "arm",
        }, v.Actions.ToArray());
    }

    [TestMethod]
    public void DetectFindsTheMovedAxisOrHat()
    {
        var before = new JoystickSnapshot([0, 0, 0], [false], [HatDirection.Centered]);
        Assert.IsNull(JoystickViewModel.DetectInput(before, new JoystickSnapshot([3000, -2000, 0], [true], [HatDirection.Centered])));
        Assert.AreEqual(JoystickInput.Axis(2),
            JoystickViewModel.DetectInput(before, new JoystickSnapshot([17000, 0, -32000], [false], [HatDirection.Centered])));
        Assert.AreEqual(new JoystickInput(JoystickInputKind.HatHorizontal, 0),
            JoystickViewModel.DetectInput(before, new JoystickSnapshot([0, 0, 0], [false], [HatDirection.Left])));
        Assert.AreEqual(new JoystickInput(JoystickInputKind.HatVertical, 0),
            JoystickViewModel.DetectInput(before, new JoystickSnapshot([0, 0, 0], [false], [HatDirection.Up | HatDirection.Right])));
    }
}
