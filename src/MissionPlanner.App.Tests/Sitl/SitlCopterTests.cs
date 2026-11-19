using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.Mission;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Tests.Logs;
using MissionPlanner.ArduPilot.Mavlink;

namespace MissionPlanner.App.Tests.Sitl;

/// <summary>
/// End-to-end checks against ArduPilot SITL (ArduCopter), through the app's own session, parameter
/// service and mission store. Opt in with MP_TEST_SITL=1; the binary is looked up in
/// MP_SITL_DIR or the Mission Planner data folder's sitl/ (where the Simulation page puts it).
/// </summary>
[TestClass]
[TestCategory("Sitl")]
public class SitlCopterTests
{
    private const int Instance = 9;
    private static SitlProcess? _sitl;
    private static AppServices? _services;
    private static TimeSpan _connectTime;
    private static readonly List<string> ConnectSteps = new();

    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        if (Environment.GetEnvironmentVariable("MP_TEST_SITL") != "1")
            return;
        var binary = SitlProcess.FindBinary("ArduCopter");
        if (binary == null)
            return;

        _sitl = await SitlProcess.StartAsync(binary, "+", "copter.parm", Instance);
        _services = new FakeUiServices().CreateServices();
        var sw = Stopwatch.StartNew();
        var progress = new Progress<OperationProgress>(p =>
        {
            lock (ConnectSteps)
                ConnectSteps.Add(p.Status);
        });
        await _services.Session.ConnectAsync(LinkSpec.Tcp("127.0.0.1", _sitl.Port), getParams: true, progress);
        _connectTime = sw.Elapsed;
    }

    [ClassCleanup]
    public static async Task Stop()
    {
        if (_services != null)
            await _services.Session.DisposeAsync();
        _sitl?.Dispose();
    }

    private AppServices Services
    {
        get
        {
            if (Environment.GetEnvironmentVariable("MP_TEST_SITL") != "1")
                Assert.Inconclusive("set MP_TEST_SITL=1 to run against ArduPilot SITL");
            if (_services == null || !_services.Session.IsConnected)
                Assert.Inconclusive("ArduCopter SITL binary not found (MP_SITL_DIR or <data dir>/sitl/)");
            return _services;
        }
    }

    [TestMethod]
    public void ConnectDownloadsParametersOverMavftp()
    {
        var s = Services.Session;
        var count = s.Vehicle.param.Count;
        TestContext.WriteLine($"connect + parameters: {_connectTime.TotalSeconds:F1} s, {count} parameters, capabilities {s.Cs.capabilities}");

        Assert.IsGreaterThan(500, count);
        Assert.IsTrue((s.Cs.capabilities & (ulong)MAVLink.MAV_PROTOCOL_CAPABILITY.FTP) != 0, "SITL reports MAVFTP");
        // the library reports which transfer it used
        lock (ConnectSteps)
            Assert.IsTrue(ConnectSteps.Any(x => x.Contains("Getting Param MAVFTP", StringComparison.Ordinal)),
                "parameters did not come over MAVFTP: " + string.Join(" | ", ConnectSteps.Distinct().Take(8)));
        Assert.IsLessThan(30, _connectTime.TotalSeconds, "connect took too long");
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task ParameterWriteReadsBack()
    {
        var p = Services.Params;
        const string name = "FENCE_RADIUS";
        var original = p.Get(name) ?? 150;
        var target = original == 175 ? 180 : 175;

        Assert.IsTrue(await p.SetAsync(name, target), "write refused");
        var v = Services.Session.Vehicle;
        var readBack = await Task.Run(() => Services.Session.Mav.GetParam(v.sysid, v.compid, name));
        Assert.AreEqual(target, readBack, 0.001);

        Assert.IsTrue(await p.SetAsync(name, original));
    }

    [TestMethod]
    [Timeout(90000, CooperativeCancellation = true)]
    public async Task MissionUploadsAndDownloadsUnchanged()
    {
        var store = Services.Mission;
        await Services.Dispatcher.InvokeAsync(() =>
        {
            store.Mission.Clear();
            store.SetHome(-35.363261, 149.165230, 584);
            store.Mission.Add(new MissionItem { Command = MAVLink.MAV_CMD.TAKEOFF, Alt = 20, Lat = 0, Lng = 0 });
            store.Mission.Add(store.NewWaypoint(-35.3620, 149.1650));
            store.Mission.Add(store.NewWaypoint(-35.3625, 149.1662));
            store.Mission.Add(new MissionItem { Command = MAVLink.MAV_CMD.RETURN_TO_LAUNCH });
        });
        var sent = store.Mission.Select(i => (i.Command, Math.Round(i.Lat, 6), Math.Round(i.Lng, 6), i.Alt)).ToList();

        await store.WriteToVehicleAsync(Services.Session, MAVLink.MAV_MISSION_TYPE.MISSION);
        await Services.Dispatcher.InvokeAsync(() => store.Mission.Clear());
        await store.ReadFromVehicleAsync(Services.Session, MAVLink.MAV_MISSION_TYPE.MISSION);

        var got = store.Mission.Select(i => (i.Command, Math.Round(i.Lat, 6), Math.Round(i.Lng, 6), i.Alt)).ToList();
        CollectionAssert.AreEqual(sent, got);
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task MavftpListsSystemFiles()
    {
        var s = Services.Session;
        var ftp = new MAVFtp(s.Mav, s.Vehicle.sysid, s.Vehicle.compid);
        var list = await Task.Run(() => ftp.kCmdListDirectory("@SYS", new CancellationTokenSource()));
        TestContext.WriteLine(string.Join(", ", list.Select(f => f.Name)));
        Assert.IsTrue(list.Any(f => f.Name == "uarts.txt" || f.Name == "threads.txt"), "@SYS listing");
    }

    [TestMethod]
    [Timeout(240000, CooperativeCancellation = true)]
    public async Task ArmsTakesOffAndLandsInGuided()
    {
        var s = Services.Session;
        var v = s.Vehicle;
        var mav = s.Mav;

        // the EKF needs GPS before GUIDED and arming are allowed
        await WaitFor(() => s.Cs.gpsstatus >= 3 && s.Cs.satcount >= 6, TimeSpan.FromSeconds(90), "GPS fix");
        await Task.Run(() => mav.setMode(v.sysid, v.compid, "GUIDED"));
        await WaitFor(() => string.Equals(s.Cs.mode, "Guided", StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(10), "GUIDED mode");

        var armed = false;
        var sw = Stopwatch.StartNew();
        while (!armed && sw.Elapsed < TimeSpan.FromSeconds(90))
        {
            armed = await Task.Run(() => mav.doARM(v.sysid, v.compid, true));
            if (!armed)
                await Task.Delay(2000);
        }

        Assert.IsTrue(armed, "arming refused: " + s.Cs.messageHigh);
        Assert.IsTrue(await Task.Run(() => mav.doCommand(v.sysid, v.compid, MAVLink.MAV_CMD.TAKEOFF, 0, 0, 0, 0, 0, 0, 10)), "takeoff refused");
        await WaitFor(() => s.Cs.alt > 8, TimeSpan.FromSeconds(40), "climb to 10 m");
        TestContext.WriteLine($"airborne at {s.Cs.alt:F1} m");

        await Task.Run(() => mav.setMode(v.sysid, v.compid, "LAND"));
        await WaitFor(() => !s.Cs.armed, TimeSpan.FromSeconds(90), "land and disarm");
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task LogListAnswers()
    {
        var entries = await Services.Session.Mav.GetLogEntry();
        TestContext.WriteLine($"{entries.Count} logs on the vehicle");
        Assert.IsNotNull(entries);
    }

    private static async Task WaitFor(Func<bool> condition, TimeSpan timeout, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout)
                Assert.Fail("timed out waiting for " + what);
            await Task.Delay(200);
        }
    }
}

/// <summary>An ArduPilot SITL process in its own temporary working directory.</summary>
internal sealed class SitlProcess : IDisposable
{
    private readonly Process _process;
    private readonly string _dir;

    private SitlProcess(Process process, string dir, int port)
    {
        _process = process;
        _dir = dir;
        Port = port;
    }

    public int Port { get; }

    public static string? SitlDir()
    {
        var env = Environment.GetEnvironmentVariable("MP_SITL_DIR");
        if (!string.IsNullOrEmpty(env))
            return env;
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                 })
        {
            var dir = Path.Combine(root, "Mission Planner", "sitl");
            if (Directory.Exists(dir))
                return dir;
        }

        return null;
    }

    public static string? FindBinary(string name)
    {
        var dir = SitlDir();
        if (dir == null)
            return null;
        foreach (var candidate in new[] { name, name + ".elf", name + ".exe" })
        {
            var path = Path.Combine(dir, candidate);
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    public static async Task<SitlProcess> StartAsync(string binary, string model, string defaults, int instance)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mp-sitl-{instance}-{Guid.NewGuid():N}"[..24]);
        Directory.CreateDirectory(dir);
        var defaultsPath = Path.Combine(Path.GetDirectoryName(binary)!, defaults);
        var psi = new ProcessStartInfo(binary)
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "-M" + model, "-O-35.363261,149.165230,584,353", "-s1", "--serial0", "tcp:0", "-I" + instance, "--defaults", defaultsPath })
            psi.ArgumentList.Add(a);
        psi.Environment["HOME"] = dir;

        var p = Process.Start(psi) ?? throw new InvalidOperationException("SITL did not start");
        p.OutputDataReceived += (_, _) => { };
        p.ErrorDataReceived += (_, _) => { };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        var port = 5760 + 10 * instance;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            if (p.HasExited)
                throw new InvalidOperationException("SITL exited with " + p.ExitCode);
            if (OperatingSystem.IsWindows())
            {
                // look for the listening socket without connecting: the Windows (cygwin) build exits
                // when its first client disconnects, so a probe connection would end the simulator
                if (IsListening(port))
                {
                    await Task.Delay(500);
                    return new SitlProcess(p, dir, port);
                }

                await Task.Delay(250);
                continue;
            }

            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync("127.0.0.1", port);
                // SITL accepts one client on serial0: let it reset before the real connection
                probe.Close();
                await Task.Delay(1500);
                return new SitlProcess(p, dir, port);
            }
            catch (SocketException)
            {
                await Task.Delay(250);
            }
        }

        p.Kill(true);
        throw new TimeoutException("SITL did not open TCP " + port);
    }

    /// <summary>Whether something listens on the TCP port, found without connecting to it (used on Windows).</summary>
    public static bool IsListening(int port) =>
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port);

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(true);
            _process.WaitForExit(3000);
        }
        catch (InvalidOperationException)
        {
        }

        _process.Dispose();
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }
}
