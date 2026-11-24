using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.Session;
using MissionPlanner.App.Core.ViewModels;
using MissionPlanner.App.Core.ViewModels.FlightData;
using MissionPlanner.App.Core.ViewModels.Tools;
using MissionPlanner.App.Tests.FlightData;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Tests;

/// <summary>The connection bar's vehicle list during log playback, UDP listen cancel/timeout, and the Ctrl+F layouts.</summary>
[TestClass]
public class ConnectionBarTests
{
    [TestCleanup]
    public void Cleanup() => Settings.Instance.Remove(ToolsViewModel.ClassicKey);

    [TestMethod]
    public void PlaybackListsTheLogsVehicles()
    {
        var services = FakeUi.Services(out _);
        var vm = new ConnectionViewModel(services);
        vm.Tick();
        Assert.IsFalse(vm.ShowVehicles);
        Assert.IsTrue(vm.ShowAutoConnect);

        using var engine = new TlogPlaybackEngine(services.Session.Mav, new FakeTlogClock());
        engine.Load(TlogWriter.CreateFlight(TlogWriter.TempPath("bar"), 5));
        vm.Tick();
        Assert.IsTrue(vm.IsReplaying);
        Assert.IsTrue(vm.ShowVehicles, "the vehicle list shows during playback, as in Mission Planner");
        Assert.IsFalse(vm.ShowAutoConnect, "auto-connect shares the row and steps aside");
        Assert.IsTrue(vm.Vehicles.Any(v => v.SysId == TlogWriter.VehicleSysId), string.Join(", ", vm.Vehicles.Select(v => v.ToString())));
        Assert.AreEqual(TlogWriter.VehicleSysId, vm.SelectedVehicle!.SysId);

        engine.Stop();
        vm.Tick();
        Assert.IsFalse(vm.IsReplaying);
        Assert.IsFalse(vm.ShowVehicles);
        Assert.AreEqual(0, vm.Vehicles.Count);
    }

    private static int FreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task CancelStopsAUdpListenThatHearsNothing()
    {
        await using var session = new VehicleSession();
        using var cancel = new CancellationTokenSource();
        var connect = session.ConnectAsync(LinkSpec.Parse("udp:" + FreeUdpPort()), getParams: false, cancel: cancel.Token);
        await Task.Delay(700);
        Assert.AreEqual(LinkState.Connecting, session.State);

        var sw = Stopwatch.StartNew();
        await cancel.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => connect);
        Assert.IsTrue(sw.ElapsedMilliseconds < 5000, $"cancel took {sw.ElapsedMilliseconds} ms");
        Assert.AreEqual(LinkState.Disconnected, session.State);
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task SilentUdpPortGivesUpWithAnExplanation()
    {
        var saved = VehicleSession.UdpFirstPacketTimeout;
        VehicleSession.UdpFirstPacketTimeout = TimeSpan.FromSeconds(1.5);
        try
        {
            await using var session = new VehicleSession();
            var port = FreeUdpPort();
            var ex = await Assert.ThrowsAsync<ConnectException>(() => session.ConnectAsync(LinkSpec.Parse("udp:" + port), getParams: false));
            StringAssert.Contains(ex.Message, $"UDP port {port}");
            StringAssert.Contains(ex.Message, "Firewall");
            Assert.AreEqual(LinkState.Disconnected, session.State);
        }
        finally
        {
            VehicleSession.UdpFirstPacketTimeout = saved;
        }
    }

    [TestMethod]
    public void ToolsScreenSwitchesToClassicButtons()
    {
        var services = FakeUi.Services(out _);
        var tools = new ToolsViewModel(services);
        tools.OnShown();
        Assert.IsFalse(tools.ClassicLayout, "the structured menu is the default");
        Assert.IsTrue(tools.ShowMenu);
        Assert.IsNotNull(tools.Content, "the menu opens its first page");

        tools.ClassicLayout = true;
        Assert.IsTrue(Settings.Instance.GetBoolean(ToolsViewModel.ClassicKey));
        Assert.IsTrue(tools.ShowButtonWall);
        Assert.IsNull(tools.Content, "no page behind the buttons");
        var titles = tools.ClassicButtons.Select(b => b.Title).ToList();
        CollectionAssert.IsSubsetOf(new[] { "Force Accel Cal", "arm and takeoff", "NMEA", "Age Map Data", "APJ Tool", "reboot pixhawk" }, titles,
            "Mission Planner's own button names");
        Assert.IsTrue(tools.ClassicButtons.All(b => b.Description.Length > 0), "every button says what it does");
        Assert.AreEqual(titles.Count, titles.Distinct().Count());

        var outputs = tools.ClassicButtons.Single(b => b.Title == "NMEA");
        outputs.Command.Execute(outputs.Parameter);
        Assert.IsTrue(tools.ShowClassicPage);
        Assert.AreEqual(ToolsViewModel.OutputsTitle, tools.Content!.Title);

        tools.BackToButtonsCommand.Execute(null);
        Assert.IsTrue(tools.ShowButtonWall);
        Assert.IsNull(tools.Content);

        // a hotkey opens its page in the classic layout too
        Assert.IsTrue(tools.Show(ToolsViewModel.MapCacheTitle));
        Assert.IsTrue(tools.ShowClassicPage);

        // the choice is remembered
        var again = new ToolsViewModel(services);
        again.OnShown();
        Assert.IsTrue(again.ShowButtonWall);
        Assert.IsNull(again.Content);

        again.ClassicLayout = false;
        Assert.IsTrue(again.ShowMenu);
        Assert.IsNotNull(again.Content);
    }
}
