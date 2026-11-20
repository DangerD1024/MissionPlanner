using System.Diagnostics;
using System.Net.NetworkInformation;
using MissionPlanner.App.Core.Session;
using MissionPlanner.App.Core.ViewModels.Simulation;
using MissionPlanner.App.Tests.Logs;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Tests.Sitl;

/// <summary>
/// The Simulation screen itself, started with a plane whose saved state holds a real aircraft's
/// parameters (it cannot arm): it must notice, offer to restart with defaults, back the state up,
/// and the restarted plane must arm and fly. Opt in with MP_TEST_SITL=1 and MP_TEST_SITL_STATE
/// pointing at such an eeprom.bin (e.g. a copy of &lt;data folder&gt;/sitl/plane/eeprom.bin).
/// </summary>
[TestClass]
[TestCategory("Sitl")]
public class SimulationScreenPlaneTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(480000, CooperativeCancellation = true)]
    public async Task StuckPlaneIsRestartedWithDefaultsAndFlies()
    {
        if (Environment.GetEnvironmentVariable("MP_TEST_SITL") != "1")
            Assert.Inconclusive("set MP_TEST_SITL=1 to run against ArduPilot SITL");
        var state = Environment.GetEnvironmentVariable("MP_TEST_SITL_STATE");
        if (state == null || !File.Exists(state))
            Assert.Inconclusive("set MP_TEST_SITL_STATE to a plane eeprom.bin that cannot arm");
        var binary = SitlProcess.FindBinary("ArduPlane");
        if (binary == null)
            Assert.Inconclusive("ArduPlane SITL binary not found");
        if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == 5760))
            Assert.Inconclusive("TCP 5760 is in use (another simulator is running)");

        // the screen's simulator folder in the test data folder: the real binary and defaults, the stuck state
        var source = Path.GetDirectoryName(binary)!;
        var sitlDir = Path.Combine(Settings.GetUserDataDirectory(), "sitl");
        Directory.CreateDirectory(Path.Combine(sitlDir, "plane"));
        Directory.CreateDirectory(Path.Combine(sitlDir, "models"));
        File.CreateSymbolicLink(Path.Combine(sitlDir, "ArduPlane"), binary);
        foreach (var f in new[] { "ArduPlane.json", "sim_vehicle.py", "vehicleinfo.py", Path.Combine("models", "plane.parm") })
            if (File.Exists(Path.Combine(source, f)))
                File.Copy(Path.Combine(source, f), Path.Combine(sitlDir, f), overwrite: true);
        File.Copy(state, Path.Combine(sitlDir, "plane", "eeprom.bin"), overwrite: true);

        var ui = new FakeUiServices { ConfirmAnswer = false }; // decline any simulator update offer
        var services = ui.CreateServices();
        var vm = new SimulationViewModel(services);
        vm.SelectedVehicle = vm.Vehicles.First(v => v.Vehicle == SitlVehicle.Plane);
        var session = services.Session;
        try
        {
            await vm.StartCommand.ExecuteAsync(null);
            Assert.IsTrue(session.IsConnected, "the screen did not connect: " + vm.StatusText);

            ui.ConfirmAnswer = true; // accept the restart offer
            var sw = Stopwatch.StartNew();
            await WaitFor(() => ui.Confirms.Any(c => c.Contains("cannot arm", StringComparison.Ordinal)), TimeSpan.FromSeconds(120), "the offer");
            TestContext.WriteLine($"offered after {sw.Elapsed.TotalSeconds:F0} s: {ui.Confirms.Last(c => c.Contains("cannot arm", StringComparison.Ordinal))}");

            await WaitFor(() => session.IsConnected && session.Cs.prearmstatus, TimeSpan.FromSeconds(150), "the restarted plane to be ready");
            Assert.IsTrue(Directory.GetFiles(Path.Combine(sitlDir, "plane"), "eeprom-*.bin").Length == 1, "state not backed up");
            TestContext.WriteLine($"ready after the restart, {sw.Elapsed.TotalSeconds:F0} s");

            var v = session.Vehicle;
            await Task.Run(() => session.Mav.setMode(v.sysid, v.compid, "TAKEOFF"));
            await WaitFor(() => string.Equals(session.Cs.mode, "TAKEOFF", StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(10), "TAKEOFF mode");
            Assert.IsTrue(await Task.Run(() => session.Mav.doARM(v.sysid, v.compid, true)), "arming refused: " + session.Cs.messageHigh);
            await WaitFor(() => session.Cs.alt > 40 && session.Cs.airspeed > 12, TimeSpan.FromSeconds(90), "climb out");
            TestContext.WriteLine($"flying: alt {session.Cs.alt:F0} m, airspeed {session.Cs.airspeed:F1} m/s");
        }
        finally
        {
            await vm.StopCommand.ExecuteAsync(null);
            await session.DisposeAsync();
        }
    }

    private async Task WaitFor(Func<bool> condition, TimeSpan timeout, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout)
                Assert.Fail("timed out waiting for " + what);
            await Task.Delay(250);
        }
    }
}
