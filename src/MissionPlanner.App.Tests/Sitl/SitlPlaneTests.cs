using System.Diagnostics;
using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.Session;
using MissionPlanner.App.Core.ViewModels.Simulation;
using AppSitl = MissionPlanner.App.Core.ViewModels.Simulation.SitlProcess;

namespace MissionPlanner.App.Tests.Sitl;

/// <summary>ArduPlane SITL started the way the Simulation screen starts it: it must arm and fly.</summary>
[TestClass]
[TestCategory("Sitl")]
public class SitlPlaneTests
{
    private const int Instance = 12;

    public TestContext TestContext { get; set; } = null!;

    private static string RequirePlane()
    {
        if (Environment.GetEnvironmentVariable("MP_TEST_SITL") != "1")
            Assert.Inconclusive("set MP_TEST_SITL=1 to run against ArduPilot SITL");
        return SitlProcess.FindBinary("ArduPlane") ?? throw new AssertInconclusiveException("ArduPlane SITL binary not found");
    }

    /// <summary>
    /// Started through the Simulation screen's own launcher (arguments, defaults file, wiped state):
    /// the plane arms, takes off in TAKEOFF mode, loiters and returns home.
    /// </summary>
    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task PlaneArmsTakesOffAndFlies()
    {
        var binary = RequirePlane();
        var sitlDir = Path.GetDirectoryName(binary)!;
        var work = Path.Combine(Path.GetTempPath(), "mp-plane-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var output = new List<string>();

        var launch = new SitlLaunch("plane", SitlHome.Cmac, 1, Path.Combine(sitlDir, "models", "plane.parm"), null, Wipe: true, Instance: Instance);
        var psi = AppSitl.CreateStartInfo(binary, SitlArguments.Build(launch), work, sitlDir, windows: false);
        using var process = AppSitl.Start(psi, line =>
        {
            lock (output)
                output.Add(line);
        }, _ => { });

        await using var session = new VehicleSession();
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                await session.ConnectAsync(LinkSpec.Tcp("127.0.0.1", SitlArguments.TcpPort(Instance)), getParams: true);
                break;
            }
            catch (ConnectException) when (sw.Elapsed < TimeSpan.FromSeconds(30))
            {
                await Task.Delay(1000);
            }
        }

        var v = session.Vehicle;
        var mav = session.Mav;
        var cs = session.Cs;

        await WaitFor(() => cs.prearmstatus, TimeSpan.FromSeconds(120), "ready to arm", session);
        TestContext.WriteLine($"ready to arm after {sw.Elapsed.TotalSeconds:F0} s");

        await Task.Run(() => mav.setMode(v.sysid, v.compid, "TAKEOFF"));
        await WaitFor(() => string.Equals(cs.mode, "TAKEOFF", StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(10), "TAKEOFF mode", session);
        Assert.IsTrue(await Task.Run(() => mav.doARM(v.sysid, v.compid, true)), "arming refused: " + cs.messageHigh);

        await WaitFor(() => cs.alt > 40 && cs.airspeed > 12, TimeSpan.FromSeconds(90), "climb out", session);
        TestContext.WriteLine($"airborne: alt {cs.alt:F0} m, airspeed {cs.airspeed:F1} m/s");

        await Task.Run(() => mav.setMode(v.sysid, v.compid, "LOITER"));
        await WaitFor(() => string.Equals(cs.mode, "Loiter", StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(10), "LOITER mode", session);
        await Task.Delay(15000);
        Assert.IsGreaterThan(30, cs.alt, "lost height while loitering");
        Assert.IsGreaterThan(10, cs.groundspeed, "not flying");

        await Task.Run(() => mav.setMode(v.sysid, v.compid, "RTL"));
        await WaitFor(() => string.Equals(cs.mode, "RTL", StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(10), "RTL mode", session);
        TestContext.WriteLine($"RTL at alt {cs.alt:F0} m, {cs.DistToHome:F0} m from home");

        process.Kill();
        try
        {
            Directory.Delete(work, true);
        }
        catch (IOException)
        {
        }
    }

    private static async Task WaitFor(Func<bool> condition, TimeSpan timeout, string what, VehicleSession session)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout)
                Assert.Fail($"timed out waiting for {what}; last message: {session.Cs.messageHigh}; " +
                            string.Join(" | ", session.Cs.messages.ToList().TakeLast(6).Select(m => m.message)));
            await Task.Delay(250);
        }
    }

    /// <summary>Diagnostic: everything the plane says while we try to arm it, fresh and from the user's saved state.</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(240000, CooperativeCancellation = true)]
    public async Task ArmingReport(bool fromSavedState)
    {
        var binary = RequirePlane();
        var saved = Path.Combine(Path.GetDirectoryName(binary)!, "plane", "eeprom.bin");
        if (fromSavedState && !File.Exists(saved))
            Assert.Inconclusive("no saved plane state");

        using var sitl = await SitlProcess.StartAsync(binary, "plane", "models/plane.parm", Instance, fromSavedState ? saved : null);
        await using var session = new VehicleSession();
        await session.ConnectAsync(LinkSpec.Tcp("127.0.0.1", sitl.Port), getParams: true);
        var v = session.Vehicle;
        var mav = session.Mav;

        // what this state changed: values that differ from the firmware's defaults (MAVFTP sends both)
        var changed = v.param.ToList()
            .Where(p => p.default_value is { } d && Math.Abs(p.Value - d) > 1e-4)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => $"{p.Name} = {p.Value} (default {p.default_value})")
            .ToList();
        if (Environment.GetEnvironmentVariable("MP_TEST_PARAM_DUMP") is { Length: > 0 } dumpDir)
            File.WriteAllLines(Path.Combine(dumpDir, $"plane-{(fromSavedState ? "saved" : "fresh")}.param"),
                v.param.ToList().OrderBy(p => p.Name, StringComparer.Ordinal)
                    .Select(p => $"{p.Name},{p.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
        TestContext.WriteLine($"--- {changed.Count} parameters differ from their defaults");
        foreach (var line in changed)
            TestContext.WriteLine(line);

        var armed = false;
        var sw = Stopwatch.StartNew();
        while (!armed && sw.Elapsed < TimeSpan.FromSeconds(120))
        {
            await Task.Delay(5000);
            try
            {
                armed = await Task.Run(() => mav.doARM(v.sysid, v.compid, true));
            }
            catch (Exception ex)
            {
                TestContext.WriteLine($"{sw.Elapsed.TotalSeconds:F0}s arm: {ex.Message}");
            }

            TestContext.WriteLine($"{sw.Elapsed.TotalSeconds:F0}s armed={armed} mode={session.Cs.mode} gps={session.Cs.gpsstatus} sats={session.Cs.satcount} ekf={session.Cs.ekfstatus:F2}");
        }

        TestContext.WriteLine("--- vehicle messages");
        foreach (var (time, message) in session.Cs.messages.ToList().DistinctBy(m => m.message))
            TestContext.WriteLine(message);
        TestContext.WriteLine("--- simulator output (last 30 lines)");
        lock (sitl.Output)
            foreach (var line in sitl.Output.TakeLast(30))
                TestContext.WriteLine(line);

        Assert.IsTrue(armed, "plane did not arm: " + session.Cs.messageHigh);
    }
}
