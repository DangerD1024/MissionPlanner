using System.Net;
using System.Net.Sockets;
using System.Text;
using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.Maps;
using MissionPlanner.App.Core.ViewModels;
using MissionPlanner.App.Core.ViewModels.FlightData;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Tests.FlightData;

/// <summary>The Actions tab additions: resume mission, abort landing, mount mode, log message, raw sensors.</summary>
[TestClass]
public class MoreActionsTests
{
    [ClassInitialize]
    public static void Init(TestContext _) =>
        MapTiles.Initialize(Path.Combine(TestEnvironment.DataRoot, "gmapcache") + Path.DirectorySeparatorChar);

    private static Locationwp Wp(MAVLink.MAV_CMD cmd, float alt = 50) => new() { id = (ushort)cmd, alt = alt, lat = -35, lng = 149 };

    [TestMethod]
    public void ResumeKeepsHomeTakeoffAndSettings_ThenEverythingFromTheResumePoint()
    {
        var mission = new List<Locationwp>
        {
            Wp(MAVLink.MAV_CMD.WAYPOINT, 0), // home
            Wp(MAVLink.MAV_CMD.TAKEOFF, 20),
            Wp(MAVLink.MAV_CMD.WAYPOINT), // 2: skipped
            Wp(MAVLink.MAV_CMD.DO_SET_CAM_TRIGG_DIST), // 3: kept, still applies
            Wp(MAVLink.MAV_CMD.LOITER_TIME), // 4: skipped
            Wp(MAVLink.MAV_CMD.WAYPOINT, 80), // 5: resume here
            Wp(MAVLink.MAV_CMD.DO_CHANGE_SPEED),
            Wp(MAVLink.MAV_CMD.RETURN_TO_LAUNCH),
        };

        var plan = ResumeMission.Plan(mission, 5);

        CollectionAssert.AreEqual(
            new[] { MAVLink.MAV_CMD.WAYPOINT, MAVLink.MAV_CMD.TAKEOFF, MAVLink.MAV_CMD.DO_SET_CAM_TRIGG_DIST, MAVLink.MAV_CMD.WAYPOINT,
                MAVLink.MAV_CMD.DO_CHANGE_SPEED, MAVLink.MAV_CMD.RETURN_TO_LAUNCH },
            plan.Select(w => (MAVLink.MAV_CMD)w.id).ToArray());
        Assert.AreEqual(80f, plan[3].alt, "the resume waypoint follows the kept items");
        Assert.HasCount(mission.Count, ResumeMission.Plan(mission, 1), "resuming at 1 keeps everything");
    }

    [TestMethod]
    public void TypedCoordinatesInTheUsualForms()
    {
        Assert.AreEqual((-35.3632, 149.1652, (double?)50), CoordinateInput.Parse("-35.3632;149.1652;50"));
        Assert.AreEqual((-35.3632, 149.1652, (double?)null), CoordinateInput.Parse("-35.3632;149.1652"));
        Assert.AreEqual((-35.3632, 149.1652, (double?)null), CoordinateInput.Parse("-35.3632, 149.1652"));
        Assert.AreEqual((-35.3632, 149.1652, (double?)12), CoordinateInput.Parse(" -35.3632  149.1652 12 "));
        Assert.AreEqual((-35.5, 149.25, (double?)null), CoordinateInput.Parse("-35,5;149,25"), "decimal commas with ;");
        Assert.IsNull(CoordinateInput.Parse("100;10"), "latitude out of range");
        Assert.IsNull(CoordinateInput.Parse("abc"));
        Assert.IsNull(CoordinateInput.Parse("1;2;3;4"));
    }

    [TestMethod]
    public void RawSensorWindowListsImusInputsAndOutputs()
    {
        var services = FakeUi.Services(out _);
        var vm = new RawSensorsViewModel(services);
        Assert.IsNotEmpty(vm.Imus);
        Assert.AreEqual("IMU 1", vm.Imus[0].Title);
        Assert.HasCount(9, vm.Imus[0].Values);
        Assert.HasCount(16, vm.RcIn);
        Assert.HasCount(16, vm.ServoOut);

        services.Session.Cs.ch3in = 1650;
        vm.OnShown();
        Assert.AreEqual("1650", vm.RcIn[2].Text);
        Assert.AreEqual(1650, vm.RcIn[2].Value);
    }

