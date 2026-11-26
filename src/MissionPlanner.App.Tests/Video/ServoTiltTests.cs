using System.Net;
using System.Net.Sockets;
using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.Synthetic;
using MissionPlanner.App.Core.ViewModels.ConfigScreens;
using MissionPlanner.App.Core.ViewModels.Video;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Tests.Video;

/// <summary>
/// The servo-driven 3D camera tilt: the glide between readings, and its request rate, which is the
/// RC telemetry rate of SETTINGS › App Settings.
/// </summary>
[TestClass]
public sealed class ServoTiltTests
{
    private const double Frame = 1 / 60.0;

    private static HudVideoViewModel Create() =>
        new(new VideoTestUi().CreateServices(), new MemoryVideoStore(), new FakeVideoStream());

    // ---- easing ---------------------------------------------------------------------------------

    [TestMethod]
    public void ANewReadingIsGlidedToMonotonicallyAndReachedWithoutOvershoot()
    {
        var e = new AngleEasing();
        Assert.IsTrue(double.IsNaN(e.At(0, 0.1)), "nothing before the first reading");
        e.SetTarget(15);
        Assert.AreEqual(15, e.At(0, 0.1), "the first reading is taken as it is");

        var t = 0.0;
        for (var i = 0; i < 60; i++)
            Assert.AreEqual(15, e.At(t += Frame, 0.1), "a steady reading stays put");

        // 10 Hz readings, the servo jumps from 15° up to 90° down
        e.SetTarget(-90);
        var start = t;
        var last = 15.0;
        var first = e.At(t += Frame, 0.1);
        Assert.IsTrue(first is < 15 and > -90, $"moves at once, but does not jump: {first}");
        double? oneInterval = null;
        double? reachedAfter = null;
        last = first;
        while (t - start < 1)
        {
            var v = e.At(t += Frame, 0.1);
            Assert.IsTrue(v <= last, $"monotonic: {v} after {last}");
            Assert.IsTrue(v >= -90, $"never past the reading: {v}");
            if (oneInterval == null && t - start >= 0.1 - 1e-9)
                oneInterval = v;
            if (reachedAfter == null && v == -90)
                reachedAfter = t - start;
            last = v;
        }

        Assert.IsTrue(oneInterval < -78, $"most of the way after one reading interval: {oneInterval}");
        Assert.IsNotNull(reachedAfter, "reaches the reading");
        Assert.IsTrue(reachedAfter < 0.4, $"reached after {reachedAfter:0.000} s");
        Assert.AreEqual(-90, e.At(t + 5, 0.1), "and stays there");
    }

    [TestMethod]
    public void AReadingThatTurnsBackIsNeitherOvershotNorRunAwayFrom()
    {
        var e = new AngleEasing();
        e.SetTarget(0);
        var t = 0.0;
        e.At(t, 0.1);
        e.SetTarget(-60);
        double v;
        do
            v = e.At(t += Frame, 0.1);
        while (v > -30);

        // moving down fast; the next reading is a little higher: straight back up to it, never below
        var turned = v;
        e.SetTarget(-25);
        var last = turned;
        for (var i = 0; i < 60; i++)
        {
            v = e.At(t += Frame, 0.1);
            Assert.IsTrue(v >= last, $"never away from the reading: {v} after {last}");
            Assert.IsTrue(v <= -25, $"never past it: {v}");
            last = v;
        }

        Assert.AreEqual(-25, v);

        // moving down fast towards -60, then a reading just ahead: stops on it rather than passing it
        e.SetTarget(-60);
        e.At(t += Frame, 0.1);
        v = e.At(t += Frame, 0.1);
        var ahead = v - 3;
        e.SetTarget(ahead);
        last = v;
        for (var i = 0; i < 60; i++)
        {
            v = e.At(t += Frame, 0.1);
            Assert.IsTrue(v <= last && v >= ahead, $"{v} after {last}, reading {ahead}");
            last = v;
        }

        Assert.AreEqual(ahead, v);
    }

