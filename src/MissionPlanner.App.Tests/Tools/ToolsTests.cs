using System.Net;
using System.Net.Sockets;
using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.ViewModels;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Calibration;
using MissionPlanner.App.Core.ViewModels.Tools;
using MissionPlanner.App.Tests.FlightData;

namespace MissionPlanner.App.Tests.Tools;

/// <summary>The TOOLS screen (Mission Planner's Ctrl+F): its logic, and its commands against a scripted vehicle.</summary>
[TestClass]
public class ToolsTests
{
    [TestMethod]
    public void QnhAcceptsPascalsOrHectopascals_AndTheNudgeIs11PaPerMetre()
    {
        Assert.AreEqual(101325, GroundPressure.ParseQnh("101325"));
        Assert.AreEqual(101325, GroundPressure.ParseQnh("1013.25")!.Value, 1e-6);
        Assert.AreEqual(101325, GroundPressure.ParseQnh("1013,25")!.Value, 1e-6);
        Assert.IsNull(GroundPressure.ParseQnh("12"));
        Assert.IsNull(GroundPressure.ParseQnh("abc"));
        Assert.AreEqual(100111, GroundPressure.ForOffset(100000, 10), 1e-9);
        Assert.AreEqual("BARO1_GND_PRESS", GroundPressure.ParamName(n => n == "BARO1_GND_PRESS"));
        Assert.AreEqual("GND_ABS_PRESS", GroundPressure.ParamName(_ => true));
        Assert.IsNull(GroundPressure.ParamName(_ => false));
    }

    [TestMethod]
    public void MessageIntervalIsMicroseconds_ZeroAndMinusOneKept()
    {
        Assert.AreEqual(100_000f, MessageIntervals.IntervalFor(10));
        Assert.AreEqual(0f, MessageIntervals.IntervalFor(0));
        Assert.AreEqual(-1f, MessageIntervals.IntervalFor(-1));
        var cmd = MessageIntervals.Command(MAVLink.MAVLINK_MSG_ID.ATTITUDE, 50);
        Assert.AreEqual(MAVLink.MAV_CMD.SET_MESSAGE_INTERVAL, cmd.Command);
        Assert.AreEqual((float)(int)MAVLink.MAVLINK_MSG_ID.ATTITUDE, cmd.P1);
        Assert.AreEqual(20_000f, cmd.P2);
        CollectionAssert.Contains(MessageIntervals.Names.ToList(), "GLOBAL_POSITION_INT");
    }

    [TestMethod]
    public void PacketBytesInEveryNotation_DecodeToTheSameMessage()
    {
        var parse = new MAVLink.MavlinkParse();
        var bytes = parse.GenerateMAVLinkPacket20(MAVLink.MAVLINK_MSG_ID.HEARTBEAT, new MAVLink.mavlink_heartbeat_t(
            custom_mode: 4, type: (byte)MAVLink.MAV_TYPE.QUADROTOR, autopilot: (byte)MAVLink.MAV_AUTOPILOT.ARDUPILOTMEGA,
            base_mode: 0, system_status: 3, mavlink_version: 3), false, 7, 1, 5);

        var spaced = string.Join(' ', bytes.Select(b => b.ToString("x2")));
        var prefixed = string.Join(',', bytes.Select(b => "0x" + b.ToString("X2")));
        var run = string.Concat(bytes.Select(b => b.ToString("x2")));
        var dec = string.Join(' ', bytes.Select(b => b.ToString()));
        foreach (var text in new[] { spaced, prefixed, run, dec })
        {
            CollectionAssert.AreEqual(bytes, PacketDecoder.ParseBytes(text), text);
            var (ok, output) = PacketDecoder.Decode(text);
            Assert.IsTrue(ok, output);
            StringAssert.Contains(output, "HEARTBEAT");
            StringAssert.Contains(output, "sysid 7");
            StringAssert.Contains(output, "\"custom_mode\": 4");
        }

        Assert.IsFalse(PacketDecoder.Decode("01 02 03").Ok, "not a MAVLink start byte");
        Assert.IsFalse(PacketDecoder.Decode("zz").Ok);
        Assert.IsFalse(PacketDecoder.Decode(spaced[..20]).Ok, "cut short");
    }

    [TestMethod]
    public void DeviceIdsDecodeIntoBusAddressAndType()
    {
        var id = DeviceId.Make(MissionPlanner.Utilities.Device.BusType.BUS_TYPE_I2C, 1, 0x1e, 5);
        var text = DeviceIdDecoder.Decode($"COMPASS_DEV_ID {id.Raw}\n{id.Raw}");
        var lines = text.Split('\n');
        Assert.HasCount(2, lines);
        StringAssert.StartsWith(lines[0], $"COMPASS_DEV_ID {id.Raw} = bus type I2C, bus 1, address 30 (0x1E), device type");
        StringAssert.Contains(lines[0], id.DevTypeName("COMPASS_DEV_ID"));
        StringAssert.StartsWith(lines[1], $"{id.Raw} = bus type I2C");
    }