    [TestMethod]
    public void JoystickButtonOpensTheSetupJoystickPage()
    {
        var services = FakeUi.Services(out _);
        var shell = new ShellViewModel(services);
        var vm = shell.GetPage<FlightDataViewModel>(Screen.Data);

        vm.OpenJoystickCommand.Execute(null);

        Assert.IsTrue(shell.IsSetup, "SETUP opens");
        var setup = shell.GetPage<MenuScreenViewModel>(Screen.Setup);
        Assert.AreEqual("Joystick", setup.SelectedItem?.Title, "on its Joystick page (it needs no vehicle)");

        // a page that does not apply is not opened, and the user is told why
        var notes = new List<string>();
        services.Notified += (_, n) => notes.Add(n.Text);
        shell.ShowMenuPage(Screen.Setup, "Battery Monitor");
        Assert.AreEqual("Joystick", setup.SelectedItem?.Title);
        Assert.IsTrue(notes.Any(n => n.Contains("Battery Monitor")), string.Join("|", notes));
    }

    [TestMethod]
    [Timeout(90000, CooperativeCancellation = true)]
    public async Task AbortLandingMountModeAndMessageReachTheVehicle()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync();
        var services = FakeUi.Services(out var ui);
        var connecting = services.Session.ConnectAsync(LinkSpec.Tcp("127.0.0.1", port), getParams: false);
        using var vehicleSide = await accept;
        listener.Stop();
        await using var vehicle = new ExtrasFakeVehicle(vehicleSide.GetStream());
        await connecting;
        try
        {
            var vm = new FlightDataViewModel(services);
            vm.OnShown();
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!vm.IsConnected && DateTime.UtcNow < until)
            {
                vm.Tick();
                await Task.Delay(50);
            }

            await vm.AbortLandingCommand.ExecuteAsync(null);
            Assert.IsFalse(vm.ActionStatusIsError, vm.ActionStatus);
            Assert.IsTrue(vehicle.Commands.Any(c => c.command == (ushort)MAVLink.MAV_CMD.DO_GO_AROUND));

            vm.SelectedMountMode = FlightDataViewModel.MountModes.Single(m => m.Mode == MAVLink.MAV_MOUNT_MODE.NEUTRAL);
            await vm.SetMountModeCommand.ExecuteAsync(null);
            var until2 = DateTime.UtcNow.AddSeconds(5);
            while (!vehicle.Commands.Any(c => c.command == (ushort)MAVLink.MAV_CMD.DO_MOUNT_CONTROL) && DateTime.UtcNow < until2)
                await Task.Delay(20);
            Assert.AreEqual((float)MAVLink.MAV_MOUNT_MODE.NEUTRAL,
                vehicle.Commands.Last(c => c.command == (ushort)MAVLink.MAV_CMD.DO_MOUNT_CONTROL).param7);

            await vm.TriggerCameraAsync();
            Assert.AreEqual(1f, vehicle.Commands.Last(c => c.command == (ushort)MAVLink.MAV_CMD.DO_DIGICAM_CONTROL).param5);

            ui.PromptAnswer = "7";
            await vm.JumpToTagAsync();
            Assert.AreEqual(7f, vehicle.Commands.Last(c => c.command == (ushort)MAVLink.MAV_CMD.DO_JUMP_TAG).param1);

            ui.PromptAnswer = "15";
            await vm.TakeoffAsync();
            Assert.AreEqual(15f, vehicle.Commands.Last(c => c.command == (ushort)MAVLink.MAV_CMD.TAKEOFF).param7, 0.01f);

            ui.PromptAnswer = "-35.36;149.16;600";
            await vm.PointCameraCoordsAsync();
            var roiUntil = DateTime.UtcNow.AddSeconds(5);
            MAVLink.mavlink_command_int_t? roi = null;
            while (roi == null && DateTime.UtcNow < roiUntil)
            {
                roi = vehicle.Received.Where(m => m.msgid == (uint)MAVLink.MAVLINK_MSG_ID.COMMAND_INT)
                    .Select(m => (MAVLink.mavlink_command_int_t?)m.data)
                    .LastOrDefault(c => c!.Value.command == (ushort)MAVLink.MAV_CMD.DO_SET_ROI);
                await Task.Delay(20);
            }

            Assert.IsNotNull(roi);
            Assert.AreEqual(-353600000, roi.Value.x);
            Assert.AreEqual(600f, roi.Value.z, 0.01f);

            ui.PromptAnswer = "bird strike at WP 3";
            await vm.SendLogMessageCommand.ExecuteAsync(null);
            var until3 = DateTime.UtcNow.AddSeconds(5);
            string Texts() => string.Join("|", vehicle.Received.Where(m => m.msgid == (uint)MAVLink.MAVLINK_MSG_ID.STATUSTEXT)
                .Select(m => Encoding.ASCII.GetString(((MAVLink.mavlink_statustext_t)m.data).text).TrimEnd('\0')));
            while (!Texts().Contains("bird strike") && DateTime.UtcNow < until3)
                await Task.Delay(20);
            StringAssert.Contains(Texts(), "bird strike at WP 3");
        }
        finally
        {
            await services.Session.DisconnectAsync();
        }
    }
}
