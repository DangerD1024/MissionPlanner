using MissionPlanner.App.Core.ViewModels.SetupScreens.Calibration;
using POS = MAVLink.ACCELCAL_VEHICLE_POS;

namespace MissionPlanner.App.Tests.SetupScreens;

[TestClass]
public sealed class AccelCalibrationProcessTests
{
    private static readonly POS[] SixPositions = [POS.LEVEL, POS.LEFT, POS.RIGHT, POS.NOSEDOWN, POS.NOSEUP, POS.BACK];

    [TestMethod]
    public async Task Start_SendsPreflightCalibrationWithParam5_1_AndListens()
    {
        var link = new FakeCalibrationLink();
        var cal = new AccelCalibrationProcess(link);

        Assert.IsTrue(await cal.StartAsync());

        Assert.HasCount(1, link.Commands);
        var (cmd, p) = link.Commands[0];
        Assert.AreEqual(MAVLink.MAV_CMD.PREFLIGHT_CALIBRATION, cmd);
        CollectionAssert.AreEqual(new float[] { 0, 0, 0, 0, 1, 0, 0 }, p);
        Assert.IsTrue(link.IsSubscribed(MAVLink.MAVLINK_MSG_ID.STATUSTEXT));
        Assert.IsTrue(link.IsSubscribed(MAVLink.MAVLINK_MSG_ID.COMMAND_LONG));
        Assert.AreEqual(AccelCalState.WaitingForVehicle, cal.State);
        Assert.IsTrue(cal.IsActive);
        Assert.IsFalse(cal.CanConfirm);
    }

    [TestMethod]
    public async Task ScriptedSixPositionRun_ConfirmsEachRequestedPosition_ThenSucceeds()
    {
        var link = new FakeCalibrationLink();
        var cal = new AccelCalibrationProcess(link);
        var texts = new List<string>();
        cal.StatusText += (_, t) => texts.Add(t);

        await cal.StartAsync();

        foreach (var pos in SixPositions)
        {
            link.PushStatusText($"Place vehicle {pos} and press any key.");
            link.PushPositionRequest(pos);

            Assert.AreEqual(AccelCalState.PositionRequested, cal.State, pos.ToString());
            Assert.AreEqual(pos, cal.Position);
            Assert.AreEqual("Please place vehicle " + pos, cal.Instruction);
            Assert.IsTrue(cal.CanConfirm);

            Assert.IsTrue(await cal.ConfirmPositionAsync());
            var sent = (MAVLink.mavlink_command_long_t)link.Sent[^1];
            Assert.AreEqual((ushort)MAVLink.MAV_CMD.ACCELCAL_VEHICLE_POS, sent.command);
            Assert.AreEqual((float)pos, sent.param1);
            Assert.AreEqual(pos, cal.Confirmed);
        }

        Assert.AreEqual(6, cal.ConfirmCount);
        Assert.HasCount(6, link.Sent);

        link.PushStatusText("Calibration successful");

        Assert.AreEqual(AccelCalState.Succeeded, cal.State);
        Assert.IsFalse(cal.IsActive);
        Assert.IsNull(cal.Position);
        Assert.AreEqual(0, link.SubscriptionCount, "unsubscribes when done");
        Assert.HasCount(7, texts);
        Assert.AreEqual("Place vehicle LEVEL and press any key.", texts[0]);

        // later messages are not ours any more
        link.PushPositionRequest(POS.LEVEL);
        Assert.AreEqual(AccelCalState.Succeeded, cal.State);
    }

    [TestMethod]
    public async Task CalibrationFailedText_EndsTheRun_CaseInsensitive()
    {
        var link = new FakeCalibrationLink();
        var cal = new AccelCalibrationProcess(link);
        await cal.StartAsync();
        link.PushPositionRequest(POS.LEVEL);

        link.PushStatusText("Calibration FAILED");

        Assert.AreEqual(AccelCalState.Failed, cal.State);
        Assert.AreEqual("Calibration FAILED", cal.Instruction);
        Assert.AreEqual(0, link.SubscriptionCount);
        Assert.IsFalse(await cal.ConfirmPositionAsync());
    }

    [TestMethod]
    public async Task SuccessAndFailedPositions_EndTheRun()
    {
        var link = new FakeCalibrationLink();
        var cal = new AccelCalibrationProcess(link);
        await cal.StartAsync();
        link.PushPositionRequest(POS.SUCCESS);
        Assert.AreEqual(AccelCalState.Succeeded, cal.State);

        await cal.StartAsync();
        link.PushPositionRequest(POS.FAILED);
        Assert.AreEqual(AccelCalState.Failed, cal.State);
        Assert.AreEqual(0, link.SubscriptionCount);
    }

    [TestMethod]
    public async Task Confirm_WithoutRequest_SendsNothing()
    {
        var link = new FakeCalibrationLink();
        var cal = new AccelCalibrationProcess(link);
        Assert.IsFalse(await cal.ConfirmPositionAsync(), "idle");

        await cal.StartAsync();
        Assert.IsFalse(await cal.ConfirmPositionAsync(), "nothing requested yet");
        Assert.IsEmpty(link.Sent);
    }

