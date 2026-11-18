using System.Net;
using System.Net.Sockets;
using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.ViewModels.SetupScreens;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Firmware;
using MissionPlanner.App.Tests.Logs;

namespace MissionPlanner.App.Tests.SetupScreens.Firmware;

[TestClass]
public class FirmwareViewModelTests
{
    private static readonly SerialDevice Matek = new("/dev/ttyACM0", "MatekH743", "ArduPilot", new UsbId(0x1209, 0x5740));

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task TilesAndPortsFollowTheManifest()
    {
        var ui = new FakeUiServices();
        var vm = new FirmwareViewModel(ui.CreateServices(), new FakePortFactory(), () => [Matek, new SerialDevice("/dev/ttyS4")], () => []);

        vm.UseCatalog(FirmwareFixtures.Catalog());
        await vm.RefreshPortsCommand.ExecuteAsync(null);

        Assert.AreEqual("V4.7.2 OFFICIAL", vm.Tiles.Single(t => t.Title == "Copter").VersionText);
        Assert.AreEqual("V4.7.1 OFFICIAL", vm.Tiles.Single(t => t.Title == "Plane").VersionText);
        Assert.IsFalse(vm.Tiles.Single(t => t.Title == "Sub").IsAvailable);

        vm.SelectedRelease = vm.Releases.Single(r => r.Type == MissionPlanner.ArduPilot.APFirmware.RELEASE_TYPES.BETA);
        Assert.AreEqual("V4.7.3-beta1 BETA", vm.Tiles.Single(t => t.Title == "Copter").VersionText);
        Assert.IsTrue(vm.HasReleaseWarning);

        Assert.AreEqual(2, vm.Ports.Count);
        Assert.AreSame(vm.Ports[0], vm.SelectedPort, "the recognised board is selected");
        var (ids, board, platform) = vm.ResolveBoard();
        CollectionAssert.AreEqual(new long[] { 1013 }, ids.ToArray());
        Assert.AreEqual("MatekH743", board);
        Assert.AreEqual("MatekH743", platform);
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task VehicleTileAsksBeforeInstalling()
    {
        var ui = new FakeUiServices { ConfirmAnswer = false };
        var vm = new FirmwareViewModel(ui.CreateServices(), new FakePortFactory(), () => [Matek], () => []);
        vm.UseCatalog(FirmwareFixtures.Catalog());
        await vm.RefreshPortsCommand.ExecuteAsync(null);

        await vm.InstallCommand.ExecuteAsync(vm.Tiles.Single(t => t.Title == "Copter"));

        Assert.AreEqual(1, ui.Confirms.Count);
        StringAssert.Contains(ui.Confirms[0], "Copter V4.7.2 (OFFICIAL) for MatekH743");
        Assert.IsFalse(vm.IsBusy);
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task UnknownBoardIsExplained()
    {
        var ui = new FakeUiServices();
        var vm = new FirmwareViewModel(ui.CreateServices(), new FakePortFactory(), () => [new SerialDevice("/dev/ttyUSB0")], () => []);
        vm.UseCatalog(FirmwareFixtures.Catalog());
        await vm.RefreshPortsCommand.ExecuteAsync(null);

        await vm.InstallCommand.ExecuteAsync(vm.Tiles[0]);

        Assert.AreEqual(0, ui.Confirms.Count);
        StringAssert.Contains(ui.Messages.Single(), "Detect bootloader");
    }

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task CustomFirmwareIsFlashedThroughTheBootloader()
    {
        var image = FirmwareFixtures.Image(4000, seed: 11);
        var ui = new FakeUiServices { OpenFileAnswer = FirmwareFixtures.WriteApj(1013, image) };
        var port = new FakeBootloaderPort("/dev/ttyACM0", 1013);
        var factory = new FakePortFactory();
        factory.Add("/dev/ttyACM0", () => port);
        var vm = new FirmwareViewModel(ui.CreateServices(), factory, () => [], () => ["/dev/ttyACM0"]);
        vm.UseCatalog(FirmwareFixtures.Catalog());

        await vm.LoadCustomFirmwareCommand.ExecuteAsync(null);

        Assert.AreEqual(UploadOutcome.Flashed, vm.LastOutcome, vm.StatusText);
        StringAssert.Contains(ui.Confirms[0], "MatekH743 (board id 1013)");
        CollectionAssert.AreEqual(image, port.Flash.Take(image.Length).ToArray());
        Assert.IsTrue(port.Rebooted);
        Assert.IsFalse(vm.IsBusy);
        Assert.IsTrue(vm.LogLines.Any(l => l.Contains("Found board id 1013", StringComparison.Ordinal)));
    }

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task DetectBootloaderRemembersTheBoard()
    {
        var ui = new FakeUiServices();
        var factory = new FakePortFactory();
        factory.Add("/dev/ttyACM3", () => new FakeBootloaderPort("/dev/ttyACM3", 140));
        var vm = new FirmwareViewModel(ui.CreateServices(), factory, () => [], () => ["/dev/ttyACM3"]);
        vm.UseCatalog(FirmwareFixtures.Catalog());

        await vm.DetectBootloaderCommand.ExecuteAsync(null);

        Assert.AreEqual(140L, vm.DetectedBoardId);
        StringAssert.Contains(vm.DetectedText, "CubeOrange");
        var (ids, _, platform) = vm.ResolveBoard();
        CollectionAssert.AreEqual(new long[] { 140 }, ids.ToArray());
        Assert.AreEqual("CubeOrange", platform);
    }

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task MavlinkRebootAsksForTheBootloader()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync();
        var link = LinkFactory.Create(LinkSpec.Tcp("127.0.0.1", port));

        var reboot = Task.Run(() => MavlinkBootloaderReboot.TryReboot(link));
        using var vehicleSide = await accept;
        listener.Stop();
        await using var vehicle = new FakeVehicle(vehicleSide.GetStream(), 0);

        Assert.IsTrue(await reboot);
        MAVLink.mavlink_command_long_t? cmd = null;
        for (var i = 0; i < 50 && cmd == null; i++)
        {
            cmd = vehicle.Received.Where(m => m.msgid == (uint)MAVLink.MAVLINK_MSG_ID.COMMAND_LONG)
                .Select(m => (MAVLink.mavlink_command_long_t?)m.ToStructure<MAVLink.mavlink_command_long_t>())
                .FirstOrDefault();
            if (cmd == null)
                await Task.Delay(50);
        }

        Assert.IsNotNull(cmd);
        Assert.AreEqual((ushort)MAVLink.MAV_CMD.PREFLIGHT_REBOOT_SHUTDOWN, cmd.Value.command);
        Assert.AreEqual(3f, cmd.Value.param1);
        Assert.AreEqual(FakeVehicle.SysId, cmd.Value.target_system);
    }
}
