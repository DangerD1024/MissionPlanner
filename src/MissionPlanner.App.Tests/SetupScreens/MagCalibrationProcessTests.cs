using MissionPlanner.App.Core.ViewModels.SetupScreens.Calibration;

namespace MissionPlanner.App.Tests.SetupScreens;

[TestClass]
public sealed class MagCalibrationProcessTests
{
    private static MAVLink.mavlink_mag_cal_progress_t Progress(byte id, byte pct, byte attempt = 1) => new()
    {
        compass_id = id,
        completion_pct = pct,
        attempt = attempt,
        cal_status = (byte)MAVLink.MAG_CAL_STATUS.MAG_CAL_RUNNING_STEP_ONE,
        completion_mask = new byte[10],
    };

    private static MAVLink.mavlink_mag_cal_report_t Report(byte id, MAVLink.MAG_CAL_STATUS status, bool autosaved, float ofsX = 12.5f) => new()
    {
        compass_id = id,
        cal_status = (byte)status,
        autosaved = (byte)(autosaved ? 1 : 0),
        ofs_x = ofsX,
        ofs_y = -3,
        ofs_z = 40,
        fitness = 4.2f,
    };

    [TestMethod]
    public async Task Start_SendsStartMagCal_AllCompassesRetryAutosave()
    {
        var link = new FakeCalibrationLink();
        var cal = new MagCalibrationProcess(link);

        Assert.IsTrue(await cal.StartAsync());

        var (cmd, p) = link.Commands.Single();
        Assert.AreEqual(MAVLink.MAV_CMD.DO_START_MAG_CAL, cmd);
        CollectionAssert.AreEqual(new float[] { 0, 1, 1, 0, 0, 0, 0 }, p);
        Assert.IsTrue(cal.IsRunning);
        Assert.IsTrue(link.IsSubscribed(MAVLink.MAVLINK_MSG_ID.MAG_CAL_PROGRESS));
        Assert.IsTrue(link.IsSubscribed(MAVLink.MAVLINK_MSG_ID.MAG_CAL_REPORT));
    }

    [TestMethod]
    public async Task RefusedStart_DoesNotListen()
    {
        var link = new FakeCalibrationLink { Answer = _ => false };
        var cal = new MagCalibrationProcess(link);

        Assert.IsFalse(await cal.StartAsync());
        Assert.IsFalse(cal.IsRunning);
        Assert.AreEqual(0, link.SubscriptionCount);
    }

    [TestMethod]
    public async Task ProgressThenAutosavedReports_Complete()
    {
        var link = new FakeCalibrationLink();
        var cal = new MagCalibrationProcess(link);
        var completed = 0;
        cal.Completed += (_, _) => completed++;
        await cal.StartAsync();

        link.Push(MAVLink.MAVLINK_MSG_ID.MAG_CAL_PROGRESS, Progress(0, 10));
        link.Push(MAVLink.MAVLINK_MSG_ID.MAG_CAL_PROGRESS, Progress(1, 5));
        link.Push(MAVLink.MAVLINK_MSG_ID.MAG_CAL_PROGRESS, Progress(0, 55));

        var snap = cal.Snapshot();
        Assert.HasCount(2, snap);
        Assert.AreEqual(55, snap[0].Percent);
        Assert.AreEqual(5, snap[1].Percent);
        Assert.IsFalse(snap[0].HasResult);

        link.Push(MAVLink.MAVLINK_MSG_ID.MAG_CAL_REPORT, Report(0, MAVLink.MAG_CAL_STATUS.MAG_CAL_SUCCESS, true));
        Assert.IsFalse(cal.IsComplete, "compass 1 still running");

        link.Push(MAVLink.MAVLINK_MSG_ID.MAG_CAL_REPORT, Report(1, MAVLink.MAG_CAL_STATUS.MAG_CAL_SUCCESS, true, ofsX: -20));

        Assert.IsTrue(cal.IsComplete);
        Assert.IsFalse(cal.IsRunning);
        Assert.AreEqual(1, completed);
        Assert.AreEqual(0, link.SubscriptionCount);

        snap = cal.Snapshot();
        Assert.AreEqual(100, snap[1].Percent);
        Assert.IsTrue(snap[1].Succeeded);
        Assert.AreEqual(-20f, snap[1].OffsetX);
        Assert.AreEqual(4.2f, snap[1].Fitness);
        Assert.AreEqual("Success", snap[1].ResultName);
    }