    [TestMethod]
    public void ParamRestoreWritesEnablesFirst_SkipsMatches_ClearsDeviceIds()
    {
        var file = new Dictionary<string, double>
        {
            ["ATC_RAT_RLL_P"] = 0.15, ["COMPASS_DEV_ID2"] = 97539, ["THR_IDLE"] = 10, ["RNGFND1_TYPE"] = 10,
            ["GPS_AUTO_CONFIG"] = 1, ["SCR_ENABLE"] = 1, ["AHRS_EKF_TYPE"] = 3,
        };
        var current = new Dictionary<string, double> { ["ATC_RAT_RLL_P"] = 0.15, ["AHRS_EKF_TYPE"] = 2 };
        var (steps, already) = ParamRestorePlan.Build(file, n => current.TryGetValue(n, out var v) ? v : null);

        Assert.AreEqual(1, already);
        Assert.AreEqual("SCR_ENABLE", steps[0].Name, "enables first");
        Assert.IsFalse(steps.Any(s => s.Name == "ATC_RAT_RLL_P"), "already matches");
        Assert.IsTrue(steps.Single(s => s.Name == "COMPASS_DEV_ID2").ZeroFirst);
        Assert.IsFalse(steps.Single(s => s.Name == "THR_IDLE").ZeroFirst, "not a device ID");
        Assert.IsTrue(ParamRestorePlan.IsDeviceId("INS_ACC_ID"));
    }

    [TestMethod]
    public void BinaryOnboardFilesPreviewAsHex()
    {
        StringAssert.StartsWith(OnboardFileViewModel.Preview("ThreadsInfo\nIdle\n"u8.ToArray()), "ThreadsInfo");
        StringAssert.StartsWith(OnboardFileViewModel.Preview([0, 1, 2, 255]), "4 bytes (binary)");
    }

    [TestMethod]
    public void CtrlFScreenListsTheToolPages()
    {
        var services = FakeUi.Services(out _);
        var shell = new ShellViewModel(services);
        shell.NavigateCommand.Execute(Screen.Tools);

        Assert.IsTrue(shell.IsTools);
        var tools = (ToolsViewModel)shell.CurrentPage;
        var pages = tools.VisibleItems.Where(i => i.IsPage).Select(i => i.Title).ToList();
        CollectionAssert.IsSubsetOf(new[] { "Vehicle commands", "Barometer", "Message interval", "Onboard files", "Parameter restore",
            "MAVLink packet decoder", "Device ID decoder", "APJ tool" }, pages);
        Assert.IsInstanceOfType<VehicleCommandsViewModel>(tools.Content, "the first page opens");
    }

    [TestMethod]
    [Timeout(90000, CooperativeCancellation = true)]
    public async Task VehicleCommandsSendMissionPlannersMagicNumbers()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync();

        var services = FakeUi.Services(out _);
        var connecting = services.Session.ConnectAsync(LinkSpec.Tcp("127.0.0.1", port), getParams: false);
        using var vehicleSide = await accept;
        listener.Stop();
        await using var vehicle = new ExtrasFakeVehicle(vehicleSide.GetStream());
        await connecting;
        try
        {
            var vm = new VehicleCommandsViewModel(services);
            vm.OnShown();
            Assert.IsTrue(vm.CanCommand);

            await vm.ForceAccelCalibratedCommand.ExecuteAsync(null);
            Assert.IsFalse(vm.StatusIsError, vm.Status);
            var accel = vehicle.Commands.Last(c => c.command == (ushort)MAVLink.MAV_CMD.PREFLIGHT_CALIBRATION);
            Assert.AreEqual((0f, 76f), (accel.param2, accel.param5));

            await vm.ForceCompassCalibratedCommand.ExecuteAsync(null);
            var mag = vehicle.Commands.Last(c => c.command == (ushort)MAVLink.MAV_CMD.PREFLIGHT_CALIBRATION);
            Assert.AreEqual((76f, 0f), (mag.param2, mag.param5));

            await vm.RebootCommand.ExecuteAsync(null);
            Assert.IsFalse(vm.StatusIsError, vm.Status);
            var reboot = vehicle.Commands.Last(c => c.command == (ushort)MAVLink.MAV_CMD.PREFLIGHT_REBOOT_SHUTDOWN);
            Assert.AreEqual(1f, reboot.param1, "reboot the autopilot, not into its bootloader");

            await vm.UpdateBootloaderCommand.ExecuteAsync(null);
            Assert.AreEqual(290876f, vehicle.Commands.Last(c => c.command == (ushort)MAVLink.MAV_CMD.FLASH_BOOTLOADER).param5);

            await vm.LockupCommand.ExecuteAsync(null);
            await Wait.Until(() => vehicle.Commands.Any(c => c.command == (ushort)MAVLink.MAV_CMD.PREFLIGHT_REBOOT_SHUTDOWN));
            var lockup = vehicle.Commands.Last(c => c.command == (ushort)MAVLink.MAV_CMD.PREFLIGHT_REBOOT_SHUTDOWN);
            Assert.AreEqual((42f, 24f, 71f, 93f), (lockup.param1, lockup.param2, lockup.param3, lockup.param4));

            var interval = new MessageIntervalViewModel(services) { Message = "VFR_HUD", RateHz = 4 };
            await interval.SetCommand.ExecuteAsync(null);
            Assert.IsFalse(interval.StatusIsError, interval.Status);
            var set = vehicle.Commands.Last(c => c.command == (ushort)MAVLink.MAV_CMD.SET_MESSAGE_INTERVAL);
            Assert.AreEqual(((float)(int)MAVLink.MAVLINK_MSG_ID.VFR_HUD, 250_000f), (set.param1, set.param2));
        }
        finally
        {
            await services.Session.DisconnectAsync();
        }
    }

    private static class Wait
    {
        public static async Task Until(Func<bool> done)
        {
            var until = DateTime.UtcNow.AddSeconds(5);
            while (!done() && DateTime.UtcNow < until)
                await Task.Delay(20);
        }
    }
}