    [TestMethod]
    public async Task Instruction_FollowsOnlyPlacementAndCalibrationMessages()
    {
        var link = new FakeCalibrationLink();
        var cal = new AccelCalibrationProcess(link);
        var texts = new List<string>();
        cal.StatusText += (_, t) => texts.Add(t);
        await cal.StartAsync();

        link.PushStatusText("Place vehicle LEFT and press any key.");
        Assert.AreEqual("Place vehicle LEFT and press any key.", cal.Instruction);

        link.PushStatusText("EKF3 IMU0 is using GPS");
        Assert.AreEqual("Place vehicle LEFT and press any key.", cal.Instruction, "unrelated text is logged only");
        Assert.HasCount(2, texts);
        Assert.AreEqual(AccelCalState.WaitingForVehicle, cal.State);
    }

    [TestMethod]
    public async Task OtherCommandsAndOutOfRangePositions_AreIgnored()
    {
        var link = new FakeCalibrationLink();
        var cal = new AccelCalibrationProcess(link);
        await cal.StartAsync();

        link.Push(MAVLink.MAVLINK_MSG_ID.COMMAND_LONG, new MAVLink.mavlink_command_long_t
        {
            command = (ushort)MAVLink.MAV_CMD.DO_SET_MODE,
            param1 = 2,
        });
        cal.HandleCommandLong((ushort)MAVLink.MAV_CMD.ACCELCAL_VEHICLE_POS, 9);

        Assert.AreEqual(AccelCalState.WaitingForVehicle, cal.State);
        Assert.IsNull(cal.Position);
    }

    [TestMethod]
    public async Task RefusedStart_LeavesNothingSubscribed()
    {
        var link = new FakeCalibrationLink { Answer = _ => false };
        var cal = new AccelCalibrationProcess(link);

        Assert.IsFalse(await cal.StartAsync());

        Assert.AreEqual(AccelCalState.Idle, cal.State);
        Assert.AreEqual(0, link.SubscriptionCount);
    }

    [TestMethod]
    public async Task TimeoutOnStart_Rethrows_AndCleansUp()
    {
        var link = new FakeCalibrationLink { Answer = _ => throw new TimeoutException("no answer") };
        var cal = new AccelCalibrationProcess(link);

        await Assert.ThrowsExactlyAsync<TimeoutException>(cal.StartAsync);

        Assert.AreEqual(AccelCalState.Idle, cal.State);
        Assert.AreEqual(0, link.SubscriptionCount);
    }

    [TestMethod]
    public async Task Cancel_Unsubscribes_AndIgnoresLaterMessages()
    {
        var link = new FakeCalibrationLink();
        var cal = new AccelCalibrationProcess(link);
        await cal.StartAsync();
        link.PushPositionRequest(POS.LEFT);

        cal.Cancel();

        Assert.AreEqual(AccelCalState.Idle, cal.State);
        Assert.AreEqual(0, link.SubscriptionCount);
        cal.HandleCommandLong((ushort)MAVLink.MAV_CMD.ACCELCAL_VEHICLE_POS, (float)POS.RIGHT);
        cal.HandleStatusText("Calibration successful");
        Assert.AreEqual(AccelCalState.Idle, cal.State);
        Assert.IsNull(cal.Position);
    }

    [TestMethod]
    public async Task SecondStartWhileRunning_IsRefused()
    {
        var link = new FakeCalibrationLink();
        var cal = new AccelCalibrationProcess(link);
        await cal.StartAsync();

        Assert.IsFalse(await cal.StartAsync());
        Assert.HasCount(1, link.Commands);
        Assert.AreEqual(2, link.SubscriptionCount);
    }

    [TestMethod]
    public async Task RepeatedConfirm_CountsThePositionOnce()
    {
        var link = new FakeCalibrationLink();
        var cal = new AccelCalibrationProcess(link);
        await cal.StartAsync();
        link.PushPositionRequest(POS.LEVEL);

        await cal.ConfirmPositionAsync();
        await cal.ConfirmPositionAsync();

        Assert.HasCount(2, link.Sent, "a lost confirmation can be sent again");
        Assert.AreEqual(1, cal.ConfirmCount);
    }

    [TestMethod]
    public void StatusTextDecoder_StopsAtTheFirstZero()
    {
        var bytes = new byte[50];
        System.Text.Encoding.ASCII.GetBytes("Calibration successful", bytes);
        var parse = new MAVLink.MavlinkParse();
        var packet = parse.GenerateMAVLinkPacket20(MAVLink.MAVLINK_MSG_ID.STATUSTEXT,
            new MAVLink.mavlink_statustext_t { severity = 6, text = bytes }, false, 1, 1, 0);

        Assert.AreEqual("Calibration successful", StatusTextDecoder.Decode(new MAVLink.MAVLinkMessage(packet)));
    }
}
