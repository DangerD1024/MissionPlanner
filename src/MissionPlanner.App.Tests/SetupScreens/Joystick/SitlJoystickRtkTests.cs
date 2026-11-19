using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Joystick;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Rtk;
using MissionPlanner.App.Tests.SetupScreens.Rtk;

namespace MissionPlanner.App.Tests.SetupScreens.Joystick;

/// <summary>
/// Against a running ArduCopter SITL. Opt in with MP_TEST_SITL=tcp:127.0.0.1:5760 (or the
/// instance's port). RC overrides move the vehicle's RC inputs, a joystick button arms it in
/// Guided, and GPS_RTCM_DATA goes out on the link.
/// </summary>
[TestClass]
public class SitlJoystickRtkTests
{
    public TestContext TestContext { get; set; } = null!;

    private static LinkSpec? Sitl()
    {
        var spec = Environment.GetEnvironmentVariable("MP_TEST_SITL");
        return string.IsNullOrWhiteSpace(spec) ? null : LinkSpec.Parse(spec);
    }

    [TestMethod]
    [TestCategory("Sitl")]
    [Timeout(240000, CooperativeCancellation = true)]
    public async Task RcOverrideMovesInputsAndAButtonArmsInGuided()
    {
        var spec = Sitl();
        if (spec == null)
            Assert.Inconclusive("set MP_TEST_SITL to run against SITL");

        var services = new SetupTestUi().CreateServices();
        await services.Session.ConnectAsync(spec, getParams: true);
        try
        {
            var session = services.Session;
            var vehicle = new SessionJoystickVehicle(session);
            await using var controller = new JoystickController(vehicle);
            var pad = new FakeJoystickDevice(new JoystickDeviceInfo("fake", "SITL pad", "fake"));
            var profile = JoystickProfile.CreateDefault("SITL pad", 4);
            for (var ch = 1; ch <= 4; ch++)
                profile.Channel(ch).Input = JoystickInput.Axis(ch - 1);
            profile.Channel(6).Input = JoystickInput.Axis(4);
            profile.Buttons[0].Button = 0;
            profile.Buttons[0].Function = JoystickButtonFunction.Arm;
            profile.Buttons[1].Button = 1;
            profile.Buttons[1].Function = JoystickButtonFunction.Disarm;
            profile.Buttons[2].Button = 2;
            profile.Buttons[2].Function = JoystickButtonFunction.ChangeMode;
            profile.Buttons[2].Mode = "Guided";
            controller.Profile = profile;
            controller.Attach(pad);
            controller.Start();

            pad.AxisValues[0] = 16384; // roll 3/4
            pad.AxisValues[1] = -16384; // pitch 1/4
            pad.AxisValues[2] = short.MinValue; // throttle down
            pad.AxisValues[4] = short.MaxValue; // channel 6 high
            controller.SetSending(true);

            var cs = session.Cs;
            Assert.IsTrue(await Wait.Until(() => controller.Outputs[5] != 0, TimeSpan.FromSeconds(2)));
            var expected = controller.Outputs;
            TestContext.WriteLine($"sending ch1..6 = {string.Join(",", expected.Take(6))}");
            var moved = await Wait.Until(() =>
                Math.Abs(cs.ch1in - expected[0]) <= 2 && Math.Abs(cs.ch2in - expected[1]) <= 2 &&
                Math.Abs(cs.ch3in - expected[2]) <= 2 && Math.Abs(cs.ch6in - expected[5]) <= 2, TimeSpan.FromSeconds(15));
            TestContext.WriteLine($"vehicle RC in: {cs.ch1in} {cs.ch2in} {cs.ch3in} {cs.ch4in} {cs.ch5in} {cs.ch6in}");
            Assert.IsTrue(moved, "RC_CHANNELS reflects the override");

            // move the stick: the vehicle follows
            pad.AxisValues[0] = -16384;
            await Task.Delay(100);
            var left = controller.Outputs[0];
            Assert.IsTrue(await Wait.Until(() => Math.Abs(cs.ch1in - left) <= 2, TimeSpan.FromSeconds(5)), $"ch1in {cs.ch1in} vs {left}");

            // sticks centred and throttle down, as ArduPilot's arming checks want
            pad.AxisValues[0] = 0;
            pad.AxisValues[1] = 0;
            pad.AxisValues[3] = 0;

            // a button changes mode, another arms (retried while the EKF settles)
            pad.ButtonValues[2] = true;
            Assert.IsTrue(await Wait.Until(() => cs.mode == "Guided", TimeSpan.FromSeconds(15)), "mode " + cs.mode);
            pad.ButtonValues[2] = false;

            var until = DateTime.UtcNow.AddSeconds(120);
            while (!cs.armed && DateTime.UtcNow < until)
            {
                pad.ButtonValues[0] = true;
                await Wait.Until(() => cs.armed, TimeSpan.FromSeconds(4));
                pad.ButtonValues[0] = false;
                await Task.Delay(300);
                TestContext.WriteLine("arm: " + controller.LastAction + " / " + cs.messageHigh);
            }

            Assert.IsTrue(cs.armed, "armed by the joystick button in Guided");
            TestContext.WriteLine("armed in " + cs.mode + ": " + controller.LastAction);

            pad.ButtonValues[1] = true;
            Assert.IsTrue(await Wait.Until(() => !cs.armed, TimeSpan.FromSeconds(15)), "disarmed by the joystick button");
            pad.ButtonValues[1] = false;

            controller.SetSending(false);
            TestContext.WriteLine($"{controller.PacketsSent} RC_CHANNELS_OVERRIDE packets sent");
        }
        finally
        {
            await services.Session.DisconnectAsync();
        }
    }