    [TestMethod]
    public void SteadilyMovingReadingsAreTrailedWithoutTheLagGrowing()
    {
        // a tilt knob turned steadily: 10 Hz readings 1° apart, drawn at 60 fps for 6 s
        var e = new AngleEasing();
        var t = 0.0;
        var reading = 0.0;
        e.SetTarget(reading);
        e.At(t, 0.1);
        var nextReading = 0.1;
        var lagEarly = 0.0;
        var lagLate = 0.0;
        var last = 0.0;
        while (t < 6)
        {
            t += Frame;
            if (t >= nextReading)
            {
                reading += 1;
                e.SetTarget(reading);
                nextReading += 0.1;
            }

            var v = e.At(t, 0.1);
            Assert.IsTrue(v >= last && v <= reading, $"{v} after {last}, reading {reading}");
            last = v;
            var lag = reading - v;
            if (t is > 1 and < 2)
                lagEarly = Math.Max(lagEarly, lag);
            else if (t > 5)
                lagLate = Math.Max(lagLate, lag);
        }

        Assert.IsTrue(lagLate < 1.2, $"trails by about one reading step: {lagLate:0.00}°");
        Assert.AreEqual(lagEarly, lagLate, 0.05, "the lag does not build up");
    }

    // ---- the view-model -------------------------------------------------------------------------

    [TestMethod]
    public void TheServoTiltGlidesFromReadingToReadingAndSettingsChangesJump()
    {
        using var vm = Create();
        vm.FpvTilt = 20;
        Assert.AreEqual(20, vm.CameraTilt, "a fixed tilt is as it was");

        vm.TiltServo = 5;
        var cs = vm.Services.Session.Cs;
        cs.ch5out = 1000;
        vm.UpdateServoTilt();
        var t = 100.0;
        Assert.AreEqual(15, vm.ServoTiltAt(t), "the first reading as it is (-15° below = 15° up)");

        // SERVO_OUTPUT_RAW at 10 Hz: the glide is timed for it
        Assert.AreEqual(HudVideoViewModel.DefaultServoInterval, vm.ServoReadingInterval);
        for (var i = 0; i < 20; i++)
            vm.ServoPacketArrived(50 + i * 0.1);
        Assert.AreEqual(0.1, vm.ServoReadingInterval, 0.01);
        vm.ServoPacketArrived(60);
        Assert.AreEqual(0.1, vm.ServoReadingInterval, 0.01, "a gap in the stream is not a rate");

        cs.ch5out = 2000;
        vm.UpdateServoTilt();
        Assert.AreEqual(-90, vm.CameraTilt, "the newest reading");
        var last = 15.0;
        var start = t;
        while (t - start < 0.5)
        {
            var v = vm.ServoTiltAt(t += Frame);
            Assert.IsTrue(v <= last && v >= -90, $"{v} after {last}");
            last = v;
        }

        Assert.AreEqual(-90, last, "reached within a few reading intervals");
        Assert.AreEqual(-90, vm.ServoTiltAt(t += 1), "a steady reading stays");

        // reversed: 2000 µs is the From end, shown at once
        vm.TiltServoReverse = true;
        Assert.AreEqual(15, vm.CameraTilt);
        Assert.AreEqual(15, vm.ServoTiltAt(t += Frame));

        vm.TiltServo = 0;
        Assert.AreEqual(20, vm.CameraTilt, "back to the fixed tilt");
    }

    [TestMethod]
    public void WithoutServoOutputTheGlideWaitsForTheFirstReading()
    {
        using var vm = Create();
        vm.TiltServo = 3;
        Assert.IsTrue(double.IsNaN(vm.ServoTiltAt(1)), "no reading: the 3D view draws CameraTilt");
        Assert.AreEqual(15, vm.CameraTilt, "the 1000 µs end until the vehicle reports the output");
        vm.Services.Session.Cs.ch3out = 1500;
        vm.UpdateServoTilt();
        Assert.AreEqual(-37.5, vm.ServoTiltAt(1.1), 1e-9, "the first reading is taken as it is");
    }

    // ---- the request rate -----------------------------------------------------------------------

    private static (string? Key, int Backup) SaveRcRate() => (Settings.Instance["CMB_raterc"], CurrentState.ratercbackup);

    private static void RestoreRcRate((string? Key, int Backup) saved)
    {
        if (saved.Key == null)
            Settings.Instance.Remove("CMB_raterc");
        else
            Settings.Instance["CMB_raterc"] = saved.Key;
        CurrentState.ratercbackup = saved.Backup;
    }