    [TestMethod]
    public async Task EmptyReportForCompass0_IsIgnored()
    {
        var link = new FakeCalibrationLink();
        var cal = new MagCalibrationProcess(link);
        await cal.StartAsync();
        link.Push(MAVLink.MAVLINK_MSG_ID.MAG_CAL_PROGRESS, Progress(0, 30));

        link.Push(MAVLink.MAVLINK_MSG_ID.MAG_CAL_REPORT, Report(0, MAVLink.MAG_CAL_STATUS.MAG_CAL_SUCCESS, true, ofsX: 0));

        Assert.IsFalse(cal.IsComplete);
        Assert.IsFalse(cal.Snapshot()[0].HasResult);
    }

    [TestMethod]
    public async Task FailedReport_IsClearedWhenTheRetryReportsProgress()
    {
        var link = new FakeCalibrationLink();
        var cal = new MagCalibrationProcess(link);
        await cal.StartAsync();
        link.Push(MAVLink.MAVLINK_MSG_ID.MAG_CAL_PROGRESS, Progress(0, 90));
        link.Push(MAVLink.MAVLINK_MSG_ID.MAG_CAL_REPORT, Report(0, MAVLink.MAG_CAL_STATUS.MAG_CAL_FAILED_RADIUS, false));

        var failed = cal.Snapshot()[0];
        Assert.IsTrue(failed.HasResult);
        Assert.IsFalse(failed.Succeeded);
        Assert.AreEqual("Failed radius", failed.ResultName);
        Assert.IsFalse(cal.IsComplete);

        link.Push(MAVLink.MAVLINK_MSG_ID.MAG_CAL_PROGRESS, Progress(0, 3, attempt: 2));

        var retry = cal.Snapshot()[0];
        Assert.IsFalse(retry.HasResult);
        Assert.AreEqual(3, retry.Percent);
        Assert.AreEqual(2, retry.Attempt);
    }

    [TestMethod]
    public async Task ResultsNotAutosaved_WaitForAccept()
    {
        var link = new FakeCalibrationLink();
        var cal = new MagCalibrationProcess(link);
        await cal.StartAsync();
        link.Push(MAVLink.MAVLINK_MSG_ID.MAG_CAL_PROGRESS, Progress(0, 90));
        link.Push(MAVLink.MAVLINK_MSG_ID.MAG_CAL_REPORT, Report(0, MAVLink.MAG_CAL_STATUS.MAG_CAL_SUCCESS, false));
        Assert.IsFalse(cal.IsComplete);

        Assert.IsTrue(await cal.AcceptAsync());

        var (cmd, p) = link.Commands[^1];
        Assert.AreEqual(MAVLink.MAV_CMD.DO_ACCEPT_MAG_CAL, cmd);
        CollectionAssert.AreEqual(new float[] { 0, 0, 1, 0, 0, 0, 0 }, p);
        Assert.IsFalse(cal.IsRunning);
        Assert.AreEqual(0, link.SubscriptionCount);
    }

    [TestMethod]
    public async Task Cancel_SendsCancelMagCal_AndStopsListening_EvenOnTimeout()
    {
        var link = new FakeCalibrationLink();
        var cal = new MagCalibrationProcess(link);
        await cal.StartAsync();

        link.Answer = _ => throw new TimeoutException();
        await Assert.ThrowsExactlyAsync<TimeoutException>(cal.CancelAsync);

        Assert.AreEqual(MAVLink.MAV_CMD.DO_CANCEL_MAG_CAL, link.Commands[^1].Command);
        Assert.IsFalse(cal.IsRunning);
        Assert.AreEqual(0, link.SubscriptionCount);

        // nothing more is recorded once stopped
        cal.HandleProgress(Progress(2, 50));
        Assert.IsEmpty(cal.Snapshot());
    }
}
