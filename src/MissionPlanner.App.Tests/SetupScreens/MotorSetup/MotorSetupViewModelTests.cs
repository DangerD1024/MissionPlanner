using MissionPlanner.App.Core.ViewModels.SetupScreens.Calibration;
using MissionPlanner.App.Core.ViewModels.SetupScreens.MotorSetup;
using MissionPlanner.App.Tests.Logs;

namespace MissionPlanner.App.Tests.SetupScreens.MotorSetup;

/// <summary>A thread-safe scripted vehicle for DO_MOTOR_TEST: records every command and answers from <see cref="Answer"/>.</summary>
internal sealed class MotorTestLink : ICalibrationLink
{
    private readonly List<float[]> _tests = new();

    public Func<float[], bool> Answer { get; set; } = _ => true;

    /// <summary>DO_MOTOR_TEST params sent so far.</summary>
    public IReadOnlyList<float[]> Tests
    {
        get
        {
            lock (_tests)
                return _tests.ToList();
        }
    }

    /// <summary>The motor tests that start or keep a motor spinning (throttle above 0).</summary>
    public IReadOnlyList<int> SpinOrders => Tests.Where(t => t[2] > 0).Select(t => (int)t[0]).ToList();

    public void Clear()
    {
        lock (_tests)
            _tests.Clear();
    }

    public Task<bool> CommandAsync(MAVLink.MAV_CMD command, float p1 = 0, float p2 = 0, float p3 = 0, float p4 = 0, float p5 = 0, float p6 = 0, float p7 = 0)
    {
        float[] p = [p1, p2, p3, p4, p5, p6, p7];
        if (command == MAVLink.MAV_CMD.DO_MOTOR_TEST)
        {
            lock (_tests)
                _tests.Add(p);
        }

        return Task.FromResult(Answer(p));
    }

    public IDisposable Subscribe(MAVLink.MAVLINK_MSG_ID id, Action<MAVLink.MAVLinkMessage> handler) => new Nothing();

    public Task SendAsync(object message) => Task.CompletedTask;