    [TestMethod]
    [TestCategory("Sitl")]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task GpsRtcmDataIsSentToSitl()
    {
        var spec = Sitl();
        if (spec == null)
            Assert.Inconclusive("set MP_TEST_SITL to run against SITL");

        var services = new SetupTestUi().CreateServices();
        await services.Session.ConnectAsync(spec, getParams: true);
        try
        {
            var session = services.Session;
            var sent = 0;
            var sentBytes = 0;
            session.Mav.OnPacketSent += (_, m) =>
            {
                if (m.msgid == (uint)MAVLink.MAVLINK_MSG_ID.GPS_RTCM_DATA)
                {
                    Interlocked.Increment(ref sent);
                    Interlocked.Add(ref sentBytes, m.ToStructure<MAVLink.mavlink_gps_rtcm_data_t>().len);
                }
            };

            var burst = RtcmFrameBuilder.Type1005(-35.363261, 149.165230, 584)
                .Concat(RtcmFrameBuilder.Msm4(1074, [2, 5, 13, 15, 18, 20, 25, 29], [44, 45, 40, 38, 47, 42, 36, 41]))
                .Concat(RtcmFrameBuilder.Padded(1077, 600)).ToArray();
            await using var server = new RtcmTcpServer(burst, dropAfterBurst: false);
            await using var injector = new RtkInjector(new SessionRtcmSink(session));
            injector.Start(new TcpRtcmSource("127.0.0.1", server.Port));

            var hbBefore = session.Vehicle.cs.datetime;
            Assert.IsTrue(await Wait.Until(() => Volatile.Read(ref sent) >= 30, TimeSpan.FromSeconds(30)), "GPS_RTCM_DATA sent");
            await injector.StopAsync();

            var st = injector.GetStatus();
            TestContext.WriteLine($"{st.FramesForwarded} frames, {st.PacketsSent} GPS_RTCM_DATA, {st.BytesForwarded} bytes; " +
                                  $"on the wire {sent} packets / {sentBytes} bytes; GPS_INJECT_TO = {services.Params.Get("GPS_INJECT_TO")}, " +
                                  $"GPS fix {session.Cs.gpsstatus}");
            Assert.AreEqual(st.PacketsSent, Volatile.Read(ref sent));
            Assert.AreEqual(st.BytesForwarded, Volatile.Read(ref sentBytes));
            Assert.IsTrue(session.IsConnected, "the link stays up");
            Assert.IsTrue(await Wait.Until(() => session.Vehicle.cs.datetime > hbBefore, TimeSpan.FromSeconds(5)));
        }
        finally
        {
            await services.Session.DisconnectAsync();
        }
    }
}
