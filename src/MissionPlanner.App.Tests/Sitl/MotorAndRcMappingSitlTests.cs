using System.Diagnostics;
using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.ViewModels.SetupScreens;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;
using MissionPlanner.App.Core.ViewModels.SetupScreens.MotorSetup;
using MissionPlanner.App.Core.ViewModels.SetupScreens.RadioMapping;
using MissionPlanner.App.Tests.Logs;

namespace MissionPlanner.App.Tests.Sitl;

/// <summary>
/// Motor order / direction / frame reversal on Servo Output and the channel mapping on Radio
/// Calibration, against ArduCopter SITL (Quad +, instance 3). Opt in with MP_TEST_SITL=1. Every
/// test puts the parameters it changes back.
/// </summary>
[TestClass]
[TestCategory("Sitl")]
public class MotorAndRcMappingSitlTests
{
    private const int Instance = 3;
    private static SitlProcess? _sitl;
    private static AppServices? _services;
    private static FakeUiServices? _ui;

    public TestContext TestContext { get; set; } = null!;

    /// <summary>The shipped APMotorLayout.json (the test output folder has no copy next to it).</summary>
    private static MotorLayoutCatalog Layouts => SetupScreens.MotorSetup.Layouts.Catalog;

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        if (Environment.GetEnvironmentVariable("MP_TEST_SITL") != "1")
            return;
        var binary = SitlProcess.FindBinary("ArduCopter");
        if (binary == null)
            return;

