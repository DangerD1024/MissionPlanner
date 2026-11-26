using System.Net;
using System.Net.Sockets;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.ViewModels.SetupScreens;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Calibration;
using MissionPlanner.App.Tests.FlightData;

namespace MissionPlanner.App.Tests.SetupScreens;

/// <summary>A failed calibration's message belongs to the link it happened on: connecting again clears it.</summary>
[TestClass]
public class CalibrationReconnectTests
{
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task ACalibrationFailureIsGoneAfterReconnecting()
    {
        var services = FakeUi.Services(out _);
        var link = new FakeCalibrationLink { Answer = _ => false };
        var accel = new AccelCalibrationViewModel(services, link);
        var compass = new CompassViewModel(services, new FakeCalibrationLink());

        var first = await ConnectAsync(services);
        try
        {
            await accel.CalibrateLevelCommand.ExecuteAsync(null);
            await accel.SimpleAccelCommand.ExecuteAsync(null);
            Assert.IsTrue(accel.LevelStatus.IsError, accel.LevelStatus.Text);
            Assert.IsTrue(accel.SimpleStatus.IsError, accel.SimpleStatus.Text);
            compass.MagStatus.Set(StatusKind.Error, "Command failed.");
        }
        finally
        {
            await services.Session.DisconnectAsync();
            await first.DisposeAsync();
        }

        Assert.IsTrue(accel.LevelStatus.IsVisible, "still shown while disconnected");

        var second = await ConnectAsync(services);
        try
        {
            Assert.IsFalse(accel.LevelStatus.IsVisible, "Calibrate Level: " + accel.LevelStatus.Text);
            Assert.IsFalse(accel.SimpleStatus.IsVisible, "Simple Accel Cal: " + accel.SimpleStatus.Text);
            Assert.AreEqual("Calibrate Level", accel.LevelButtonText);
            Assert.IsFalse(compass.MagStatus.IsVisible, "Compass: " + compass.MagStatus.Text);
        }
        finally
        {
            await services.Session.DisconnectAsync();
            await second.DisposeAsync();
        }
    }

    private static async Task<ExtrasFakeVehicle> ConnectAsync(AppServices services)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync();
        var connecting = services.Session.ConnectAsync(LinkSpec.Tcp("127.0.0.1", port), getParams: false);
        var client = await accept;
        listener.Stop();
        var vehicle = new ExtrasFakeVehicle(client.GetStream());
        await connecting;
        return vehicle;
    }
}
