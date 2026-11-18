using MissionPlanner.App.Core.ViewModels.SetupScreens.Firmware;

namespace MissionPlanner.App.Tests.SetupScreens.Firmware;

[TestClass]
public class FirmwareUploadFlowTests
{
    private sealed class ScriptedBootloader(string port, int boardId) : IBootloader
    {
        public string PortName { get; } = port;
        public bool Same { get; set; }
        public Exception? SameThrows { get; set; }
        public Exception? UploadThrows { get; set; }
        public bool Uploaded { get; private set; }
        public bool RebootedOnly { get; private set; }
        public bool Disposed { get; private set; }
        public CancellationToken UploadToken { get; private set; }

        public BootloaderInfo Identify() => new(PortName, boardId, 0, 5, 2 * 1024 * 1024);

        public bool HasSameFirmware(ApjFirmware firmware) => SameThrows != null ? throw SameThrows : Same;

        public void Upload(ApjFirmware firmware, Action<FlashStep, double> progress, Action<string> log, CancellationToken cancel)
        {
            UploadToken = cancel;
            if (UploadThrows != null)
                throw UploadThrows;
            progress(FlashStep.Erase, -1);
            progress(FlashStep.Program, 50);
            progress(FlashStep.Program, 100);
            progress(FlashStep.Verify, 100);
            log("verified");
            Uploaded = true;
        }

        public void Reboot() => RebootedOnly = true;

        public void Dispose() => Disposed = true;
    }

    /// <summary>Ports appear from a given probe round on; opening a missing port fails.</summary>
    private sealed class ScriptedFactory : IBootloaderFactory
    {
        public readonly Dictionary<string, ScriptedBootloader> Boards = new();
        public readonly Dictionary<string, int> AppearsInRound = new();
        public int Round;
        public readonly List<ScriptedBootloader> Opened = new();

        public IBootloader Open(string portName)
        {
            if (!Boards.TryGetValue(portName, out var b) || Round < AppearsInRound.GetValueOrDefault(portName))
                throw new IOException("not there");
            lock (Opened)
                Opened.Add(b);
            return b;
        }
    }

    private static ApjFirmware Firmware(int boardId = 1013) =>
        ApjFirmware.Parse(FirmwareFixtures.Apj(boardId, FirmwareFixtures.Image(1000)));

