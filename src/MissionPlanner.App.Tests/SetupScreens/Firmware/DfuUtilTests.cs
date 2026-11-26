using System.Runtime.InteropServices;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Firmware;
using static MissionPlanner.App.Tests.SetupScreens.Firmware.DfuTestSupport;

namespace MissionPlanner.App.Tests.SetupScreens.Firmware;

/// <summary>dfu-util's command line, output and outcomes, against a scripted fake: no program is started.</summary>
[TestClass]
public class DfuUtilTests
{
    private static (DfuFlasher Flasher, List<string> Log, List<UploadProgress> Progress) Create(FakeDfuRunner runner, DfuUtilLocator? locator = null)
    {
        var log = new List<string>();
        var progress = new List<UploadProgress>();
        var flasher = new DfuFlasher(runner, locator ?? LinuxLocator())
        {
            Log = l =>
            {
                lock (log)
                    log.Add(l);
            },
            Progress = new SyncProgress<UploadProgress>(p =>
            {
                lock (progress)
                    progress.Add(p);
            }),
        };
        return (flasher, log, progress);
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    [TestMethod]
    public void DownloadCommandLine()
    {
        CollectionAssert.AreEqual(
            new[] { "-a", "0", "-d", "0483:df11", "-s", "0x08000000:leave", "-D", "/tmp/fw.bin" },
            DfuFlasher.DownloadArguments(0x08000000, "/tmp/fw.bin").ToArray());
        CollectionAssert.AreEqual(new[] { "-s", "0x0800A000:leave" }, DfuFlasher.DownloadArguments(0x0800A000, "x").Skip(4).Take(2).ToArray());
        Assert.AreEqual("dfu-util -a 0 -d 0483:df11 -s 0x08000000:leave -D \"/tmp/my fw.bin\"",
            new ProcessRequest("dfu-util", DfuFlasher.DownloadArguments(0x08000000, "/tmp/my fw.bin")).CommandLine);
    }

    [TestMethod]
    public void ProgressBarsAreReadFromCarriageReturnRedraws()
    {
        var output = new DfuUtilOutput();
        var lines = new List<DfuLine>();
        FakeDfuRunner.Print((l, e) => lines.Add(output.Add(l, e)), false, FakeDfuRunner.Banner + FakeDfuRunner.DownloadTranscript(1677264));

        Assert.AreEqual("dfu-util 0.11", output.Version);
        var erase = lines.Where(l => l.Stage == UploadStage.Erasing).Select(l => l.Percent).ToArray();
        var write = lines.Where(l => l.Stage == UploadStage.Programming).Select(l => l.Percent).ToArray();
        CollectionAssert.AreEqual(new[] { 0, 20, 40, 60, 80, 100 }, erase);
        CollectionAssert.AreEqual(new[] { 0, 20, 40, 60, 80, 100 }, write);
        Assert.IsTrue(lines.Any(l => l.Text == "Erase    done."));
        Assert.IsTrue(output.Downloaded);
        Assert.IsTrue(output.LeaveRequested);
        Assert.IsTrue(output.Succeeded(0));
        Assert.AreEqual(0, output.Errors.Count);
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task GetStatusErrorAfterLeaveIsSuccess()
    {
        var runner = new FakeDfuRunner();
        var (flasher, log, progress) = Create(runner);
        var image = FlashImage.FromBinary(FirmwareFixtures.Image(4096, seed: 9));

        var result = await flasher.FlashAsync(image, CancellationToken.None);

        Assert.AreEqual(DfuOutcome.Flashed, result.Outcome, result.Message);
        StringAssert.Contains(result.Message, "starting ArduPilot");
        CollectionAssert.AreEqual(image.Data, runner.Flashed);
        Assert.IsFalse(File.Exists(runner.FlashedPath), "the temporary .bin is deleted");
        var download = runner.Downloads.Single();
        Assert.AreEqual(BundledLinux, download.FileName);
        CollectionAssert.AreEqual(new[] { "-a", "0", "-d", "0483:df11", "-s", "0x08000000:leave", "-D" }, download.Arguments.Take(7).ToArray());

        Assert.IsTrue(log.Contains("Using dfu-util 0.11 (bundled)."), string.Join("\n", log));
        Assert.IsTrue(log.Any(l => l.StartsWith("Running " + BundledLinux + " -a 0 -d 0483:df11 -s 0x08000000:leave -D ", StringComparison.Ordinal)));
        Assert.IsTrue(log.Contains("File downloaded successfully"));
        Assert.IsTrue(log.Contains("dfu-util exited with code 74."));
        Assert.IsFalse(log.Any(l => l.Contains("DFU suffix", StringComparison.Ordinal)), "the raw .bin warning is expected and not shown");
        Assert.IsFalse(log.Any(l => l.Contains("Copyright", StringComparison.Ordinal)));

        Assert.IsTrue(progress.Any(p => p is { Stage: UploadStage.Erasing, Percent: 40 }));
        Assert.IsTrue(progress.Any(p => p is { Stage: UploadStage.Programming, Percent: 60 }));
        Assert.AreEqual(UploadStage.Done, progress[^1].Stage);
        Assert.AreEqual(100, progress[^1].Percent);
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task ErrorBeforeTheEndIsAFailure()
    {
        var runner = new FakeDfuRunner
        {
            OnDownload = (_, output, _) =>
            {
                FakeDfuRunner.Print(output, false, FakeDfuRunner.Banner + "Downloading element to address = 0x08000000, size = 4096\n" +
                                                   "\rErase   \t[=========================] 100%         4096 bytes\nErase    done.\n" +
                                                   "\rDownload\t[=====                    ]  20%          819 bytes");
                FakeDfuRunner.Print(output, false, " failed!\n");
                FakeDfuRunner.Print(output, true, "DFU state(10) = dfuERROR, status(10) = Device's firmware is corrupt\ndfu-util: Error during download get_status\n");
                return Task.FromResult(74);
            },
        };
        var (flasher, _, _) = Create(runner);

        var result = await flasher.FlashAsync(FlashImage.FromBinary(new byte[4096]), CancellationToken.None);

        Assert.AreEqual(DfuOutcome.Failed, result.Outcome);
        StringAssert.Contains(result.Message, "Error during download get_status");
        StringAssert.Contains(result.Message, "hold BOOT", "the board can be flashed again");
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task NoBoardExplainsDfuModeAndOnWindowsTheDriver()
    {
        static FakeDfuRunner NoDevice() => new()
        {
            OnDownload = (_, output, _) =>
            {
                FakeDfuRunner.Print(output, false, FakeDfuRunner.Banner);
                FakeDfuRunner.Print(output, true, "dfu-util: No DFU capable USB device available\n");
                return Task.FromResult(74);
            },
        };

        var (linux, _, _) = Create(NoDevice());
        var r = await linux.FlashAsync(FlashImage.FromBinary([1]), CancellationToken.None);
        Assert.AreEqual(DfuOutcome.NoDevice, r.Outcome);
        StringAssert.Contains(r.Message, "BOOT");
        StringAssert.Contains(r.Message, "iNav");
        StringAssert.Contains(r.Message, "Reboot to DFU");
        Assert.IsFalse(r.Message.Contains("Zadig", StringComparison.Ordinal));

        var windows = new DfuUtilLocator(HostOs.Windows, Architecture.X64, @"C:\MP\Tools\dfu-util", "", _ => true);
        var (win, _, _) = Create(NoDevice(), windows);
        r = await win.FlashAsync(FlashImage.FromBinary([1]), CancellationToken.None);
        StringAssert.Contains(r.Message, "Zadig");
        StringAssert.Contains(r.Message, "STM32 BOOTLOADER");
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task DeviceThatCannotBeOpenedGetsTheRightHint()
    {
        static FakeDfuRunner CannotOpen(string error) => new()
        {
            OnDownload = (_, output, _) =>
            {
                FakeDfuRunner.Print(output, true, $"dfu-util: Cannot open DFU device 0483:df11 found on devnum 7 ({error})\n" +
                                                  "dfu-util: No DFU capable USB device available\n");
                return Task.FromResult(74);
            },
        };

        var windows = new DfuUtilLocator(HostOs.Windows, Architecture.X64, @"C:\MP\Tools\dfu-util", "", _ => true);
        var r = await Create(CannotOpen("LIBUSB_ERROR_NOT_SUPPORTED"), windows).Flasher.FlashAsync(FlashImage.FromBinary([1]), CancellationToken.None);
        Assert.AreEqual(DfuOutcome.NoDevice, r.Outcome);
        StringAssert.Contains(r.Message, "WinUSB");
        StringAssert.Contains(r.Message, "zadig.akeo.ie");

        r = await Create(CannotOpen("LIBUSB_ERROR_ACCESS")).Flasher.FlashAsync(FlashImage.FromBinary([1]), CancellationToken.None);
        StringAssert.Contains(r.Message, "Allow USB access");
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task CancellingStopsTheRun()
    {
        var runner = new FakeDfuRunner
        {
            OnDownload = async (_, output, cancel) =>
            {
                FakeDfuRunner.Print(output, false, "\rErase   \t[=====                    ]  20%          819 bytes");
                await Task.Delay(Timeout.Infinite, cancel);
                return 0;
            },
        };
        var (flasher, _, _) = Create(runner);
        using var cts = new CancellationTokenSource();

        var run = flasher.FlashAsync(FlashImage.FromBinary(new byte[16]), cts.Token);
        while (!runner.Started)
            await Task.Delay(10);
        await cts.CancelAsync();
        var result = await run;

        Assert.AreEqual(DfuOutcome.Cancelled, result.Outcome);
        StringAssert.Contains(result.Message, "incomplete");
        Assert.IsFalse(File.Exists(runner.FlashedPath));
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task BundledThatDoesNotStartFallsBackToPath()
    {
        var runner = new FakeDfuRunner();
        runner.Broken.Add(BundledLinux);
        var (flasher, log, _) = Create(runner, LinuxLocator("/usr/bin/dfu-util"));

        var tool = await flasher.FindToolAsync(CancellationToken.None);

        Assert.IsNotNull(tool);
        Assert.AreEqual("/usr/bin/dfu-util", tool.Tool.Path);
        Assert.AreEqual("dfu-util 0.11 (/usr/bin/dfu-util)", tool.Text);
        Assert.IsTrue(log.Any(l => l.Contains("Bad CPU type", StringComparison.Ordinal)));
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task MissingDfuUtilSaysWhatToInstall()
    {
        var runner = new FakeDfuRunner();
        var nothing = new DfuUtilLocator(HostOs.Linux, Architecture.X64, AppDir, "/usr/bin", _ => false);
        var r = await Create(runner, nothing).Flasher.FlashAsync(FlashImage.FromBinary([1]), CancellationToken.None);
        Assert.AreEqual(DfuOutcome.NoTool, r.Outcome);
        StringAssert.Contains(r.Message, "sudo apt install dfu-util");
        Assert.AreEqual(0, runner.Requests.Count);

        var mac = new DfuUtilLocator(HostOs.MacOS, Architecture.Arm64, "/Applications/MP.app/Contents/MacOS/Tools/dfu-util", "/usr/bin",
            p => p.EndsWith("/Tools/dfu-util/dfu-util", StringComparison.Ordinal));
        var macRunner = new FakeDfuRunner();
        macRunner.Broken.Add("/Applications/MP.app/Contents/MacOS/Tools/dfu-util/dfu-util");
        var (macFlasher, _, _) = Create(macRunner, mac);
        Assert.IsNull(await macFlasher.FindToolAsync(CancellationToken.None));
        var text = macFlasher.MissingToolMessage();
        StringAssert.Contains(text, "softwareupdate --install-rosetta");
        StringAssert.Contains(text, "brew install dfu-util");

        var win = new DfuFlasher(runner, new DfuUtilLocator(HostOs.Windows, Architecture.X64, @"C:\MP", "", _ => false));
        await win.FindToolAsync(CancellationToken.None);
        text = win.MissingToolMessage();
        StringAssert.Contains(text, "Zadig");
        StringAssert.Contains(text, "STM32CubeProgrammer");
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task ListingParsesFoundLinesOncePerBoard()
    {
        var runner = new FakeDfuRunner
        {
            ListOutput = """
                Found DFU: [0483:df11] ver=2200, devnum=7, cfg=1, intf=0, path="1-4", alt=1, name="@Option Bytes   /0x5200201C/01*128 e", serial="357A35673133"
                Found DFU: [0483:df11] ver=2200, devnum=7, cfg=1, intf=0, path="1-4", alt=0, name="@Internal Flash   /0x08000000/16*128Kg", serial="357A35673133"

                """,
        };
        var (flasher, _, _) = Create(runner);

        var (devices, problem) = await flasher.ListDevicesAsync(CancellationToken.None);

        Assert.IsNull(problem);
        var d = devices.Single();
        Assert.AreEqual(DfuFlasher.StmDfu, d.Id);
        Assert.AreEqual("357A35673133", d.Serial);
        Assert.AreEqual("1-4", d.Location);
        StringAssert.Contains(d.Description, "0483:DF11");
        CollectionAssert.AreEqual(new[] { "-l", "-d", "0483:df11" }, runner.Requests.Last().Arguments.ToArray());
    }

    [TestMethod]
    public void SysfsScanFindsOnlyTheStmBootloader()
    {
        var root = Path.Combine(TestEnvironment.DataRoot, "sysfs-" + Guid.NewGuid().ToString("N"));
        void Device(string name, params (string File, string Value)[] attrs)
        {
            var dir = Path.Combine(root, name);
            Directory.CreateDirectory(dir);
            foreach (var (f, v) in attrs)
                File.WriteAllText(Path.Combine(dir, f), v + "\n");
        }

        Device("1-4", ("idVendor", "0483"), ("idProduct", "df11"), ("product", "STM32  BOOTLOADER"), ("serial", "357A35673133"),
            ("busnum", "1"), ("devnum", "7"));
        Device("1-4:1.0", ("bInterfaceClass", "fe"));
        Device("1-5", ("idVendor", "1209"), ("idProduct", "5740"), ("product", "MatekH743"));
        Device("usb1", ("idVendor", "1d6b"), ("idProduct", "0002"));

        var found = DfuDeviceScanner.ScanSysfs(root);

        var d = found.Single();
        Assert.AreEqual("STM32 BOOTLOADER", d.Name);
        Assert.AreEqual("357A35673133", d.Serial);
        Assert.AreEqual("USB bus 1 device 7", d.Location);
        Assert.AreEqual(0, DfuDeviceScanner.ScanSysfs(Path.Combine(root, "missing")).Count);
    }
}
