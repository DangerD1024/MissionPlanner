using MissionPlanner.App.Core.ViewModels.SetupScreens;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Firmware;
using MissionPlanner.App.Tests.Logs;
using static MissionPlanner.App.Tests.SetupScreens.Firmware.DfuTestSupport;

namespace MissionPlanner.App.Tests.SetupScreens.Firmware;

/// <summary>Install Firmware's DFU path end to end with fakes: manifest row → .hex → image → dfu-util → log.</summary>
[TestClass]
public class FirmwareDfuViewModelTests
{
    private static FirmwareViewModel Create(FakeUiServices ui, DfuEnvironment dfu)
    {
        var vm = new FirmwareViewModel(ui.CreateServices(), new FakePortFactory(), () => [], () => [], dfu);
        vm.UseCatalog(FirmwareFixtures.Catalog());
        vm.ShowAllOptions = true;
        return vm;
    }

    private static FirmwareRow Row(FirmwareViewModel vm, string vehicle, string release = "OFFICIAL") =>
        vm.Options!.Rows.Single(r => r.Platform == "MatekH743" && r.Vehicle == vehicle && r.Release == release);

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task InstallViaDfuDownloadsTheHexAndFlashesIt()
    {
        var (hex, image) = WithBootloader();
        var ui = new FakeUiServices();
        var runner = new FakeDfuRunner();
        var downloads = new List<Uri>();
        var heads = new List<Uri>();
        var vm = Create(ui, Environment(runner, download: (_, path) => File.WriteAllTextAsync(path, hex), downloads: downloads, heads: heads));
        await vm.RefreshPortsCommand.ExecuteAsync(null);
        Assert.IsTrue(vm.HasDfuDevice);
        StringAssert.Contains(vm.DfuStatusText, "STM32 BOOTLOADER");

        vm.Options!.Selected = Row(vm, "Copter");
        Assert.IsTrue(vm.InstallSelectedViaDfuCommand.CanExecute(null));
        await vm.InstallSelectedViaDfuCommand.ExecuteAsync(null);

        Assert.AreEqual(DfuOutcome.Flashed, vm.LastDfuOutcome, vm.StatusText);
        // the manifest's hex entry, no guessing on the server
        Assert.AreEqual("https://firmware.ardupilot.org/Copter/stable/MatekH743/arducopter_with_bl.hex", downloads.Single().AbsoluteUri);
        Assert.AreEqual(0, heads.Count);

        var flash = runner.Downloads.Single();
        Assert.AreEqual(BundledLinux, flash.FileName);
        CollectionAssert.AreEqual(new[] { "-a", "0", "-d", "0483:df11", "-s", "0x08000000:leave", "-D" }, flash.Arguments.Take(7).ToArray());
        CollectionAssert.AreEqual(image, runner.Flashed, "bootloader, 0xFF gap and firmware as one image");

        var confirm = ui.Confirms.Single();
        StringAssert.Contains(confirm, "Copter V4.7.2 (OFFICIAL) for MatekH743");
        StringAssert.Contains(confirm, "arducopter_with_bl.hex");
        StringAssert.Contains(confirm, "whole firmware");
        StringAssert.Contains(confirm, "bootloader");
        StringAssert.Contains(confirm, "settings");
        StringAssert.Contains(confirm, "iNav");

        var done = ui.Messages.Last();
        StringAssert.Contains(done, "starting ArduPilot");
        StringAssert.Contains(done, "USB");
        StringAssert.Contains(done, "frame type");
        StringAssert.Contains(done, "calibration");

        var log = string.Join("\n", vm.LogLines);
        StringAssert.Contains(log, "DFU image (from the manifest): https://firmware.ardupilot.org/Copter/stable/MatekH743/arducopter_with_bl.hex");
        StringAssert.Contains(log, "Using dfu-util 0.11 (bundled).");
        StringAssert.Contains(log, "image 0x08000000–0x0802005F");
        StringAssert.Contains(log, "Running " + BundledLinux + " -a 0 -d 0483:df11 -s 0x08000000:leave -D ");
        StringAssert.Contains(log, "File downloaded successfully");
        StringAssert.Contains(log, "board resetting");
        Assert.AreEqual(100, vm.ProgressValue);
        Assert.IsFalse(vm.IsBusy);
        Assert.AreEqual("dfu-util 0.11 (bundled)", vm.DfuToolText);
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task HexMissingFromTheManifestIsFoundBesideTheApj()
    {
        var (hex, _) = WithBootloader();
        var ui = new FakeUiServices();
        var runner = new FakeDfuRunner();
        var downloads = new List<Uri>();
        var heads = new List<Uri>();
        var vm = Create(ui, Environment(runner, download: (_, path) => File.WriteAllTextAsync(path, hex),
            exists: u => u.AbsolutePath.EndsWith("/arduplane_with_bl.hex", StringComparison.Ordinal), downloads: downloads, heads: heads));

        await vm.DfuInstallAsync(Row(vm, "Plane"));

        Assert.AreEqual(DfuOutcome.Flashed, vm.LastDfuOutcome, vm.StatusText);
        Assert.AreEqual("https://firmware.ardupilot.org/Plane/stable/MatekH743/arduplane_with_bl.hex", heads.Single().AbsoluteUri);
        Assert.AreEqual(heads.Single(), downloads.Single());
        Assert.IsTrue(vm.LogLines.Any(l => l.Contains("not in the manifest, found on the server", StringComparison.Ordinal)));
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task NoHexOnTheServerIsExplainedWithoutRunningAnything()
    {
        var ui = new FakeUiServices();
        var runner = new FakeDfuRunner();
        var downloads = new List<Uri>();
        var vm = Create(ui, Environment(runner, exists: _ => false, downloads: downloads));

        await vm.DfuInstallAsync(Row(vm, "Plane"));

        Assert.AreEqual(0, downloads.Count);
        Assert.AreEqual(0, runner.Requests.Count);
        StringAssert.Contains(ui.Messages.Single(), "_with_bl.hex");
        Assert.IsFalse(vm.IsBusy);
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task NoBoardInDfuModeStopsBeforeTheDownload()
    {
        var ui = new FakeUiServices();
        var runner = new FakeDfuRunner();
        var downloads = new List<Uri>();
        var vm = Create(ui, Environment(runner, devices: () => [], downloads: downloads));

        await vm.DfuInstallAsync(Row(vm, "Copter"));

        Assert.AreEqual(DfuOutcome.NoDevice, vm.LastDfuOutcome);
        Assert.AreEqual(0, downloads.Count);
        Assert.AreEqual(0, ui.Confirms.Count);
        Assert.AreEqual(0, runner.Downloads.Count);
        var message = ui.Messages.Single();
        StringAssert.Contains(message, "BOOT");
        StringAssert.Contains(message, "dfu");
        StringAssert.Contains(message, "bl");
        StringAssert.Contains(message, "Reboot to DFU");
        Assert.IsFalse(vm.HasDfuDevice);
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task AnsweringNoFlashesNothing()
    {
        var ui = new FakeUiServices { ConfirmAnswer = false };
        var runner = new FakeDfuRunner();
        var downloads = new List<Uri>();
        var vm = Create(ui, Environment(runner, downloads: downloads));

        await vm.DfuInstallAsync(Row(vm, "Copter"));

        Assert.AreEqual(1, ui.Confirms.Count);
        Assert.AreEqual(0, downloads.Count);
        Assert.AreEqual(0, runner.Downloads.Count);
        Assert.IsNull(vm.LastDfuOutcome);
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task CustomHexAndBinGoThroughDfu()
    {
        var (hex, image) = WithBootloader();
        var hexPath = Path.Combine(TestEnvironment.DataRoot, $"custom-{Guid.NewGuid():N}.hex");
        await File.WriteAllTextAsync(hexPath, hex);
        var ui = new FakeUiServices { OpenFileAnswer = hexPath };
        var runner = new FakeDfuRunner();
        var vm = Create(ui, Environment(runner));

        await vm.LoadCustomFirmwareCommand.ExecuteAsync(null);

        Assert.AreEqual(DfuOutcome.Flashed, vm.LastDfuOutcome, vm.StatusText);
        CollectionAssert.AreEqual(image, runner.Flashed);
        var confirm = ui.Confirms.Single();
        StringAssert.Contains(confirm, Path.GetFileName(hexPath));
        StringAssert.Contains(confirm, "0x08000000–0x0802005F");
        StringAssert.Contains(confirm, "bootloader");
        StringAssert.Contains(confirm, "settings");

        // a .bin goes to the start of the flash as it is
        var binPath = Path.Combine(TestEnvironment.DataRoot, $"custom-{Guid.NewGuid():N}.bin");
        var bin = FirmwareFixtures.Image(3000, seed: 5);
        await File.WriteAllBytesAsync(binPath, bin);
        ui.OpenFileAnswer = binPath;
        await vm.LoadCustomFirmwareCommand.ExecuteAsync(null);

        Assert.AreEqual(2, runner.Downloads.Count);
        CollectionAssert.AreEqual(bin, runner.Flashed);
        Assert.AreEqual("0x08000000:leave", runner.Downloads[1].Arguments[5]);
        StringAssert.Contains(ui.Confirms[1], "from 0x08000000");
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task BadHexFileIsRefusedBeforeDfuUtil()
    {
        var path = Path.Combine(TestEnvironment.DataRoot, $"bad-{Guid.NewGuid():N}.hex");
        await File.WriteAllTextAsync(path, Linear(0x0800) + ":0400000001020304FF\n" + End);
        var ui = new FakeUiServices { OpenFileAnswer = path };
        var runner = new FakeDfuRunner();
        var vm = Create(ui, Environment(runner));

        await vm.LoadCustomFirmwareCommand.ExecuteAsync(null);

        Assert.AreEqual(0, runner.Requests.Count);
        StringAssert.Contains(ui.Messages.Single(), "checksum");
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task WithoutSysfsCheckRunsTheListing()
    {
        var ui = new FakeUiServices();
        var runner = new FakeDfuRunner
        {
            ListOutput = "Found DFU: [0483:df11] ver=2200, devnum=4, cfg=1, intf=0, path=\"20-1\", alt=0, name=\"@Internal Flash  /0x08000000/16*128Kg\", serial=\"3A8B\"\n",
        };
        var mac = new DfuUtilLocator(HostOs.MacOS, System.Runtime.InteropServices.Architecture.X64, "/mp/Tools/dfu-util", "", p => p == "/mp/Tools/dfu-util/dfu-util");
        var env = new DfuEnvironment
        {
            Runner = runner,
            Locator = mac,
            ScanDevices = null,
            Download = (_, _, _, _) => Task.CompletedTask,
            UrlExists = (_, _) => Task.FromResult(false),
        };
        var vm = Create(ui, env);
        StringAssert.Contains(vm.DfuStatusText, "Check");

        await vm.RefreshPortsCommand.ExecuteAsync(null);
        Assert.AreEqual(0, runner.Requests.Count, "dfu-util -l only runs when Check is clicked");

        await vm.CheckDfuCommand.ExecuteAsync(null);

        Assert.IsTrue(vm.HasDfuDevice);
        StringAssert.Contains(vm.DfuStatusText, "serial 3A8B");
        var list = runner.Requests.Last();
        Assert.AreEqual("-l", list.Arguments[0]);
        Assert.IsTrue(list.Environment!.ContainsKey("DYLD_FALLBACK_LIBRARY_PATH"));
    }

    [TestMethod]
    public void HexEntryIsMatchedToItsApj()
    {
        var catalog = FirmwareFixtures.Catalog();
        var copter = catalog.All.Single(f => f.Format == "apj" && f.Platform == "MatekH743" && f.MavType == "Copter" && f.MavFirmwareVersionType == "OFFICIAL");
        Assert.AreEqual("https://firmware.ardupilot.org/Copter/stable/MatekH743/arducopter_with_bl.hex",
            DfuHexLocator.FromManifest(catalog.All, copter)!.Url!.AbsoluteUri);

        // same board, other release or vehicle: not that file
        var beta = catalog.All.Single(f => f.Format == "apj" && f.Platform == "MatekH743" && f.MavFirmwareVersionType == "BETA");
        Assert.IsNull(DfuHexLocator.FromManifest(catalog.All, beta));
        var heli = catalog.All.Single(f => f.Format == "apj" && f.Platform == "MatekH743" && f.MavType == "HELICOPTER");
        Assert.IsNull(DfuHexLocator.FromManifest(catalog.All, heli));

        Assert.AreEqual("https://firmware.ardupilot.org/Copter/stable/MatekH743-heli/arducopter-heli_with_bl.hex",
            DfuHexLocator.DerivedUrl(heli.Url)!.AbsoluteUri);
        Assert.AreEqual("https://firmware.ardupilot.org/Plane/stable/MatekH743/arduplane_with_bl.hex",
            DfuHexLocator.DerivedUrl(new Uri("https://firmware.ardupilot.org/Plane/stable/MatekH743/arduplane.apj"))!.AbsoluteUri);
    }

    [TestMethod]
    public void HexEntryWithAnotherCommitIsNotUsed()
    {
        var catalog = FirmwareCatalog.Parse("""
            { "firmware": [
              { "board_id": 1013, "mav-type": "FIXED_WING", "format": "apj", "mav-firmware-version-type": "OFFICIAL", "platform": "MatekH743",
                "git-sha": "d9c5097c", "url": "https://firmware.ardupilot.org/Plane/stable/MatekH743/arduplane.apj" },
              { "mav-type": "FIXED_WING", "format": "hex", "mav-firmware-version-type": "OFFICIAL", "platform": "MatekH743",
                "git-sha": "0000000", "url": "https://firmware.ardupilot.org/Plane/stable/MatekH743/arduplane_with_bl.hex" },
              { "mav-type": "FIXED_WING", "format": "hex", "mav-firmware-version-type": "OFFICIAL", "platform": "MatekH743",
                "git-sha": "d9c5097c", "url": "https://firmware.ardupilot.org/Plane/stable/MatekH743/arduplane.hex" }
            ] }
            """);
        Assert.IsNull(DfuHexLocator.FromManifest(catalog.All, catalog.Uploadable.Single()), "a stale hex, and a plain .hex without bootloader, are not used");
    }
}
