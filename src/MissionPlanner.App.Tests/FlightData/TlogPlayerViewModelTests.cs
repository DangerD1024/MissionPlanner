using System.Net;
using System.Net.Sockets;
using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.ViewModels.FlightData;

namespace MissionPlanner.App.Tests.FlightData;

[TestClass]
public class TlogPlayerViewModelTests
{
    [TestMethod]
    public async Task LoadPlaysToTheEndAndShowsWhereItIs()
    {
        var services = TlogTestUi.Services(out var ui);
        var clock = new FakeTlogClock { BlockAfter = 10 };
        var vm = new TlogPlayerViewModel(services, clock);
        var path = TlogWriter.CreateFlight(TlogWriter.TempPath("vm"), 40);

        await vm.LoadFileAsync(path);
        vm.Tick();

        Assert.AreEqual(0, ui.Messages.Count, string.Join("; ", ui.Messages));
        Assert.IsTrue(vm.IsLoaded);
        Assert.IsTrue(vm.IsPlaying, "playback starts after Load Log, as in Mission Planner");
        Assert.AreEqual("Pause", vm.PlayPauseText);
        Assert.AreEqual(Path.GetFileName(path), vm.FileName);
        Assert.AreEqual("00:04", vm.DurationText);
        StringAssert.Contains(vm.DetailsText, "sysid 7");
        Assert.IsTrue(vm.Services.Session.Mav.logreadmode);

        await TlogTestExtensions.WaitUntilAsync(() => clock.Delays > 10);
        vm.Tick();
        Assert.AreEqual("00:01", vm.ElapsedText, "ten 100 ms waits into the log");
        Assert.AreEqual(TlogWriter.T0.AddSeconds(1).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), vm.LogClockText);
        Assert.AreEqual(11, vm.Services.Session.Cs.roll, 0.01, "the rest of the app reads the log through Session.Cs");

        vm.PlayPauseCommand.Execute(null);
        vm.Tick();
        Assert.IsFalse(vm.IsPlaying);
        Assert.AreEqual("Play", vm.PlayPauseText);

        clock.BlockAfter = int.MaxValue;
        vm.PlayPauseCommand.Execute(null);
        await vm.Engine.WaitForLoopAsync();
        vm.Tick();

        Assert.IsFalse(vm.IsPlaying);
        Assert.IsTrue(vm.IsLoaded, "the log stays open at the end, to replay or seek");
        Assert.AreEqual("100.00 %", vm.PercentText);
        Assert.AreEqual(100, vm.PositionPercent, 1e-9);
        Assert.AreEqual("00:04", vm.ElapsedText);
        StringAssert.Contains(vm.StatusText, "End of log");

        vm.StopCommand.Execute(null);
        vm.Tick();
        Assert.IsFalse(vm.IsLoaded);
        Assert.AreEqual("", vm.FileName);
        Assert.AreEqual("--:--", vm.ElapsedText);
        Assert.IsNull(vm.Services.Session.Mav.logplaybackfile);
    }

    [TestMethod]
    public async Task MovingTheSliderSeeks()
    {
        var services = TlogTestUi.Services(out _);
        var clock = new FakeTlogClock { BlockAfter = 0 };
        var vm = new TlogPlayerViewModel(services, clock);
        await vm.LoadFileAsync(TlogWriter.CreateFlight(TlogWriter.TempPath("slider"), 100));
        vm.PlayPauseCommand.Execute(null); // pause
        vm.Tick();

        vm.PositionPercent = 75; // as the slider binding does

        Assert.AreEqual(0.75, vm.Engine.Position, 0.02);
        vm.Tick();
        Assert.AreEqual(75, vm.PositionPercent, 2);
        Assert.AreEqual("00:07", vm.ElapsedText, "estimated from the record the seek landed on");
        vm.StopCommand.Execute(null);
    }

    [TestMethod]
    public async Task ABadFileIsReportedAndNotLoaded()
    {
        var services = TlogTestUi.Services(out var ui);
        var vm = new TlogPlayerViewModel(services, new FakeTlogClock());
        var path = TlogWriter.TempPath("bad");
        await File.WriteAllTextAsync(path, "this is not a telemetry log");

        ui.FileToOpen = path;
        await vm.LoadLogCommand.ExecuteAsync(null);
        vm.Tick();

        Assert.IsFalse(vm.IsLoaded);
        Assert.AreEqual(1, ui.Messages.Count);
        StringAssert.Contains(ui.Messages[0], "valid telemetry log");
        Assert.IsTrue(vm.StatusIsError);
        Assert.IsFalse(services.Session.Mav.logreadmode);
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task ConnectingStopsPlaybackAndALogCannotBeLoadedWhileConnected()
    {
        var services = TlogTestUi.Services(out var ui);
        var clock = new FakeTlogClock { BlockAfter = 5 };
        var vm = new TlogPlayerViewModel(services, clock);
        var path = TlogWriter.CreateFlight(TlogWriter.TempPath("connect"), 100);
        await vm.LoadFileAsync(path);
        await TlogTestExtensions.WaitUntilAsync(() => clock.Delays > 5);
        Assert.AreEqual(TlogPlaybackState.Playing, vm.Engine.State);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync();

        // Connecting is raised before the link opens: playback must have let go by then
        var stoppedBeforeOpen = false;
        services.Session.StateChanged += (_, state) =>
        {
            if (state == Core.Session.LinkState.Connecting)
                stoppedBeforeOpen = vm.Engine.State == TlogPlaybackState.Unloaded && !services.Session.Mav.logreadmode;
        };

        var connecting = services.Session.ConnectAsync(LinkSpec.Tcp("127.0.0.1", port), getParams: false);
        using var vehicleSide = await accept;
        listener.Stop();
        await using var vehicle = new FakeVehicle(vehicleSide.GetStream(), rollDegrees: 12.5f);
        await connecting;

        try
        {
            Assert.IsTrue(stoppedBeforeOpen, "playback stopped synchronously when the connect started");
            Assert.AreEqual(TlogPlaybackState.Unloaded, vm.Engine.State);
            Assert.IsNull(services.Session.Mav.logplaybackfile);
            Assert.IsTrue(services.Session.IsConnected);
            vm.Tick();
            Assert.IsFalse(vm.IsLoaded);
            StringAssert.Contains(vm.StatusText, "connecting");

            // the live vehicle, not the log, is on screen
            await TlogTestExtensions.WaitUntilAsync(() => Math.Abs(services.Session.Cs.roll - 12.5) < 0.1);
            Assert.AreEqual((uint)FakeVehicle.SysId, services.Session.Vehicle.sysid);

            // and a log can't be started over the live link
            await vm.LoadFileAsync(path);
            Assert.AreEqual(TlogPlaybackState.Unloaded, vm.Engine.State);
            Assert.IsFalse(services.Session.Mav.logreadmode);
            Assert.IsTrue(ui.Messages.Any(m => m.Contains("Disconnect", StringComparison.Ordinal)));

            ui.FileToOpen = path;
            await vm.LoadLogCommand.ExecuteAsync(null);
            Assert.AreEqual(TlogPlaybackState.Unloaded, vm.Engine.State);
        }
        finally
        {
            await services.Session.DisconnectAsync();
        }
    }
}
