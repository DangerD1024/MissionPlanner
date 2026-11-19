using System.Text;
using MissionPlanner.App.Core.ViewModels.FlightData;

namespace MissionPlanner.App.Tests.FlightData;

/// <summary>A command runner that records instead of talking to a vehicle.</summary>
internal sealed class RecordingCommands : IFlightCommands
{
    public bool CanCommand { get; set; } = true;
    public bool IsConnected { get; set; } = true;
    public List<string> Runs { get; } = new();
    public List<string> Reports { get; } = new();

    public Task RunAsync(string what, Func<MAVLinkInterface, uint, byte, CurrentState, bool> action, string okText, string failText)
    {
        Runs.Add(what);
        return Task.CompletedTask;
    }

    public void Report(string text, bool isError) => Reports.Add(text);
}

[TestClass]
public class ExtraTabCommandTests
{
    [TestMethod]
    public void ServoCommandsMatchMissionPlanner()
    {
        var set = ServoCommands.SetServo(7, 1900);
        Assert.AreEqual(MAVLink.MAV_CMD.DO_SET_SERVO, set.Command);
        Assert.AreEqual(7f, set.P1);
        Assert.AreEqual(1900f, set.P2);
        Assert.IsFalse(set.IsInt);

        var repeat = ServoCommands.RepeatServo(9, 1800, 3, 1.5);
        Assert.AreEqual(MAVLink.MAV_CMD.DO_REPEAT_SERVO, repeat.Command);
        Assert.AreEqual((9f, 1800f, 3f, 1.5f), (repeat.P1, repeat.P2, repeat.P3, repeat.P4));

        var relay = ServoCommands.SetRelay(0, true);
        Assert.AreEqual(MAVLink.MAV_CMD.DO_SET_RELAY, relay.Command);
        Assert.AreEqual((0f, 1f), (relay.P1, relay.P2));
        Assert.AreEqual(0f, ServoCommands.SetRelay(2, false).P2);

        var repeatRelay = ServoCommands.RepeatRelay(1, 4, 2);
        Assert.AreEqual(MAVLink.MAV_CMD.DO_REPEAT_RELAY, repeatRelay.Command);
        Assert.AreEqual((1f, 4f, 2f), (repeatRelay.P1, repeatRelay.P2, repeatRelay.P3));
    }

    [TestMethod]
    public void ServoRangeComesFromServoMinMax()
    {
        var p = new Dictionary<string, double> { ["SERVO6_MIN"] = 1000, ["SERVO6_MAX"] = 2000, ["SERVO7_MIN"] = 1900, ["SERVO7_MAX"] = 1100 };
        double? Param(string n) => p.TryGetValue(n, out var v) ? v : null;
        Assert.AreEqual((1000, 2000), ServoCommands.Range(6, Param));
        Assert.AreEqual((1100, 1900), ServoCommands.Range(5, Param), "no parameters: Mission Planner's defaults");
        Assert.AreEqual((1100, 1900), ServoCommands.Range(7, Param), "an inverted range is ignored");
    }

    [TestMethod]
    public void ToggleGoesToTheOtherEnd()
    {
        Assert.AreEqual(1900, ServoCommands.TogglePwm(1100, 1100, 1900));
        Assert.AreEqual(1900, ServoCommands.TogglePwm(1450, 1100, 1900), "nearer low");
        Assert.AreEqual(1100, ServoCommands.TogglePwm(1890, 1100, 1900));
        Assert.AreEqual(1900, ServoCommands.TogglePwm(0, 1100, 1900), "unknown output: go high");
        Assert.AreEqual("Low", ServoCommands.Position(1105, 1100, 1900));
        Assert.AreEqual("High", ServoCommands.Position(1900, 1100, 1900));
        Assert.AreEqual("", ServoCommands.Position(1500, 1100, 1900));
        Assert.AreEqual("", ServoCommands.Position(0, 1100, 1900));
    }

    [TestMethod]
    public void ServoTabHasOutputsFiveToFourteen()
    {
        var services = FakeUi.Services(out _);
        var commands = new RecordingCommands();
        var vm = new ServoRelayViewModel(services, commands);
        CollectionAssert.AreEqual(Enumerable.Range(5, 10).ToArray(), vm.Servos.Select(s => s.Channel).ToArray());
        Assert.HasCount(6, vm.Relays);
        Assert.AreEqual("Relay 1", vm.Relays[0].Name);

        services.Session.Cs.ch9out = 1900;
        vm.OnShown();
        var ch9 = vm.Servos.Single(s => s.Channel == 9);
        Assert.AreEqual(1900, ch9.Output);
        Assert.AreEqual("High", ch9.Position);
        Assert.AreEqual("1900", ch9.OutputText);

        ch9.SetLowCommand.Execute(null);
        Assert.HasCount(1, commands.Runs);
    }