    private static (FirmwareUploadFlow Flow, List<UploadProgress> Progress, List<string> Log) Create(ScriptedFactory factory,
        string[] ports, TimeSpan? timeout = null, Func<string, Task<bool>>? confirm = null, Func<CancellationToken, Task<bool>>? reboot = null)
    {
        var progress = new List<UploadProgress>();
        var log = new List<string>();
        var flow = new FirmwareUploadFlow(factory, () =>
        {
            factory.Round++;
            return ports;
        })
        {
            BootloaderTimeout = timeout ?? TimeSpan.FromSeconds(5),
            ScanInterval = TimeSpan.FromMilliseconds(10),
            SettleDelay = TimeSpan.Zero,
            ConfirmSameFirmware = confirm,
            RebootToBootloader = reboot,
            Progress = new SyncProgress<UploadProgress>(p =>
            {
                lock (progress)
                    progress.Add(p);
            }),
            Log = l =>
            {
                lock (log)
                    log.Add(l);
            },
        };
        return (flow, progress, log);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task WaitsForTheBoardThenFlashes()
    {
        var factory = new ScriptedFactory();
        var board = new ScriptedBootloader("/dev/ttyACM0", 1013);
        factory.Boards["/dev/ttyACM0"] = board;
        factory.AppearsInRound["/dev/ttyACM0"] = 3; // plugged in a little later
        var (flow, progress, log) = Create(factory, ["/dev/ttyS0", "/dev/ttyACM0"]);

        var result = await flow.RunAsync(Firmware(), CancellationToken.None);

        Assert.AreEqual(UploadOutcome.Flashed, result.Outcome, result.Message);
        Assert.IsTrue(board.Uploaded);
        Assert.IsTrue(board.Disposed);
        Assert.AreEqual(1013, result.Board!.BoardId);
        var stages = progress.Select(p => p.Stage).Distinct().ToList();
        CollectionAssert.AreEqual(new[]
        {
            UploadStage.WaitingForBootloader, UploadStage.CheckingFirmware, UploadStage.Erasing, UploadStage.Programming,
            UploadStage.Verifying, UploadStage.Done,
        }, stages);
        Assert.AreEqual(100, progress[^1].Percent);
        Assert.IsTrue(log.Contains("verified"));
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task WrongBoardIsNotFlashed()
    {
        var factory = new ScriptedFactory();
        var other = new ScriptedBootloader("/dev/ttyACM0", 140);
        factory.Boards["/dev/ttyACM0"] = other;
        var (flow, _, log) = Create(factory, ["/dev/ttyACM0"], TimeSpan.FromMilliseconds(400));

        var result = await flow.RunAsync(Firmware(1013), CancellationToken.None);

        Assert.AreEqual(UploadOutcome.NoBoard, result.Outcome);
        Assert.IsFalse(other.Uploaded);
        Assert.IsTrue(other.Disposed);
        StringAssert.Contains(result.Message, "board id 140");
        StringAssert.Contains(result.Message, "board id 1013");
        // reported once, not every probe round
        Assert.AreEqual(1, log.Count(l => l.Contains("not the board", StringComparison.Ordinal)));
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task NoBootloaderTimesOut()
    {
        var factory = new ScriptedFactory();
        var (flow, _, _) = Create(factory, ["/dev/ttyACM0"], TimeSpan.FromMilliseconds(300));

        var result = await flow.RunAsync(Firmware(), CancellationToken.None);

        Assert.AreEqual(UploadOutcome.NoBoard, result.Outcome);
        StringAssert.Contains(result.Message, "No response");
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task SameFirmwareIsSkippedWhenTheUserSaysNo()
    {
        var factory = new ScriptedFactory();
        var board = new ScriptedBootloader("COM7", 1013) { Same = true };
        factory.Boards["COM7"] = board;
        string? asked = null;
        var (flow, _, _) = Create(factory, ["COM7"], confirm: q =>
        {
            asked = q;
            return Task.FromResult(false);
        });

        var result = await flow.RunAsync(Firmware(), CancellationToken.None);

        Assert.AreEqual(UploadOutcome.NotNeeded, result.Outcome);
        Assert.IsNotNull(asked);
        Assert.IsFalse(board.Uploaded);
        Assert.IsTrue(board.RebootedOnly, "the board is sent back to its firmware");
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task SameFirmwareIsUploadedAgainWhenConfirmed()
    {
        var factory = new ScriptedFactory();
        var board = new ScriptedBootloader("COM7", 1013) { Same = true };
        factory.Boards["COM7"] = board;
        var (flow, _, _) = Create(factory, ["COM7"], confirm: _ => Task.FromResult(true));

        var result = await flow.RunAsync(Firmware(), CancellationToken.None);

        Assert.AreEqual(UploadOutcome.Flashed, result.Outcome);
        Assert.IsTrue(board.Uploaded);
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task LostCommunicationDuringChecksumFails()
    {
        var factory = new ScriptedFactory();
        factory.Boards["COM7"] = new ScriptedBootloader("COM7", 1013) { SameThrows = new TimeoutException("silence") };
        var (flow, _, _) = Create(factory, ["COM7"]);

        var result = await flow.RunAsync(Firmware(), CancellationToken.None);

        Assert.AreEqual(UploadOutcome.Failed, result.Outcome);
        StringAssert.Contains(result.Message, "Lost communication");
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task UploadErrorFails()
    {
        var factory = new ScriptedFactory();
        var board = new ScriptedBootloader("COM7", 1013) { UploadThrows = new IOException("Program CRC failed.") };
        factory.Boards["COM7"] = board;
        var (flow, progress, _) = Create(factory, ["COM7"]);

        var result = await flow.RunAsync(Firmware(), CancellationToken.None);

        Assert.AreEqual(UploadOutcome.Failed, result.Outcome);
        StringAssert.Contains(result.Message, "Program CRC failed");
        Assert.AreEqual(UploadStage.Failed, progress[^1].Stage);
        Assert.IsTrue(board.Disposed);
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task CancelWhileWaiting()
    {
        var factory = new ScriptedFactory();
        var (flow, _, _) = Create(factory, ["COM1"], TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var result = await flow.RunAsync(Firmware(), cts.Token);

        Assert.AreEqual(UploadOutcome.Cancelled, result.Outcome);
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task AsksForAMavlinkRebootOnceWhenNoBootloaderAnswers()
    {
        var factory = new ScriptedFactory();
        var board = new ScriptedBootloader("/dev/ttyACM0", 1013);
        factory.Boards["/dev/ttyACM0"] = board;
        factory.AppearsInRound["/dev/ttyACM0"] = int.MaxValue; // only after the reboot
        var reboots = 0;
        var (flow, progress, _) = Create(factory, ["/dev/ttyACM0"], reboot: _ =>
        {
            reboots++;
            factory.AppearsInRound["/dev/ttyACM0"] = factory.Round + 2;
            return Task.FromResult(true);
        });

        var result = await flow.RunAsync(Firmware(), CancellationToken.None);

        Assert.AreEqual(UploadOutcome.Flashed, result.Outcome);
        Assert.AreEqual(1, reboots);
        Assert.IsTrue(progress.Any(p => p.Stage == UploadStage.RebootingToBootloader));
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task FlashesThroughTheRealProtocolOnAFakeBoard()
    {
        var image = FirmwareFixtures.Image(5000, seed: 7);
        var fw = ApjFirmware.Parse(FirmwareFixtures.Apj(1013, image));
        var port = new FakeBootloaderPort("/dev/ttyACM1", 1013);
        var factory = new FakePortFactory();
        factory.Add("/dev/ttyACM1", () => port);
        factory.Add("/dev/ttyS0", () => new FakeBootloaderPort("/dev/ttyS0", 0) { Silent = true });
        var log = new List<string>();
        var flow = new FirmwareUploadFlow(factory, () => ["/dev/ttyS0", "/dev/ttyACM1"])
        {
            SettleDelay = TimeSpan.Zero,
            Log = l =>
            {
                lock (log)
                    log.Add(l);
            },
        };

        var result = await flow.RunAsync(fw, CancellationToken.None);

        Assert.AreEqual(UploadOutcome.Flashed, result.Outcome, result.Message);
        CollectionAssert.AreEqual(image, port.Flash.Take(image.Length).ToArray());
        Assert.IsTrue(port.Rebooted);
        Assert.AreEqual(1, port.EraseCount);
        Assert.IsTrue(log.Any(l => l.StartsWith("CRC expected", StringComparison.Ordinal)), string.Join("\n", log));

        // flashing the same file again: the CRC matches and nothing is written without consent
        var again = new FakeBootloaderPort("/dev/ttyACM1", 1013);
        again.Preload(port.Flash);
        var factory2 = new FakePortFactory();
        factory2.Add("/dev/ttyACM1", () => again);
        var flow2 = new FirmwareUploadFlow(factory2, () => ["/dev/ttyACM1"]) { SettleDelay = TimeSpan.Zero };
        var second = await flow2.RunAsync(fw, CancellationToken.None);
        Assert.AreEqual(UploadOutcome.NotNeeded, second.Outcome, second.Message);
        Assert.AreEqual(0, again.EraseCount);
        Assert.IsTrue(again.Rebooted);
    }
}
