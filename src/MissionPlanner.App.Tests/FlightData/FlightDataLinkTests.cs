using System.Net;
using System.Net.Sockets;
using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.Maps;
using MissionPlanner.App.Core.ViewModels.FlightData;

namespace MissionPlanner.App.Tests.FlightData;

/// <summary>Flight Data against the fake vehicle over TCP loopback: telemetry in, commands out.</summary>
[TestClass]
public class FlightDataLinkTests
{
    [ClassInitialize]
    public static void Init(TestContext _) =>
        MapTiles.Initialize(Path.Combine(TestEnvironment.DataRoot, "gmapcache") + Path.DirectorySeparatorChar);

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task ConnectedPageShowsTelemetryAndSendsActions()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync();

        var services = FakeUi.Services(out var ui);
        var connecting = services.Session.ConnectAsync(LinkSpec.Tcp("127.0.0.1", port), getParams: false);
        using var vehicleSide = await accept;
        listener.Stop();
        await using var vehicle = new FakeVehicle(vehicleSide.GetStream(), rollDegrees: 12.5f);
        await connecting;

        try
        {
            var vm = new FlightDataViewModel(services);
            vm.OnShown();

            // the reader task decodes ATTITUDE; the page copies it on its tick
            var until = DateTime.UtcNow.AddSeconds(10);
            while (Math.Abs(vm.Hud.Roll - 12.5) > 0.1 && DateTime.UtcNow < until)
            {
                vm.Tick();
                await Task.Delay(50);
            }

            Assert.AreEqual(12.5, vm.Hud.Roll, 0.1);
            Assert.IsTrue(vm.Hud.Connected);
            Assert.IsTrue(vm.IsConnected);
            Assert.IsTrue(vm.CanCommand);

            // System_Time is a plain message: confirm, send, report
            vm.SelectedDoAction = FlightActions.Options.Single(o => o.Action == FlightAction.System_Time);
            await vm.DoActionCommand.ExecuteAsync(null);
            Assert.Contains("Action", ui.Confirms);
            StringAssert.Contains(vm.ActionStatus, "done");
            Assert.IsFalse(vm.ActionStatusIsError);
            Assert.IsFalse(vm.IsCommandRunning);

            until = DateTime.UtcNow.AddSeconds(5);
            while (!vehicle.Received.Any(m => m.msgid == (uint)MAVLink.MAVLINK_MSG_ID.SYSTEM_TIME) && DateTime.UtcNow < until)
                await Task.Delay(50);
            Assert.IsTrue(vehicle.Received.Any(m => m.msgid == (uint)MAVLink.MAVLINK_MSG_ID.SYSTEM_TIME), "SYSTEM_TIME reached the vehicle");

            // declining the confirmation sends nothing
            ui.ConfirmAnswer = false;
            var before = vehicle.Received.Count(m => m.msgid == (uint)MAVLink.MAVLINK_MSG_ID.SYSTEM_TIME);
            await vm.DoActionCommand.ExecuteAsync(null);
            await Task.Delay(300);
            Assert.AreEqual(before, vehicle.Received.Count(m => m.msgid == (uint)MAVLink.MAVLINK_MSG_ID.SYSTEM_TIME));

            vm.OnHidden();
        }
        finally
        {
            await services.Session.DisconnectAsync();
        }
    }
}