    [TestMethod]
    public void TheRequestRateIsTheSettingsTabsRcRateBothWays()
    {
        var saved = SaveRcRate();
        try
        {
            CurrentState.ratercbackup = 2;
            using var vm = Create();
            var settings = new PlannerSettingsViewModel(vm.Services);
            var row = settings.Rates.Single(r => r.Group == PlannerSettings.StreamGroup.Rc);
            var dialog = new FpvCameraViewModel(vm);
            dialog.OnShown();
            CollectionAssert.AreEqual(row.Choices.ToList(), dialog.ServoRateChoices.ToList(), "the same choices");
            Assert.AreEqual(row.Selected, dialog.ServoRate, "the same value");
            Assert.AreEqual(2, dialog.ServoRate.Hz);

            var raised = 0;
            dialog.PropertyChanged += (_, e) => raised += e.PropertyName == nameof(FpvCameraViewModel.ServoRate) ? 1 : 0;

            // set in the 3D view settings: stored as the Settings tab stores it, and shown there
            dialog.ServoRate = dialog.ServoRateChoices.Single(c => c.Hz == 5);
            Assert.AreEqual("5", Settings.Instance["CMB_raterc"]);
            Assert.AreEqual(5, CurrentState.ratercbackup, "new connections start with it");
            Assert.AreSame(dialog.ServoRate, row.Selected, "the Settings row shows it");

            // set on the Settings tab: the dialog shows it
            raised = 0;
            row.Selected = row.Choices.Single(c => c.Hz == 10);
            Assert.AreEqual(10, dialog.ServoRate.Hz);
            Assert.IsTrue(raised > 0, "the dialog's dropdown is told");
            Assert.AreEqual("10", Settings.Instance["CMB_raterc"]);
            Assert.AreEqual(10, CurrentState.ratercbackup);

            // the other groups are left alone
            Assert.AreEqual(PlannerSettings.CurrentRate(vm.Services.Session, PlannerSettings.StreamGroup.Attitude),
                settings.Rates.Single(r => r.Group == PlannerSettings.StreamGroup.Attitude).Selected!.Hz);

            // Cancel puts back the rate the dialog opened with, everywhere
            dialog.CancelCommand.Execute(null);
            Assert.AreEqual(2, CurrentState.ratercbackup);
            Assert.AreEqual("2", Settings.Instance["CMB_raterc"]);
            Assert.AreEqual(2, row.Selected!.Hz);
            dialog.OnHidden();

            // a dialog that did not change the rate leaves it on Cancel
            var other = new FpvCameraViewModel(vm);
            row.Selected = row.Choices.Single(c => c.Hz == 4);
            other.CancelCommand.Execute(null);
            Assert.AreEqual(4, CurrentState.ratercbackup);
        }
        finally
        {
            RestoreRcRate(saved);
        }
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task TheRequestRateIsAppliedToTheConnectedVehiclesRcStream()
    {
        var saved = SaveRcRate();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync();
        using var vm = Create();
        var session = vm.Services.Session;
        var connecting = session.ConnectAsync(LinkSpec.Tcp("127.0.0.1", port), getParams: false);
        using var vehicleSide = await accept;
        listener.Stop();
        await using var vehicle = new FakeVehicle(vehicleSide.GetStream(), 0);
        await connecting;
        try
        {
            var dialog = new FpvCameraViewModel(vm);
            dialog.ServoRate = dialog.ServoRateChoices.Single(c => c.Hz == 7);
            Assert.AreEqual(7, session.Cs.raterc, "the vehicle's RC streams are kept at it");
            Assert.AreEqual(7, dialog.ServoRate.Hz);

            static bool IsRcRequest(MAVLink.MAVLinkMessage m) =>
                m.msgid == (uint)MAVLink.MAVLINK_MSG_ID.REQUEST_DATA_STREAM &&
                m.ToStructure<MAVLink.mavlink_request_data_stream_t>() is
                {
                    req_stream_id: (byte)MAVLink.MAV_DATA_STREAM.RC_CHANNELS, req_message_rate: 7, start_stop: 1,
                    target_system: FakeVehicle.SysId,
                };

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!vehicle.Received.Any(IsRcRequest) && DateTime.UtcNow < deadline)
                await Task.Delay(20);
            Assert.IsTrue(vehicle.Received.Any(IsRcRequest), "REQUEST_DATA_STREAM RC_CHANNELS at 7 Hz sent to the vehicle");
        }
        finally
        {
            await session.DisconnectAsync();
            RestoreRcRate(saved);
        }
    }
}
