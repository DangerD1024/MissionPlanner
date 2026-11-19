using System.Net;
using System.Net.Sockets;
using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.Maps;
using MissionPlanner.App.Core.ViewModels.FlightData;

namespace MissionPlanner.App.Tests.FlightData;

/// <summary>The extra tabs against a scripted vehicle over TCP: commands out, acknowledgements, servo output, traffic, transponder.</summary>
[TestClass]
public class FlightDataExtrasLinkTests
{
    [ClassInitialize]
    public static void Init(TestContext _) =>
        MapTiles.Initialize(Path.Combine(TestEnvironment.DataRoot, "gmapcache") + Path.DirectorySeparatorChar);

    private static async Task Until(FlightDataViewModel vm, Func<bool> done, int seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!done() && DateTime.UtcNow < until)
        {
            vm.Tick();
            await Task.Delay(50);
        }

        vm.Tick();
    }

    [TestMethod]
    [Timeout(90000, CooperativeCancellation = true)]
    public async Task ServoAuxTrafficAndTransponderAgainstAVehicle()
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
            var vm = new FlightDataViewModel(services);
            vm.OnShown();
            vm.SelectedTabIndex = (int)FlightDataTab.Servo;
            await Until(vm, () => vm.IsConnected && vm.ServoRelay.Servos.Single(s => s.Channel == 9).Output > 0);

            // Servo/Relay: High on output 9 sends DO_SET_SERVO and the output follows
            var ch9 = vm.ServoRelay.Servos.Single(s => s.Channel == 9);
            Assert.AreEqual(1500, ch9.Output);
            await ch9.SetHighCommand.ExecuteAsync(null);
            StringAssert.Contains(vm.ActionStatus, "1900");
            Assert.IsFalse(vm.ActionStatusIsError, vm.ActionStatus);
            var servo = vehicle.Commands.Last(c => c.command == (ushort)MAVLink.MAV_CMD.DO_SET_SERVO);
            Assert.AreEqual((9f, 1900f), (servo.param1, servo.param2));
            await Until(vm, () => ch9.Output == 1900);
            Assert.AreEqual("High", ch9.Position);

            // relay on: DO_SET_RELAY, state remembered from the accepted command
            await vm.ServoRelay.Relays[0].OnCommand.ExecuteAsync(null);
            var relay = vehicle.Commands.Last(c => c.command == (ushort)MAVLink.MAV_CMD.DO_SET_RELAY);
            Assert.AreEqual((0f, 1f), (relay.param1, relay.param2));
            Assert.IsTrue(vm.ServoRelay.Relays[0].IsOn);

            // Aux Function: High sends DO_AUX_FUNCTION with switch position 2
            vm.SelectedTabIndex = (int)FlightDataTab.AuxFunction;
            await Until(vm, () => vm.AuxFunctions.Rows.Count > 0, 20);
            var row = vm.AuxFunctions.Rows.FirstOrDefault(r => r.Id == 41) ?? vm.AuxFunctions.Rows[0];
            await row.HighCommand.ExecuteAsync(null);
            var aux = vehicle.Commands.Last(c => c.command == (ushort)MAVLink.MAV_CMD.DO_AUX_FUNCTION);
            Assert.AreEqual(((float)row.Id, 2f), (aux.param1, aux.param2));
            Assert.IsTrue(row.IsHigh);

            // ADS-B traffic reaches the map model
            await Until(vm, () => vm.MapModel.Traffic.Count == 2);
            Assert.HasCount(2, vm.MapModel.Traffic);
            var qfa = vm.MapModel.Traffic.Single(t => t.Callsign == "QFA12");
            Assert.AreEqual(1200, qfa.AltitudeM, 0.01);
            Assert.AreEqual(90, qfa.HeadingDeg, 0.01);
            Assert.IsFalse(qfa.IsStale);
            Assert.AreEqual("7C1234", vm.MapModel.Traffic.Single(t => t.Callsign == "").Label, "no callsign: ICAO address");

            // Transponder: status shows, ALT sends UAVIONIX_ADSB_OUT_CONTROL with all modes
            vm.SelectedTabIndex = (int)FlightDataTab.Transponder;
            await Until(vm, () => vm.Transponder.Link == TransponderLink.Connected);
            Assert.AreEqual(TransponderLink.Connected, vm.Transponder.Link);
            Assert.AreEqual("ON", vm.Transponder.ModeName);
            Assert.AreEqual(1200m, vm.Transponder.Squawk);
            Assert.AreEqual("FAKE01", vm.Transponder.FlightId);
            vm.Transponder.AltCommand.Execute(null);
            var until = DateTime.UtcNow.AddSeconds(5);
            while (!vehicle.Received.Any(m => m.msgid == (uint)MAVLink.MAVLINK_MSG_ID.UAVIONIX_ADSB_OUT_CONTROL) && DateTime.UtcNow < until)
                await Task.Delay(50);
            var control = vehicle.Received.Last(m => m.msgid == (uint)MAVLink.MAVLINK_MSG_ID.UAVIONIX_ADSB_OUT_CONTROL);
            var ctrl = (MAVLink.mavlink_uavionix_adsb_out_control_t)control.data;
            Assert.AreEqual(16 | 32 | 64 | 128, ctrl.state);
            Assert.AreEqual(1200, ctrl.squawk);

            // the transponder goes quiet: the tab says so after 5 s
            vehicle.TransponderPresent = false;
            await Until(vm, () => vm.Transponder.Link == TransponderLink.Lost, 12);
            Assert.AreEqual(TransponderLink.Lost, vm.Transponder.Link);

            vm.OnHidden();
        }
        finally
        {
            await services.Session.DisconnectAsync();
        }
    }
}