    [TestMethod]
    public void AuxFunctionCommandCarriesFunctionAndSwitchPosition()
    {
        var cmd = AuxCommands.AuxFunction(41, AuxSwitchPosition.High);
        Assert.AreEqual(MAVLink.MAV_CMD.DO_AUX_FUNCTION, cmd.Command);
        Assert.AreEqual(41f, cmd.P1);
        Assert.AreEqual(2f, cmd.P2);
        Assert.AreEqual(0f, AuxCommands.AuxFunction(9, AuxSwitchPosition.Low).P2);
        Assert.AreEqual(1f, AuxCommands.AuxFunction(9, AuxSwitchPosition.Middle).P2);
    }

    [TestMethod]
    public void AuxTableDropsDoNothingAndDuplicates()
    {
        var clean = AuxCommands.Clean([new(9, "Camera Trigger"), new(0, "Do Nothing"), new(4, "RTL"), new(9, "dup"), new(5, " ")]);
        CollectionAssert.AreEqual(new[] { 4, 9 }, clean.Select(c => c.Key).ToArray());
        Assert.AreEqual("Camera Trigger", clean[1].Value);
    }

    [TestMethod]
    public void AuxListComesFromTheOfflineDocumentationAndKeepsPinsOnTop()
    {
        var services = FakeUi.Services(out _);
        var vm = new AuxFunctionViewModel(services, new RecordingCommands());
        vm.OnShown();
        var until = DateTime.UtcNow.AddSeconds(20);
        while (vm.Rows.Count == 0 && DateTime.UtcNow < until)
            Thread.Sleep(50);
        Assert.IsGreaterThan(5, vm.Rows.Count, "the RCn_OPTION table (or the built-in fallback)");
        Assert.IsTrue(vm.Rows.All(r => r.Id > 0), "Do Nothing is not offered");

        vm.Filter = "camera";
        Assert.IsTrue(vm.Rows.All(r => r.Name.Contains("camera", StringComparison.OrdinalIgnoreCase)));

        vm.Filter = "";
        var last = vm.Rows[^1];
        last.TogglePinCommand.Execute(null);
        Assert.AreSame(last, vm.Rows[0], "pinned first");
        last.TogglePinCommand.Execute(null);
        Assert.AreSame(last, vm.Rows[^1], "unpinned: back in number order");
        CollectionAssert.AreEqual(vm.Rows.Select(r => r.Id).Order().ToArray(), vm.Rows.Select(r => r.Id).ToArray());
        var some = vm.Rows[vm.Rows.Count / 2];
        Assert.Contains(some, AuxFunctionViewModel.Arrange(vm.Rows, some.IdText).ToList(), "a number matches the id");
    }

    [TestMethod]
    public void GimbalManagerAngles()
    {
        var cmd = GimbalCommands.PitchYaw(-45, 190, yawLock: true, gimbalId: 1);
        Assert.AreEqual(MAVLink.MAV_CMD.DO_GIMBAL_MANAGER_PITCHYAW, cmd.Command);
        Assert.AreEqual(-45f, cmd.P1);
        Assert.AreEqual(-170f, cmd.P2, 1e-4, "yaw wrapped to ±180");
        Assert.IsTrue(float.IsNaN(cmd.P3) && float.IsNaN(cmd.P4), "rates unused");
        Assert.AreEqual((float)MAVLink.GIMBAL_MANAGER_FLAGS.YAW_LOCK, cmd.P5);
        Assert.AreEqual(1f, cmd.P7);
        Assert.AreEqual(0f, GimbalCommands.PitchYaw(0, 0, yawLock: false).P5);

        var retract = GimbalCommands.ManagerFlags(MAVLink.GIMBAL_MANAGER_FLAGS.RETRACT);
        Assert.IsTrue(float.IsNaN(retract.P1) && float.IsNaN(retract.P2));
        Assert.AreEqual((float)MAVLink.GIMBAL_MANAGER_FLAGS.RETRACT, retract.P5);
    }

    [TestMethod]
    public void LegacyMountCommands()
    {
        var angles = GimbalCommands.MountAngles(-30, 0, 45);
        Assert.AreEqual(MAVLink.MAV_CMD.DO_MOUNT_CONTROL, angles.Command);
        Assert.AreEqual((-30f, 0f, 45f), (angles.P1, angles.P2, angles.P3));
        Assert.AreEqual((float)MAVLink.MAV_MOUNT_MODE.MAVLINK_TARGETING, angles.P7);

        var retract = GimbalCommands.MountMode(MAVLink.MAV_MOUNT_MODE.RETRACT);
        Assert.AreEqual(MAVLink.MAV_CMD.DO_MOUNT_CONTROL, retract.Command);
        Assert.AreEqual(0f, retract.P7);
        Assert.AreEqual((float)MAVLink.MAV_MOUNT_MODE.RC_TARGETING, GimbalCommands.MountMode(MAVLink.MAV_MOUNT_MODE.RC_TARGETING).P7);
        Assert.AreEqual((float)MAVLink.MAV_MOUNT_MODE.RC_TARGETING, GimbalCommands.Configure(MAVLink.MAV_MOUNT_MODE.RC_TARGETING).P1);
    }