    private sealed class Nothing : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

[TestClass]
public sealed class MotorSpinnerTests
{
    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task KeepsTheMotorSpinningWithShortTimeoutsUntilStopped()
    {
        var link = new MotorTestLink();
        await using var spinner = new MotorSpinner(link, TimeSpan.FromMilliseconds(20), TimeSpan.Zero);

        Assert.AreEqual(MotorSpinResult.Spinning, await spinner.SpinAsync(3, 6));
        Assert.AreEqual(3, spinner.TestOrder);
        while (link.Tests.Count < 4)
            await Task.Delay(10);

        foreach (var t in link.Tests)
        {
            Assert.AreEqual(3f, t[0], "test order");
            Assert.AreEqual((float)MAVLink.MOTOR_TEST_THROTTLE_TYPE.MOTOR_TEST_THROTTLE_PERCENT, t[1]);
            Assert.AreEqual(6f, t[2], "throttle %");
            Assert.AreEqual(MotorSpinner.TestTimeoutSeconds, t[3], "short timeout");
            Assert.AreEqual(0f, t[4], "this motor only");
        }

        await spinner.StopAsync();
        Assert.IsFalse(spinner.IsSpinning);
        var last = link.Tests[^1];
        Assert.AreEqual(0f, last[2], "stop: throttle 0");
        Assert.AreEqual(0f, last[3], "stop: timeout 0");
        var count = link.Tests.Count;
        await Task.Delay(100);
        Assert.HasCount(count, link.Tests, "nothing is sent after the stop");
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task SwitchingMotorsStopsThePreviousOneFirst()
    {
        var link = new MotorTestLink();
        await using var spinner = new MotorSpinner(link, TimeSpan.FromSeconds(10), TimeSpan.Zero);
        await spinner.SpinAsync(1, 5);
        await spinner.SpinAsync(2, 5);
        var t = link.Tests;
        Assert.HasCount(3, t);
        Assert.AreEqual((1f, 5f), (t[0][0], t[0][2]));
        Assert.AreEqual((1f, 0f, 0f), (t[1][0], t[1][2], t[1][3]), "the first motor is stopped");
        Assert.AreEqual((2f, 5f), (t[2][0], t[2][2]));
    }

    [TestMethod]
    public async Task ThrottleIsCappedAndRefusalsReported()
    {
        var link = new MotorTestLink();
        await using var spinner = new MotorSpinner(link, TimeSpan.FromSeconds(10), TimeSpan.Zero);
        await spinner.SpinAsync(1, 80);
        Assert.AreEqual(MotorSpinner.MaxThrottle, link.Tests[0][2]);
        await spinner.StopAsync();

        link.Answer = _ => false;
        Assert.AreEqual(MotorSpinResult.Denied, await spinner.SpinAsync(2, 5));
        Assert.IsFalse(spinner.IsSpinning);

        link.Answer = _ => throw new TimeoutException();
        Assert.AreEqual(MotorSpinResult.Error, await spinner.SpinAsync(2, 5));
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task ALostKeepAliveEndsTheSpinAndSaysSo()
    {
        var link = new MotorTestLink();
        await using var spinner = new MotorSpinner(link, TimeSpan.FromMilliseconds(20), TimeSpan.Zero);
        var failed = new TaskCompletionSource<string>();
        spinner.Failed += (_, m) => failed.TrySetResult(m);
        await spinner.SpinAsync(1, 5);
        link.Answer = _ => false;
        StringAssert.Contains(await failed.Task, "stopped");
        Assert.IsFalse(spinner.IsSpinning);
    }
}

[TestClass]
public sealed class MotorSetupViewModelTests
{
    private sealed class Rig
    {
        public FakeUiServices Ui { get; } = new();

        public FakeParamSource Params { get; } = new();

        public MotorTestLink Link { get; } = new();

        public bool Armed { get; set; }

        public bool Rebooted { get; set; }

        public MotorSetupViewModel Vm { get; }

        public Rig(int frameType = 1)
        {
            var p = Params.Values;
            p["FRAME_CLASS"] = 1;
            p["FRAME_TYPE"] = frameType;
            for (var n = 1; n <= 8; n++)
                p[$"SERVO{n}_FUNCTION"] = n <= 4 ? 32 + n : 0;
            p["MOT_PWM_TYPE"] = 6;
            p["SERVO_DSHOT_ESC"] = 0;
            p["SERVO_BLH_RVMASK"] = 0;
            p["SERVO_BLH_3DMASK"] = 0;
            p["SERVO_BLH_BDMASK"] = 0;
            Params.Metas["FRAME_TYPE"] = FakeParamSource.MetaWith("FRAME_TYPE", reboot: true);
            Vm = new MotorSetupViewModel(Ui.CreateServices(), Params, Params.SetAsync, () => Rebooted = true, Link, Layouts.Catalog,
                () => true, TimeSpan.FromSeconds(30), () => true, () => Armed);
            Vm.Build();
        }

        public double Param(string name) => Params.Values[name];
    }

    [TestMethod]
    public void ShowsTheFrameWithOutputsAndFrameChoices()
    {
        var r = new Rig();
        var vm = r.Vm;
        Assert.IsTrue(vm.IsAvailable);
        Assert.AreEqual(1, vm.Layout!.Type);
        StringAssert.Contains(vm.FrameText, "Quad X");
        Assert.AreEqual("OUT 3", vm.Diagram.MarkOf(3)!.Caption);
        Assert.IsTrue(vm.HasFrameTypes);
        CollectionAssert.IsSubsetOf(new[] { 0, 1, 2, 3, 12, 13, 14, 18 }, vm.FrameTypes.Select(t => t.Value).ToArray());
        Assert.AreEqual(1, vm.SelectedFrameType!.Value);
        Assert.IsTrue(vm.CanRunWizard);
        Assert.IsTrue(vm.CanReverseFrame);
        Assert.IsFalse(vm.FrameReversed);
        Assert.AreEqual(DirectionSupport.EscTypeNone, vm.Direction!.Support);
    }

    [TestMethod]
    public void RoversAndHelicoptersGetNoCard()
    {
        var r = new Rig();
        r.Params.Values["FRAME_CLASS"] = 6;
        r.Vm.Build();
        Assert.IsFalse(r.Vm.IsAvailable, "heli: no motor layout");
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task OrderWizardSpinsEachOutputAndWritesTheMapping()
    {
        var r = new Rig();
        var vm = r.Vm;
        vm.StartOrderCommand.Execute(null);
        Assert.IsTrue(vm.ShowSafetyGate);

        await vm.BeginCommand.ExecuteAsync(null);
        Assert.IsEmpty(r.Link.Tests, "nothing spins before the propellers box is ticked");
        StringAssert.Contains(vm.Status.Text, "propellers");

        vm.PropsRemoved = true;
        r.Armed = true;
        await vm.BeginCommand.ExecuteAsync(null);
        Assert.IsEmpty(r.Link.Tests);
        StringAssert.Contains(vm.Status.Text, "Disarm");

        r.Armed = false;
        await vm.BeginCommand.ExecuteAsync(null);
        Assert.IsTrue(vm.IsIdentifying);
        Assert.IsTrue(vm.Diagram.Clickable);

        // Quad X: output 1 (Motor1) is test A, output 2 (Motor2) test C, output 3 (Motor3) test D, output 4 (Motor4) test B
        int[] expected = [1, 3, 4, 2];
        // the vehicle is wired with outputs 1 and 2 swapped
        int[] clicks = [2, 1, 3, 4];
        for (var i = 0; i < 4; i++)
        {
            Assert.AreEqual(expected[i], r.Link.SpinOrders[^1], $"output {i + 1} spins test {expected[i]}");
            Assert.AreEqual(6f, r.Link.Tests.Last(t => t[2] > 0)[2], "default throttle");
            await vm.MotorClickedCommand.ExecuteAsync(clicks[i]);
        }

        Assert.IsTrue(vm.IsReviewing);
        Assert.IsFalse(vm.Spinner.IsSpinning, "stopped at the end");
        Assert.AreEqual(0f, r.Link.Tests[^1][2]);
        Assert.IsTrue(vm.CanWriteMapping);
        Assert.HasCount(4, vm.ReviewRows);
        Assert.AreEqual(2, vm.ReviewRows.Count(x => x.Changed));

        await vm.WriteMappingCommand.ExecuteAsync(null);
        Assert.AreEqual(34, r.Param("SERVO1_FUNCTION"));
        Assert.AreEqual(33, r.Param("SERVO2_FUNCTION"));
        Assert.AreEqual(35, r.Param("SERVO3_FUNCTION"));
        Assert.IsTrue(r.Rebooted, "reboot banner");
        Assert.IsTrue(vm.IsOrderDone);
        Assert.IsTrue(r.Ui.Confirms.Any(c => c.Contains("SERVO1_FUNCTION: Motor1 (33) → Motor2 (34)", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ATakenPositionIsRefusedAndTheStopButtonStopsEverything()
    {
        var r = new Rig();
        var vm = r.Vm;
        vm.StartOrderCommand.Execute(null);
        vm.PropsRemoved = true;
        await vm.BeginCommand.ExecuteAsync(null);
        await vm.MotorClickedCommand.ExecuteAsync(3);
        await vm.MotorClickedCommand.ExecuteAsync(3);
        StringAssert.Contains(vm.Status.Text, "already");
        Assert.AreEqual(2, vm.Wizard!.Current!.Output);

        await vm.StopCommand.ExecuteAsync(null);
        Assert.IsFalse(vm.IsSpinning);
        Assert.AreEqual((0f, 0f), (r.Link.Tests[^1][2], r.Link.Tests[^1][3]));

        await vm.CloseModeCommand.ExecuteAsync(null);
        Assert.IsTrue(vm.IsOverview);
        Assert.IsFalse(vm.Diagram.Clickable);
    }

    [TestMethod]
    public async Task DirectionModeSpinsAClickedMotorAndWritesTheReverseMask()
    {
        var r = new Rig();
        var vm = r.Vm;
        vm.StartDirectionCommand.Execute(null);
        vm.PropsRemoved = true;
        await vm.BeginCommand.ExecuteAsync(null);
        Assert.IsTrue(vm.IsDirectionRunning);

        await vm.MotorClickedCommand.ExecuteAsync(2);
        Assert.AreEqual(3, r.Link.SpinOrders[^1], "motor 2 of a Quad X is test C");
        Assert.AreEqual(MotorMarkKind.Spinning, vm.Diagram.MarkOf(2)!.Kind);
        StringAssert.Contains(vm.SpinText, "counter-clockwise");

        // DShot without an ESC type: reversing is not possible yet
        var row = vm.DirectionRows.Single(x => x.Motor.Number == 2);
        Assert.IsFalse(row.CanReverse);
        Assert.IsTrue(vm.OfferEscType);
        vm.SelectedEscType = vm.EscTypeChoices.First(e => e.Value == 1);
        await vm.SetEscTypeCommand.ExecuteAsync(null);
        Assert.AreEqual(1, r.Param("SERVO_DSHOT_ESC"));
        Assert.IsTrue(vm.DirectionSupported);
        Assert.IsTrue(row.CanReverse);

        r.Params.Values["SERVO2_FUNCTION"] = 0;
        r.Params.Values["SERVO6_FUNCTION"] = 34; // motor 2 moved to output 6
        vm.Refresh();
        row = vm.DirectionRows.Single(x => x.Motor.Number == 2);
        row.IsReversed = true;
        for (var i = 0; i < 50 && r.Param("SERVO_BLH_RVMASK") == 0; i++)
            await Task.Delay(10);
        Assert.AreEqual(32, r.Param("SERVO_BLH_RVMASK"), "bit of output 6");
        Assert.IsTrue(r.Rebooted);
        Assert.IsTrue(vm.Diagram.MarkOf(2)!.Reversed);

        await vm.MotorClickedCommand.ExecuteAsync(2);
        Assert.IsFalse(vm.IsSpinning, "a second click stops it");
    }

    [TestMethod]
    public async Task ReversedSwitchWritesTheMirrorFrameType()
    {
        var r = new Rig(frameType: 1);
        var vm = r.Vm;
        vm.FrameReversed = true;
        for (var i = 0; i < 50 && r.Param("FRAME_TYPE") == 1; i++)
            await Task.Delay(10);
        Assert.AreEqual(3, r.Param("FRAME_TYPE"), "X → H");
        Assert.IsTrue(r.Rebooted);
        Assert.IsTrue(r.Ui.Confirms.Any(c => c.Contains("propeller", StringComparison.Ordinal)));
        Assert.IsTrue(vm.FrameReversed);
        Assert.AreEqual(3, vm.Layout!.Type);

        r.Ui.ConfirmAnswer = false;
        vm.FrameReversed = false;
        await Task.Delay(50);
        Assert.AreEqual(3, r.Param("FRAME_TYPE"), "cancelled");
        Assert.IsTrue(vm.FrameReversed, "the switch goes back");
    }

    [TestMethod]
    public void FramesWithoutAMirrorExplainWhy()
    {
        var r = new Rig();
        r.Params.Values["FRAME_CLASS"] = 2;
        for (var n = 5; n <= 6; n++)
            r.Params.Values[$"SERVO{n}_FUNCTION"] = 32 + n;
        r.Vm.Build();
        Assert.IsTrue(r.Vm.IsAvailable);
        Assert.IsFalse(r.Vm.CanReverseFrame);
        StringAssert.Contains(r.Vm.ReverseFrameNote, "scripted matrix");
    }

    [TestMethod]
    public async Task FrameTypePickerPreviewsThenWrites()
    {
        var r = new Rig();
        var vm = r.Vm;
        vm.SelectedFrameType = vm.FrameTypes.Single(t => t.Value == 0);
        Assert.IsTrue(vm.IsPreviewing);
        Assert.AreEqual(0, vm.Diagram.Layout!.Type, "the drawing shows Plus");
        Assert.AreEqual(1, r.Param("FRAME_TYPE"), "nothing written yet");

        vm.CancelPreviewCommand.Execute(null);
        Assert.IsFalse(vm.IsPreviewing);
        Assert.AreEqual(1, vm.SelectedFrameType!.Value);
        Assert.AreEqual(1, vm.Diagram.Layout!.Type);

        vm.SelectedFrameType = vm.FrameTypes.Single(t => t.Value == 0);
        await vm.ApplyFrameTypeCommand.ExecuteAsync(null);
        Assert.AreEqual(0, r.Param("FRAME_TYPE"));
        Assert.IsTrue(r.Rebooted);
        Assert.AreEqual(0, vm.Layout!.Type);
        Assert.IsFalse(vm.IsPreviewing);
    }
}
