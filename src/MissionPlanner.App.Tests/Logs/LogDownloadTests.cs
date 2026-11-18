using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.ViewModels.Logs;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Tests.Logs;

[TestClass]
public class LogDownloadTests
{
    private const uint EntryTime = 1_700_000_000;

    [TestMethod]
    public void FileNamesFollowMissionPlanner()
    {
        Assert.AreEqual("a-b-c-d ' [x] y z", LogFileNaming.MakeValidFileName("a/b\\c:d \" <x> y|z"));
        Assert.AreEqual(Path.Combine("logs", "QUADROTOR", "1"), LogFileNaming.VehicleFolder("logs", "QUADROTOR", 1));

        var t = new DateTime(2026, 5, 6, 7, 8, 9);
        Assert.AreEqual("12 2026-05-06 07-08-09.bin", LogFileNaming.EntryFileName(12, t));
        Assert.AreEqual("2026-05-06 07-08-09.bin", LogFileNaming.GpsFileName(t));

        Assert.IsNull(LogFileNaming.EntryTime(0));
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(EntryTime).LocalDateTime, LogFileNaming.EntryTime(EntryTime));
    }

    [TestMethod]
    public void StoredLogIsRenamedByGpsTimeAndNeverOverwrites()
    {
        var folder = Path.Combine(TestEnvironment.DataRoot, "store-" + Guid.NewGuid().ToString("N")[..8]);
        var gpsTime = DFLog.gpsTimeToTime(LogTestFiles.GpsWeek, LogTestFiles.GpsMs / 1000.0);

        string Store(byte[] data, ushort id)
        {
            var temp = LogTestFiles.TempPath(".tmp");
            File.WriteAllBytes(temp, data);
            var path = LogFileNaming.StoreDownloaded(temp, folder, id, new DateTime(2026, 1, 1, 12, 0, 0));
            Assert.IsFalse(File.Exists(temp));
            return path;
        }

        var first = Store(LogTestFiles.BinaryLog(), 3);
        Assert.AreEqual(Path.Combine(folder, LogFileNaming.GpsFileName(gpsTime)), first);

        var second = Store(LogTestFiles.BinaryLog(), 3);
        Assert.AreEqual(Path.Combine(folder, Path.GetFileNameWithoutExtension(LogFileNaming.GpsFileName(gpsTime)) + " (2).bin"), second);

        // no GPS fix: keeps the entry name
        var noGps = Store(LogTestFiles.BinaryLog(withGps: false), 4);
        Assert.AreEqual(Path.Combine(folder, "4 2026-01-01 12-00-00.bin"), noGps);
    }

    [TestMethod]
    public void MissingBlocksAreAskedForInFewRequests()
    {
        // close holes: one request spanning them
        var have = Enumerable.Repeat(true, 100).ToArray();
        have[10] = have[11] = have[40] = false;
        Assert.AreEqual((900u, 41u * 90 - 900), LogDownloader.NextRequest(have, 100 * 90));

        // the last block may be short
        have = Enumerable.Repeat(true, 10).ToArray();
        have[9] = false;
        Assert.AreEqual((810u, 50u), LogDownloader.NextRequest(have, 860));

        // far apart holes: the first only
        have = Enumerable.Repeat(true, 10_000).ToArray();
        have[5] = have[9_000] = false;
        Assert.AreEqual((450u, 90u), LogDownloader.NextRequest(have, 10_000 * 90));

        Assert.AreEqual((0u, 0u), LogDownloader.NextRequest([true, true], 180));
    }

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task ListsDownloadsRenamesAndErases()
    {
        var gpsLog = LogTestFiles.BinaryLog(attSamples: 300);
        var plainLog = LogTestFiles.BinaryLog(attSamples: 40, withGps: false);
        await using var rig = await Rig.StartAsync([(EntryTime, gpsLog), (0, plainLog), (EntryTime + 60, plainLog)]);
        var (vm, ui, vehicle) = (rig.Vm, rig.Ui, rig.Vehicle);

        vm.OnShown();
        await WaitFor(() => !vm.IsListing && vm.Entries.Count == 3, "log list");

        var rows = vm.Entries.ToList();
        CollectionAssert.AreEqual(new ushort[] { 1, 2, 3 }, rows.Select(r => r.Id).ToArray());
        Assert.AreEqual((uint)gpsLog.Length, rows[0].Size);
        Assert.AreEqual(LogFileNaming.EntryTime(EntryTime), rows[0].Time);
        Assert.AreEqual("no date", rows[1].DateText);
        Assert.IsFalse(vm.StatusIsError, vm.Status);

        // selected rows only
        rows[0].IsSelected = true;
        rows[1].IsSelected = true;
        Assert.IsNull(vm.AllSelected);
        var sw = Stopwatch.StartNew();
        await vm.DownloadSelectedCommand.ExecuteAsync(null);
        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(10), "two small logs took " + sw.Elapsed);

        Assert.IsFalse(vm.StatusIsError, vm.Status);
        Assert.AreEqual(LogEntryState.Done, rows[0].State);
        Assert.AreEqual(LogEntryState.Done, rows[1].State);
        Assert.AreEqual(LogEntryState.None, rows[2].State);

        var folder = LogFileNaming.VehicleFolder(Settings.Instance.LogDir, "QUADROTOR", FakeLogVehicle.SysId);
        var gpsTime = DFLog.gpsTimeToTime(LogTestFiles.GpsWeek, LogTestFiles.GpsMs / 1000.0);
        Assert.AreEqual(Path.Combine(folder, LogFileNaming.GpsFileName(gpsTime)), rows[0].LocalPath);
        CollectionAssert.AreEqual(gpsLog, File.ReadAllBytes(rows[0].LocalPath!));
        Assert.AreEqual(Path.Combine(folder, LogFileNaming.EntryFileName(2, null)), rows[1].LocalPath);
        CollectionAssert.AreEqual(plainLog, File.ReadAllBytes(rows[1].LocalPath!));
        Assert.AreEqual(rows[1].LocalPath, vm.LastDownloaded);
        Assert.AreEqual(100, vm.OverallProgress, 1e-9);
        Assert.IsTrue(vehicle.Requests.Contains((uint)MAVLink.MAVLINK_MSG_ID.LOG_REQUEST_END), "the vehicle is told the transfer ended");
        Assert.AreEqual(0, Directory.GetFiles(folder, "*.part").Length);

        // review opens the browser on the log
        vm.ReviewCommand.Execute(rows[0]);
        var browser = (LogBrowserViewModel)ui.Shown.Single();
        await WaitFor(() => browser.HasLog, "browser loads the log");
        Assert.AreEqual(Path.GetFileName(rows[0].LocalPath), browser.FileName);
        browser.Dispose();

        // erase asks first
        ui.ConfirmAnswer = false;
        await vm.ClearLogsCommand.ExecuteAsync(null);
        Assert.AreEqual(0, vehicle.EraseCount);
        Assert.AreEqual(3, vm.Entries.Count);

        ui.ConfirmAnswer = true;
        await vm.ClearLogsCommand.ExecuteAsync(null);
        Assert.AreEqual(2, ui.Confirms.Count);
        await WaitFor(() => vehicle.EraseCount > 0, "LOG_ERASE");
        Assert.AreEqual(0, vm.Entries.Count);
    }

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task CancelStopsTheLogInProgressAndTheQueue()
    {
        var log = LogTestFiles.BinaryLog(attSamples: 400);
        await using var rig = await Rig.StartAsync([(EntryTime, log), (EntryTime, log)]);
        var (vm, vehicle) = (rig.Vm, rig.Vehicle);
        vehicle.BlockDelayMs = 5;

        await vm.ListLogsCommand.ExecuteAsync(null);
        Assert.AreEqual(2, vm.Entries.Count);

        var all = vm.DownloadAllCommand.ExecuteAsync(null);
        Assert.IsTrue(vm.IsDownloading);
        Assert.IsTrue(vm.CanCancel);
        await WaitFor(() =>
        {
            vm.Tick();
            return vm.FileProgress > 5;
        }, "the first blocks");

        vm.CancelCommand.Execute(null);
        Assert.IsTrue(vm.CancelRequested);
        Assert.IsFalse(vm.CanCancel);
        await all.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(vm.Entries.All(r => r.State == LogEntryState.Skipped), string.Join(",", vm.Entries.Select(r => r.State)));
        Assert.IsTrue(vm.Entries.All(r => r.LocalPath == null));
        StringAssert.StartsWith(vm.Status, "Cancelled");
        Assert.IsFalse(vm.IsDownloading);
        Assert.IsTrue(vehicle.Requests.Contains((uint)MAVLink.MAVLINK_MSG_ID.LOG_REQUEST_END));
        Assert.IsTrue(rig.Services.Session.IsConnected, "cancel leaves the link up");

        var folder = LogFileNaming.VehicleFolder(Settings.Instance.LogDir, "QUADROTOR", FakeLogVehicle.SysId);
        Assert.AreEqual(0, Directory.GetFiles(folder, "*.part").Length, "a cancelled log leaves nothing behind");

        // and the next download works
        vehicle.BlockDelayMs = 0;
        await vm.DownloadAsync([vm.Entries[1]]);
        Assert.AreEqual(LogEntryState.Done, vm.Entries[1].State);
        CollectionAssert.AreEqual(log, File.ReadAllBytes(vm.Entries[1].LocalPath!));
    }

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task LostBlocksAreFetchedAgainAndAShorterLogEnds()
    {
        var log = LogTestFiles.BinaryLog(attSamples: 200);
        await using var rig = await Rig.StartAsync([(EntryTime, log)]);
        var (vm, vehicle) = (rig.Vm, rig.Vehicle);
        vehicle.DropOnce = block => block % 7 == 3;
        vehicle.ExtraListedSize = 1000;

        await vm.ListLogsCommand.ExecuteAsync(null);
        Assert.AreEqual((uint)(log.Length + 1000), vm.Entries[0].Size);
        await vm.DownloadAllCommand.ExecuteAsync(null);

        Assert.AreEqual(LogEntryState.Done, vm.Entries[0].State, vm.Status);
        CollectionAssert.AreEqual(log, File.ReadAllBytes(vm.Entries[0].LocalPath!));
        Assert.IsTrue(vehicle.Requests.Count(r => r == (uint)MAVLink.MAVLINK_MSG_ID.LOG_REQUEST_DATA) >= 2, "missing blocks asked again");
    }

    [TestMethod]
    public void WithoutAVehicleNothingIsAsked()
    {
        var ui = new FakeUiServices();
        var vm = new LogDownloadViewModel(ui.CreateServices());
        vm.OnShown();
        Assert.IsFalse(vm.IsConnected);
        Assert.IsFalse(vm.CanList);
        Assert.IsTrue(vm.ShowNotConnected);
        vm.ListLogsCommand.Execute(null);
        Assert.IsTrue(vm.StatusIsError);
        Assert.AreEqual(0, ui.Confirms.Count);
    }

    private static async Task WaitFor(Func<bool> condition, string what, int seconds = 30)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(seconds))
                Assert.Fail("timed out waiting for " + what);
            await Task.Delay(20);
        }
    }

    /// <summary>A session connected over TCP to a <see cref="FakeLogVehicle"/>, and the page under test.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private TcpClient _client = null!;

        public FakeUiServices Ui { get; } = new();

        public AppServices Services { get; private set; } = null!;

        public FakeLogVehicle Vehicle { get; private set; } = null!;

        public LogDownloadViewModel Vm { get; private set; } = null!;

        public static async Task<Rig> StartAsync(IReadOnlyList<(uint, byte[])> logs)
        {
            var rig = new Rig();
            rig.Services = rig.Ui.CreateServices();

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accept = listener.AcceptTcpClientAsync();
            var connect = rig.Services.Session.ConnectAsync(LinkSpec.Tcp("127.0.0.1", port), getParams: false);
            rig._client = await accept;
            listener.Stop();
            rig.Vehicle = new FakeLogVehicle(rig._client.GetStream(), logs);
            await connect;
            rig.Vm = new LogDownloadViewModel(rig.Services);
            return rig;
        }

        public async ValueTask DisposeAsync()
        {
            Vm?.Dispose();
            await Services.Session.DisconnectAsync();
            _client.Dispose();
            await Vehicle.DisposeAsync();
        }
    }
}