    [TestMethod]
    public void PointAtUsesCommandIntAboveHome()
    {
        var legacy = GimbalCommands.PointAt(-35.3632621, 149.1652374, 20, gimbalManager: false);
        Assert.AreEqual(MAVLink.MAV_CMD.DO_SET_ROI, legacy.Command);
        Assert.IsTrue(legacy.IsInt);
        Assert.AreEqual(-353632621, legacy.X);
        Assert.AreEqual(1491652374, legacy.Y);
        Assert.AreEqual(20f, legacy.P7);
        Assert.AreEqual(MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT, legacy.Frame);

        var gm = GimbalCommands.PointAt(1, 2, 3, gimbalManager: true, gimbalId: 2);
        Assert.AreEqual(MAVLink.MAV_CMD.DO_SET_ROI_LOCATION, gm.Command);
        Assert.AreEqual(2f, gm.P1, "gimbal device id");
        Assert.AreEqual(MAVLink.MAV_CMD.DO_SET_ROI_NONE, GimbalCommands.RoiNone().Command);
    }

    [TestMethod]
    public void Wrap180()
    {
        Assert.AreEqual(0, GimbalCommands.Wrap180(360), 1e-9);
        Assert.AreEqual(-90, GimbalCommands.Wrap180(270), 1e-9);
        Assert.AreEqual(180, GimbalCommands.Wrap180(180), 1e-9);
        Assert.AreEqual(180, GimbalCommands.Wrap180(-180), 1e-9);
        Assert.AreEqual(10, GimbalCommands.Wrap180(-350), 1e-9);
    }

    [TestMethod]
    public void PayloadSlidersPickTheProtocol()
    {
        var services = FakeUi.Services(out _);
        var vm = new PayloadViewModel(services, new RecordingCommands());
        vm.OnShown();
        Assert.IsFalse(vm.UsesGimbalManager, "no GIMBAL_MANAGER_INFORMATION seen");
        vm.Pitch = -20;
        vm.Yaw = 30;
        var cmd = vm.AnglesCommand();
        Assert.AreEqual(MAVLink.MAV_CMD.DO_MOUNT_CONTROL, cmd.Command);
        Assert.AreEqual((-20f, 30f), (cmd.P1, cmd.P3));
    }

    [TestMethod]
    public void SquawkCodesAreOctal()
    {
        Assert.IsTrue(SquawkCode.IsValid(7700));
        Assert.IsTrue(SquawkCode.IsValid(1200));
        Assert.IsTrue(SquawkCode.IsValid(0));
        Assert.IsFalse(SquawkCode.IsValid(1280));
        Assert.IsFalse(SquawkCode.IsValid(10000));
        Assert.AreEqual(1210, SquawkCode.Normalize(1208), "stepping up past 7 carries");
        Assert.AreEqual(1177, SquawkCode.Normalize(1199), "stepping down from 1200");
        Assert.AreEqual(1200, SquawkCode.Normalize(1200));
        Assert.AreEqual(7700, SquawkCode.Normalize(7700));
    }

    [TestMethod]
    public void TransponderControlMessage()
    {
        Assert.AreEqual(16 | 64 | 128, TransponderControl.State(true, false, true, true, false), "ON");
        Assert.AreEqual(16 | 32 | 64 | 128 | 8, TransponderControl.State(true, true, true, true, true), "ALT + ident");
        Assert.AreEqual(0, TransponderControl.State(false, false, false, false, false), "STBY");

        var msg = TransponderControl.Build(7000, TransponderControl.State(true, true, true, true, false), "abc123");
        Assert.AreEqual(int.MaxValue, msg.baroAltMSL, "altitude left to the autopilot");
        Assert.AreEqual(7000, msg.squawk);
        Assert.HasCount(8, msg.flight_id);
        Assert.AreEqual("ABC123", Encoding.ASCII.GetString(msg.flight_id).TrimEnd('\0'));
        Assert.HasCount(8, TransponderControl.Build(1200, 0, "TOOLONGCALLSIGN").flight_id);

        Assert.AreEqual("<25m", TransponderControl.NicText(10));
        Assert.AreEqual("<3m", TransponderControl.NacpText(11));
        Assert.AreEqual("UNKNOWN", TransponderControl.NacpText(0));
        Assert.AreEqual("14", TransponderControl.NicText(14));
    }
}
