using MissionPlanner.App.Core.ViewModels.SetupScreens;
using MissionPlanner.App.Tests.ConfigScreens;

namespace MissionPlanner.App.Tests.SetupScreens.OptionalHardware;

[TestClass]
public sealed class CompassMotViewModelTests
{
    private static (CompassMotViewModel Vm, FakeCalibrationLink Link, FakeDialogs Dialogs) Create(bool confirm = true)
    {
        var (services, dialogs, _) = ParamTestSupport.CreateServices();
        dialogs.ConfirmAnswer = confirm;
        var link = new FakeCalibrationLink();
        var src = new FakeParamSource();
        src.Values["COMPASS_MOTCT"] = 0;
        src.Values["COMPASS_MOT_X"] = 0;
        var vm = new CompassMotViewModel(services, link, src);
        vm.OnShown();
        return (vm, link, dialogs);
    }

    [TestMethod]
    public async Task Start_SendsCompassmot_AndShowsTheStatusStream()
    {
        var (vm, link, dialogs) = Create();
        Assert.AreEqual("Start", vm.ButtonText);

        await vm.ToggleCommand.ExecuteAsync(null);

        Assert.HasCount(1, dialogs.Confirms, "the motors will run: asks first");
        var (cmd, p) = link.Commands.Single();
        Assert.AreEqual(MAVLink.MAV_CMD.PREFLIGHT_CALIBRATION, cmd);
        CollectionAssert.AreEqual(new float[] { 0, 0, 0, 0, 0, 1, 0 }, p, "param6 = 1 is compassmot");
        Assert.IsTrue(vm.IsRunning);
        Assert.AreEqual("Finish", vm.ButtonText);
        Assert.IsTrue(link.IsSubscribed(MAVLink.MAVLINK_MSG_ID.COMPASSMOT_STATUS));

        link.Push(MAVLink.MAVLINK_MSG_ID.COMPASSMOT_STATUS, new MAVLink.mavlink_compassmot_status_t(6.5f, 10, -5, 2, 500, 35));
        link.Push(MAVLink.MAVLINK_MSG_ID.COMPASSMOT_STATUS, new MAVLink.mavlink_compassmot_status_t(1.5f, 1, -1, 0, 200, 8));
        link.PushStatusText("Started compassmot");
        vm.Tick();

        CollectionAssert.AreEqual(new[] { 20.0, 50.0 }, vm.Points.Select(s => s.ThrottlePercent).ToArray());
        StringAssert.Contains(vm.StatusText, "Throttle: 20 %");
        StringAssert.Contains(vm.MessagesText, "Started compassmot");
        StringAssert.Contains(vm.RatingText, "Highest interference: 35 %");
        Assert.AreEqual(Core.ViewModels.SetupScreens.OptionalHardware.InterferenceRating.Acceptable, vm.Rating);
    }

    [TestMethod]
    public async Task Finish_SendsTheAckThatEndsIt()
    {
        var (vm, link, _) = Create();
        await vm.ToggleCommand.ExecuteAsync(null);
        await vm.ToggleCommand.ExecuteAsync(null);

        Assert.IsFalse(vm.IsRunning);
        Assert.HasCount(2, link.Sent, "sent twice, as Mission Planner");
        var ack = (MAVLink.mavlink_command_ack_t)link.Sent[0];
        Assert.AreEqual((ushort)MAVLink.MAV_CMD.PREFLIGHT_CALIBRATION, ack.command);
        Assert.AreEqual(0, ack.result);
    }

    [TestMethod]
    public async Task Declined_DoesNothing_AndLeavingStopsARunningCalibration()
    {
        var (vm, link, _) = Create(confirm: false);
        await vm.ToggleCommand.ExecuteAsync(null);
        Assert.IsEmpty(link.Commands);
        Assert.IsFalse(vm.IsRunning);

        var (vm2, link2, _) = Create();
        await vm2.ToggleCommand.ExecuteAsync(null);
        vm2.OnHidden();
        Assert.IsFalse(vm2.IsRunning);
        Assert.AreEqual(0, link2.SubscriptionCount);
        await Task.Delay(100);
        Assert.HasCount(2, link2.Sent);
    }

    [TestMethod]
    public async Task OldFirmware_WithoutAnAnswer_SaysSo()
    {
        var (vm, link, dialogs) = Create();
        link.Answer = _ => throw new TimeoutException("no answer");
        await vm.ToggleCommand.ExecuteAsync(null);
        Assert.IsFalse(vm.IsRunning);
        Assert.AreEqual(0, link.SubscriptionCount);
        StringAssert.Contains(dialogs.Messages.Single().Message, "Compassmot requires AC 3.2+");
    }
}
