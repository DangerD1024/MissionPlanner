using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.Maps;
using MissionPlanner.App.Core.ViewModels.FlightData;

namespace MissionPlanner.App.Tests.FlightData;

/// <summary>
/// Opt-in check against ArduCopter SITL (never a real vehicle): arm and take off in GUIDED through
/// the Actions tab, then the Servo/Relay, Aux Function and gauge tabs, and ADS-B traffic from the
/// simulator. Run with MP_TEST_SITL=tcp:127.0.0.1:5800 (a SITL instance you started).
/// </summary>
[TestClass]
public class FlightDataExtrasSitlTests
{
    [ClassInitialize]
    public static void Init(TestContext _) =>
        MapTiles.Initialize(Path.Combine(TestEnvironment.DataRoot, "gmapcache") + Path.DirectorySeparatorChar);

    private static async Task<bool> Until(FlightDataViewModel vm, Func<bool> done, int seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!done() && DateTime.UtcNow < until)
        {
            vm.Tick();
            await Task.Delay(50);
        }

        vm.Tick();
        return done();
    }

    [TestMethod]
    [TestCategory("Sitl")]
    [Timeout(400000, CooperativeCancellation = true)]
    public async Task ExtraTabsAgainstArduCopterSitl()
    {
        var link = Environment.GetEnvironmentVariable("MP_TEST_SITL");
        if (string.IsNullOrEmpty(link) || !link.StartsWith("tcp:", StringComparison.Ordinal))
            Assert.Inconclusive("set MP_TEST_SITL=tcp:127.0.0.1:<port> to run against a SITL instance");

        var services = FakeUi.Services(out _);
        await services.Session.ConnectAsync(LinkSpec.Parse(link!), getParams: true);
        try
        {
            var vm = new FlightDataViewModel(services);
            vm.OnShown();
            Assert.IsTrue(await Until(vm, () => vm.IsConnected, 10));
            var cs = services.Session.Cs;
            var log = new List<string>();
            var s0 = services.Session;

            // traffic from the ADS-B simulator
            if (services.Params.Get("ADSB_TYPE") != 1 || services.Params.Get("SIM_ADSB_COUNT") != 5)
            {
                await services.Params.SetAsync("SIM_ADSB_COUNT", 5);
                await services.Params.SetAsync("ADSB_TYPE", 1);
                log.Add("ADS-B parameters set");
            }

            // the simulator spreads aircraft over SIM_ADSB_RADIUS; the autopilot keeps those within ADSB_LIST_RADIUS
            if (services.Params.Get("ADSB_LIST_RADIUS") is { } r && r < 10000)
                await services.Params.SetAsync("ADSB_LIST_RADIUS", 10000);

            // the autopilot forwards traffic to the ground station at SRn_ADSB
            if (services.Params.FirstExisting("SR0_ADSB", "MAV1_ADSB") is { } rate && services.Params.Get(rate) is < 1)
                await services.Params.SetAsync(rate, 5);
            // otherwise ask for the message itself
            await Task.Run(() => s0.Mav.doCommand(s0.Vehicle.sysid, s0.Vehicle.compid, MAVLink.MAV_CMD.SET_MESSAGE_INTERVAL,
                (float)MAVLink.MAVLINK_MSG_ID.ADSB_VEHICLE, 200_000, 0, 0, 0, 0, 0));

            // Actions tab: GUIDED, arm (retrying until the EKF and GPS are ready), take off
            vm.SelectedTabIndex = (int)FlightDataTab.Actions;
            await Until(vm, () => vm.Modes.Count > 0, 10);
            vm.SelectedMode = FlightModes.Find(vm.Modes, "Guided");
            await vm.SetModeCommand.ExecuteAsync(null);
            Assert.AreEqual("Guided", cs.mode, true, vm.ActionStatus);

            var armUntil = DateTime.UtcNow.AddSeconds(120);
            while (!cs.armed && DateTime.UtcNow < armUntil)
            {
                await vm.ArmDisarmCommand.ExecuteAsync(null);
                if (!cs.armed)
                    await Until(vm, () => false, 3);
            }

            Assert.IsTrue(cs.armed, "armed: " + vm.ActionStatus);
            var s = services.Session;
            var tookOff = await Task.Run(() => s.Mav.doCommand(s.Vehicle.sysid, s.Vehicle.compid, MAVLink.MAV_CMD.TAKEOFF, 0, 0, 0, 0, 0, 0, 15));
            Assert.IsTrue(tookOff, "NAV_TAKEOFF accepted");

            // Gauges follow the climb
            vm.SelectedTabIndex = (int)FlightDataTab.Gauges;
            Assert.IsTrue(await Until(vm, () => vm.Gauges.Altitude > 8, 40), "altitude " + vm.Gauges.Altitude);
            log.Add($"gauges: alt {vm.Gauges.Altitude} {vm.Gauges.AltitudeUnit}, vs {vm.Gauges.VerticalSpeed}, hdg {vm.Gauges.Heading}");

            // Servo/Relay: output 9 to its SERVO9_MAX and back to SERVO9_MIN, seen in SERVO_OUTPUT_RAW
            vm.SelectedTabIndex = (int)FlightDataTab.Servo;
            var ch9 = vm.ServoRelay.Servos.Single(x => x.Channel == 9);
            await ch9.SetHighCommand.ExecuteAsync(null);
            Assert.IsFalse(vm.ActionStatusIsError, vm.ActionStatus);
            Assert.IsTrue(await Until(vm, () => Math.Abs(ch9.Output - ch9.HighPwm) < 2, 10), $"ch9 {ch9.Output} != {ch9.HighPwm}");
            log.Add($"servo 9 high: {ch9.Output} ({vm.ActionStatus})");
            await ch9.SetLowCommand.ExecuteAsync(null);
            Assert.IsTrue(await Until(vm, () => Math.Abs(ch9.Output - ch9.LowPwm) < 2, 10), $"ch9 {ch9.Output} != {ch9.LowPwm}");
            log.Add($"servo 9 low: {ch9.Output}");
            await ch9.ToggleCommand.ExecuteAsync(null);
            Assert.IsTrue(await Until(vm, () => Math.Abs(ch9.Output - ch9.HighPwm) < 2, 10), "toggle");
            await ch9.RepeatCommand.ExecuteAsync(null);
            Assert.IsFalse(vm.ActionStatusIsError, "DO_REPEAT_SERVO: " + vm.ActionStatus);
            log.Add("repeat: " + vm.ActionStatus);
            await vm.ServoRelay.Relays[0].OnCommand.ExecuteAsync(null);
            log.Add("relay 1 on: " + vm.ActionStatus);

            // Aux Function: camera trigger (9), high
            vm.SelectedTabIndex = (int)FlightDataTab.AuxFunction;
            Assert.IsTrue(await Until(vm, () => vm.AuxFunctions.Rows.Count > 0, 20));
            var camera = vm.AuxFunctions.Rows.Single(r => r.Id == 9);
            await camera.HighCommand.ExecuteAsync(null);
            Assert.IsFalse(vm.ActionStatusIsError, "DO_AUX_FUNCTION: " + vm.ActionStatus);
            Assert.IsTrue(camera.IsHigh);
            log.Add($"aux {camera.Name}: {vm.ActionStatus}");

            // ADS-B traffic on the map
            var traffic = await Until(vm, () => vm.MapModel.Traffic.Count > 0, 30);
            log.Add($"traffic: {vm.MapModel.Traffic.Count} " +
                    string.Join(", ", vm.MapModel.Traffic.Select(t => $"{t.Label} {t.AltitudeM:0}m hdg {t.HeadingDeg:0}")));

            log.Add(string.Join(" ", services.Params.Snapshot().Where(p => p.Name.StartsWith("SIM_ADSB") || p.Name.Contains("ADSB") || p.Name.StartsWith("SERIAL") && p.Name.EndsWith("_PROTOCOL"))
                .Select(p => $"{p.Name}={p.Value}")));

            // land (unless asked to keep flying, e.g. for screenshots of a second client on SERIAL2)
            if (Environment.GetEnvironmentVariable("MP_TEST_SITL_KEEP_FLYING") != "1")
            {
                vm.SelectedMode = FlightModes.Find(vm.Modes, "Land");
                await vm.SetModeCommand.ExecuteAsync(null);
                log.Add("mode: " + cs.mode);
            }

            foreach (var line in log)
                Console.WriteLine(line);
            Assert.IsTrue(traffic, "ADS-B traffic from SIM_ADSB_COUNT (may need a SITL restart after setting ADSB_TYPE)");
            vm.OnHidden();
        }
        finally
        {
            await services.Session.DisconnectAsync();
        }
    }
}