        _sitl = await SitlProcess.StartAsync(binary, "+", "copter.parm", Instance);
        _ui = new FakeUiServices();
        _services = _ui.CreateServices();
        await _services.Session.ConnectAsync(LinkSpec.Tcp("127.0.0.1", _sitl.Port), getParams: true, null);
    }

    [ClassCleanup]
    public static async Task Stop()
    {
        if (_services != null)
            await _services.Session.DisposeAsync();
        _sitl?.Dispose();
    }

    private AppServices Services
    {
        get
        {
            if (Environment.GetEnvironmentVariable("MP_TEST_SITL") != "1")
                Assert.Inconclusive("set MP_TEST_SITL=1 to run against ArduPilot SITL");
            if (_services == null || !_services.Session.IsConnected)
                Assert.Inconclusive("ArduCopter SITL binary not found (MP_SITL_DIR or <data dir>/sitl/)");
            _ui!.ConfirmAnswer = true;
            return _services;
        }
    }

    private float Read(string name)
    {
        var s = Services.Session;
        return s.Mav.GetParam(s.Vehicle.sysid, s.Vehicle.compid, name);
    }

    private Task<float> ReadAsync(string name) => Task.Run(() => Read(name));

    private async Task Restore(string name, double value)
    {
        if (!await Services.Params.SetAsync(name, value))
            TestContext.WriteLine("could not restore " + name);
    }

    private void RequestOutputs()
    {
        var s = Services.Session;
        s.Cs.raterc = 10;
        s.Mav.requestDatastream(MAVLink.MAV_DATA_STREAM.RC_CHANNELS, 10, s.Vehicle.sysid, s.Vehicle.compid);
    }

    /// <summary>The one output of 1-4 driven above idle, or 0.</summary>
    private static int MovingOutput(CurrentState cs)
    {
        float[] outs = [cs.ch1out, cs.ch2out, cs.ch3out, cs.ch4out];
        var max = outs.Max();
        var i = Array.IndexOf(outs, max);
        return max >= 1050 && outs.Where((_, j) => j != i).All(v => v <= max - 40) ? i + 1 : 0;
    }

    private static async Task WaitFor(Func<bool> condition, TimeSpan timeout, string what, Action? each = null)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            each?.Invoke();
            if (condition())
                return;
            if (sw.Elapsed > timeout)
                Assert.Fail("timed out waiting for " + what);
            await Task.Delay(50);
        }
    }

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task MotorOrderWizardSpinsEachOutputAndWritesASwappedMapping()
    {
        var s = Services.Session;
        Assert.AreEqual(0, (int)Read("FRAME_TYPE"), "SITL runs a Quad +");
        RequestOutputs();

        var page = new ServoOutputViewModel(Services, null, Layouts);
        page.OnShown();
        var ms = page.MotorSetup;
        Assert.IsTrue(ms.IsAvailable, "multirotor card");
        Assert.AreEqual(0, ms.Layout!.Type);

        try
        {
            ms.StartOrderCommand.Execute(null);
            ms.PropsRemoved = true;
            ms.Throttle = 10;
            await ms.BeginCommand.ExecuteAsync(null);
            Assert.IsTrue(ms.IsIdentifying, ms.Status.Text);

            // the vehicle is "wired" with outputs 1 and 2 swapped
            var physical = new Dictionary<int, int> { [1] = 2, [2] = 1, [3] = 3, [4] = 4 };
            for (var step = 0; step < 4; step++)
            {
                var current = ms.Wizard!.Current!;
                var order = ms.Wizard.CurrentTestOrder!.Value;
                await WaitFor(() => MovingOutput(s.Cs) == current.Output, TimeSpan.FromSeconds(8),
                    $"output {current.Output} to spin (DO_MOTOR_TEST param1 {order}); outputs {s.Cs.ch1out} {s.Cs.ch2out} {s.Cs.ch3out} {s.Cs.ch4out}");
                TestContext.WriteLine($"param1 {order} (test {MotorInfoLetter(order)}) spun output {current.Output} (Motor{current.Motor}) at {Channels(s.Cs)}");
                // it keeps spinning while the user looks: the keep-alive outlasts the 1.5 s test timeout
                await Task.Delay(2500);
                Assert.AreEqual(current.Output, MovingOutput(s.Cs), "still spinning after the first timeout");
                await ms.MotorClickedCommand.ExecuteAsync(physical[current.Output]);
            }

            Assert.IsTrue(ms.IsReviewing, ms.Status.Text);
            await WaitFor(() => MovingOutput(s.Cs) == 0, TimeSpan.FromSeconds(5), "motors to stop");

            await ms.WriteMappingCommand.ExecuteAsync(null);
            Assert.AreEqual(34, (int)await ReadAsync("SERVO1_FUNCTION"));
            Assert.AreEqual(33, (int)await ReadAsync("SERVO2_FUNCTION"));
            Assert.AreEqual(35, (int)await ReadAsync("SERVO3_FUNCTION"));
            Assert.AreEqual(36, (int)await ReadAsync("SERVO4_FUNCTION"));
            Assert.IsTrue(page.RebootRequired, "reboot banner");
        }
        finally
        {
            await ms.StopCommand.ExecuteAsync(null);
            page.OnHidden();
            await Restore("SERVO1_FUNCTION", 33);
            await Restore("SERVO2_FUNCTION", 34);
        }

        Assert.AreEqual(33, (int)await ReadAsync("SERVO1_FUNCTION"));
        Assert.AreEqual(34, (int)await ReadAsync("SERVO2_FUNCTION"));
    }

    [TestMethod]
    [Timeout(90000, CooperativeCancellation = true)]
    public async Task DirectionModeSpinsAClickedMotorAndHandlesTheDirectionParameters()
    {
        var p = Services.Params;
        var hasMask = p.Has("SERVO_BLH_RVMASK");
        var hasEsc = p.Has("SERVO_DSHOT_ESC");
        var original = (Pwm: p.Get("MOT_PWM_TYPE") ?? 0, Esc: p.Get("SERVO_DSHOT_ESC") ?? 0, Mask: p.Get("SERVO_BLH_RVMASK") ?? 0);
        TestContext.WriteLine($"SITL: MOT_PWM_TYPE {p.Get("MOT_PWM_TYPE")}, SERVO_DSHOT_ESC {p.Get("SERVO_DSHOT_ESC")}, SERVO_BLH_RVMASK {p.Get("SERVO_BLH_RVMASK")}, " +
                              $"3DMASK {p.Get("SERVO_BLH_3DMASK")}, BDMASK {p.Get("SERVO_BLH_BDMASK")}");
        RequestOutputs();

        var page = new ServoOutputViewModel(Services, null, Layouts);
        page.OnShown();
        var ms = page.MotorSetup;
        try
        {
            TestContext.WriteLine("direction: " + ms.Direction!.Support + ": " + ms.DirectionMessage);
            Assert.AreEqual(hasMask ? DirectionSupport.NotDshot : DirectionSupport.NoReverseParam, ms.Direction.Support);
            Assert.IsFalse(ms.DirectionSupported);

            ms.StartDirectionCommand.Execute(null);
            ms.PropsRemoved = true;
            await ms.BeginCommand.ExecuteAsync(null);
            Assert.IsTrue(ms.IsDirectionRunning, ms.Status.Text);

            // Quad +: motor 2 is the left motor, test D, on output 2
            await ms.MotorClickedCommand.ExecuteAsync(2);
            await WaitFor(() => MovingOutput(Services.Session.Cs) == 2, TimeSpan.FromSeconds(8), "motor 2 on output 2 to spin");
            TestContext.WriteLine("spinning: " + ms.SpinText);
            Assert.IsTrue(ms.DirectionRows.All(r => !r.CanReverse), "nothing can be reversed in software here");
            await ms.MotorClickedCommand.ExecuteAsync(2);
            await WaitFor(() => MovingOutput(Services.Session.Cs) == 0, TimeSpan.FromSeconds(5), "a second click to stop it");

            if (!hasMask)
            {
                // still check the parameters SITL does have round-trip
                if (hasEsc)
                {
                    Assert.IsTrue(await p.SetAsync("SERVO_DSHOT_ESC", 1));
                    Assert.AreEqual(1, (int)await ReadAsync("SERVO_DSHOT_ESC"));
                }

                Assert.Inconclusive("SITL has no SERVO_BLH_RVMASK (no BLHeli support in this build): the reverse write was checked with the scripted vehicle only");
            }

            // DShot (takes effect only after a reboot, which this test does not do)
            Assert.IsTrue(await p.SetAsync("MOT_PWM_TYPE", 6));
            page.OnShown();
            Assert.AreEqual(DirectionSupport.EscTypeNone, ms.Direction!.Support);
            ms.SelectedEscType = ms.EscTypeChoices.First(e => e.Value == 1);
            await ms.SetEscTypeCommand.ExecuteAsync(null);
            Assert.AreEqual(1, (int)await ReadAsync("SERVO_DSHOT_ESC"));
            Assert.IsTrue(ms.DirectionSupported);

            var row = ms.DirectionRows.Single(r => r.Motor.Number == 2);
            row.IsReversed = true;
            await WaitFor(() => p.Get("SERVO_BLH_RVMASK") == 2, TimeSpan.FromSeconds(10), "the mask write");
            Assert.AreEqual(2, (int)await ReadAsync("SERVO_BLH_RVMASK"));
            Assert.IsTrue(page.RebootRequired);
        }
        finally
        {
            await ms.CloseModeCommand.ExecuteAsync(null);
            page.OnHidden();
            if (hasMask)
                await Restore("SERVO_BLH_RVMASK", original.Mask);
            if (hasEsc)
                await Restore("SERVO_DSHOT_ESC", original.Esc);
            await Restore("MOT_PWM_TYPE", original.Pwm);
            if (hasEsc)
                Assert.AreEqual(original.Esc, await ReadAsync("SERVO_DSHOT_ESC"));
            if (hasMask)
                Assert.AreEqual(original.Mask, await ReadAsync("SERVO_BLH_RVMASK"));
            Assert.AreEqual(original.Pwm, await ReadAsync("MOT_PWM_TYPE"));
        }
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task ReversedSwitchGoesFromXToHAndBack()
    {
        var p = Services.Params;
        var original = p.Get("FRAME_TYPE") ?? 0;
        var page = new ServoOutputViewModel(Services, null, Layouts);
        try
        {
            Assert.IsTrue(await p.SetAsync("FRAME_TYPE", 1));
            page.OnShown();
            var ms = page.MotorSetup;
            Assert.AreEqual(1, ms.Layout!.Type);
            Assert.IsTrue(ms.CanReverseFrame);
            Assert.IsFalse(ms.FrameReversed);

            ms.FrameReversed = true;
            await WaitFor(() => p.Get("FRAME_TYPE") == 3, TimeSpan.FromSeconds(10), "FRAME_TYPE = H");
            Assert.AreEqual(3, (int)await ReadAsync("FRAME_TYPE"));
            Assert.IsTrue(ms.FrameReversed);
            Assert.IsTrue(page.RebootRequired);

            ms.FrameReversed = false;
            await WaitFor(() => p.Get("FRAME_TYPE") == 1, TimeSpan.FromSeconds(10), "FRAME_TYPE = X");
            Assert.AreEqual(1, (int)await ReadAsync("FRAME_TYPE"));
        }
        finally
        {
            page.OnHidden();
            await Restore("FRAME_TYPE", original);
        }

        Assert.AreEqual(original, await ReadAsync("FRAME_TYPE"));
    }

    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public async Task ChannelMappingFindsSwappedAndReversedSticksFromOverrides()
    {
        var p = Services.Params;
        var session = Services.Session;
        string[] touched = ["RCMAP_ROLL", "RCMAP_PITCH", "RCMAP_THROTTLE", "RCMAP_YAW", "RC1_REVERSED", "RC2_REVERSED", "RC3_REVERSED", "RC4_REVERSED"];
        var original = touched.ToDictionary(n => n, n => p.Get(n) ?? 0);

        var vm = new RadioCalibrationViewModel(Services);
        vm.OnShown();
        var map = vm.Mapping;

        // the transmitter: roll on channel 2 moving the wrong way, pitch on 1, throttle on 3, yaw on 4 reversed
        ushort[] sticks = [1500, 1500, 1000, 1500, 1500, 1500, 1500, 1500];
        void Send()
        {
            var v = session.Vehicle;
            session.Mav.sendPacket(new MAVLink.mavlink_rc_channels_override_t
            {
                target_system = (byte)v.sysid, target_component = v.compid,
                chan1_raw = sticks[0], chan2_raw = sticks[1], chan3_raw = sticks[2], chan4_raw = sticks[3],
                chan5_raw = sticks[4], chan6_raw = sticks[5], chan7_raw = sticks[6], chan8_raw = sticks[7],
            }, v.sysid, v.compid);
        }

        void Pump()
        {
            Send();
            vm.Tick();
        }

        async Task Move(RcMapStep until, params (int Channel, ushort Pwm)[] moves)
        {
            foreach (var (c, v) in moves)
                sticks[c - 1] = v;
            await WaitFor(() => map.Mapper.Step == until, TimeSpan.FromSeconds(15), $"step {until} (at {map.Mapper.Step}: {map.DetectText})", Pump);
            TestContext.WriteLine($"{until}: {map.DetectText}");
        }

        try
        {
            await map.StartCommand.ExecuteAsync(null);
            Assert.IsTrue(map.IsActive);
            await WaitFor(() => session.Cs.ch3in is > 990 and < 1010 && session.Cs.ch1in is > 1490 and < 1510, TimeSpan.FromSeconds(10),
                "the overrides to show in RC_CHANNELS", Pump);
            map.NextCommand.Execute(null);
            Assert.AreEqual(RcMapStep.ThrottleUp, map.Mapper.Step, map.Status.Text);

            await Move(RcMapStep.ThrottleDown, (3, 2000));
            await Move(RcMapStep.YawRight, (3, 1000));
            await Move(RcMapStep.YawLeft, (4, 1000));
            await Move(RcMapStep.RollRight, (4, 2000));
            sticks[3] = 1500;
            await Move(RcMapStep.RollLeft, (2, 1000));
            await Move(RcMapStep.PitchForward, (2, 2000));
            sticks[1] = 1500;
            await Move(RcMapStep.PitchBack, (1, 1000));
            await Move(RcMapStep.Review, (1, 2000));
            sticks[0] = 1500;

            foreach (var row in map.Summary)
                TestContext.WriteLine($"{row.Function}: {row.Channel} {row.Direction} ({row.Change})");
            await map.WriteCommand.ExecuteAsync(null);
            Assert.IsFalse(map.IsActive, map.Status.Text);
            Assert.IsTrue(map.RebootRequired);
            Assert.AreEqual(RadioCalStage.Capturing, vm.Stage, "continues with the normal calibration");
            vm.CancelCalibrationCommand.Execute(null);

            Assert.AreEqual(2, (int)await ReadAsync("RCMAP_ROLL"));
            Assert.AreEqual(1, (int)await ReadAsync("RCMAP_PITCH"));
            Assert.AreEqual(3, (int)await ReadAsync("RCMAP_THROTTLE"));
            Assert.AreEqual(4, (int)await ReadAsync("RCMAP_YAW"));
            Assert.AreEqual(1, (int)await ReadAsync("RC2_REVERSED"));
            Assert.AreEqual(1, (int)await ReadAsync("RC4_REVERSED"));
            Assert.AreEqual(0, (int)await ReadAsync("RC1_REVERSED"));
            Assert.AreEqual(0, (int)await ReadAsync("RC3_REVERSED"));
        }
        finally
        {
            // release the overrides
            sticks = [0, 0, 0, 0, 0, 0, 0, 0];
            Send();
            vm.OnHidden();
            foreach (var (name, value) in original)
                await Restore(name, value);
        }

        foreach (var (name, value) in original)
            Assert.AreEqual(value, await ReadAsync(name), name);
    }

    private static string MotorInfoLetter(int order) => MotorInfo.TestLetterFor(order);

    private static string Channels(CurrentState cs) => $"[{cs.ch1out} {cs.ch2out} {cs.ch3out} {cs.ch4out}]";
}
