using System.Net;
using System.Net.Sockets;
using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.Session;
using MissionPlanner.App.Core.ViewModels;
using MissionPlanner.App.Core.ViewModels.Mobile;
using MissionPlanner.App.Tests.FlightData;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Tests.Mobile;

[TestClass]
public class MobileTests
{
    [TestCleanup]
    public void Cleanup()
    {
        Settings.Instance.Remove(MobileLinkViewModel.RecentKey);
        Settings.Instance.Remove(MobileLinkViewModel.AutoKey);
        Settings.Instance.Remove("next_mobile_hud_large");
        Settings.Instance.Remove("next_mobile_map_main");
        Settings.Instance.Remove(MobileShellViewModel.OrientationKey);
    }

    [TestMethod]
    public void ReadinessReadsLikeTheDjiStatusLine()
    {
        Assert.AreEqual(("Link lost", StatusLevel.Bad), MobileStatus.Readiness(true, true, false, linkLost: true, 10, 0));
        Assert.AreEqual(("Failsafe", StatusLevel.Bad), MobileStatus.Readiness(true, true, failsafe: true, false, 10, 0));
        Assert.AreEqual(("Ready to arm", StatusLevel.Ok), MobileStatus.Readiness(false, readyToArm: true, false, false, 0, 0));
        Assert.AreEqual(("Not ready to arm", StatusLevel.Warning), MobileStatus.Readiness(false, false, false, false, 0, 0));
        Assert.AreEqual(("Armed", StatusLevel.Ok), MobileStatus.Readiness(true, true, false, false, 0.1, 0));
        Assert.AreEqual(("Flying", StatusLevel.Ok), MobileStatus.Readiness(true, true, false, false, 12, 0));
        // the vehicle's landed state wins over the height guess
        Assert.AreEqual(("Armed", StatusLevel.Ok), MobileStatus.Readiness(true, true, false, false, 12, 0, landedState: 1));
        Assert.AreEqual(("Taking off", StatusLevel.Ok), MobileStatus.Readiness(true, true, false, false, 0, 0, landedState: 3));
        Assert.AreEqual(("Landing", StatusLevel.Ok), MobileStatus.Readiness(true, true, false, false, 3, 0, landedState: 4));
    }

    [TestMethod]
    public void NumbersNeverReadMinusZero()
    {
        Assert.AreEqual("0.0", MobileStatus.Num(-0.01, "0.0"));
        Assert.AreEqual("0", MobileStatus.Num(-0.2, "0"));
        Assert.AreEqual("-0.1", MobileStatus.Num(-0.06, "0.0"));
        Assert.AreEqual("12.3", MobileStatus.Num(12.34, "0.0"));
        Assert.AreEqual("3D", MobileStatus.GpsFix(3));
        Assert.AreEqual("RTK", MobileStatus.GpsFix(6));
        Assert.AreEqual("No GPS", MobileStatus.GpsFix(0));
    }

    [TestMethod]
    public void StatusWithoutALinkIsBlank()
    {
        var services = FakeUi.Services(out _);
        var status = new MobileStatus();
        status.Update(services.Session);
        Assert.IsFalse(status.IsConnected);
        Assert.AreEqual("No link", status.LinkText);
        Assert.AreEqual("Not connected", status.ReadyText);
        Assert.AreEqual("—", status.ModeText);
        Assert.AreEqual("DISARMED", status.ArmText);
    }

    [TestMethod]
    public void LinkFieldsBuildTheLink()
    {
        var vm = new MobileLinkViewModel(FakeUi.Services(out _));
        Assert.AreEqual(LinkKind.UdpListen, vm.SelectedKind.Kind);
        Assert.IsFalse(vm.NeedsHost);
        Assert.AreEqual(LinkSpec.UdpListen(14550), vm.BuildSpec());

        vm.SelectedKind = vm.Kinds.Single(k => k.Kind == LinkKind.Tcp);
        Assert.AreEqual("5760", vm.Port); // the TCP default follows the choice
        vm.Host = " 10.0.0.2 ";
        Assert.AreEqual(LinkSpec.Tcp("10.0.0.2", 5760), vm.BuildSpec());

        vm.Port = "70000";
        Assert.IsNull(vm.BuildSpec());
        StringAssert.Contains(vm.Error, "not a port number");
        vm.Port = "5760";
        vm.Host = "";
        Assert.IsNull(vm.BuildSpec());
        StringAssert.Contains(vm.Error, "host");

        vm.SelectedKind = vm.Kinds.Single(k => k.Kind == LinkKind.UdpConnect);
        vm.Host = "192.168.4.1";
        Assert.AreEqual("14550", vm.Port);
        Assert.AreEqual(LinkSpec.UdpConnect("192.168.4.1", 14550), vm.BuildSpec());
        Assert.AreEqual("", vm.Error);
        Assert.AreEqual("ArduCopter", MobileLinkViewModel.FirmwareName(MissionPlanner.ArduPilot.Firmwares.ArduCopter2));
        Assert.AreEqual("ArduPlane", MobileLinkViewModel.FirmwareName(MissionPlanner.ArduPilot.Firmwares.ArduPlane));
    }

    [TestMethod]
    public void RecentLinksAreRestored()
    {
        Settings.Instance[MobileLinkViewModel.RecentKey] = "tcp:10.1.1.1:5763|udp:14551|bad:entry:x";
        var vm = new MobileLinkViewModel(FakeUi.Services(out _));
        CollectionAssert.AreEqual(new[] { LinkSpec.Tcp("10.1.1.1", 5763), LinkSpec.UdpListen(14551) }, vm.Recent.Take(2).ToArray());
        // the newest is put in the fields
        Assert.AreEqual(LinkKind.Tcp, vm.SelectedKind.Kind);
        Assert.AreEqual("10.1.1.1", vm.Host);
        Assert.AreEqual("5763", vm.Port);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task ConnectButtonCancelsWhileConnecting()
    {
        // a TCP server that accepts and never says anything: the connect waits for a heartbeat
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = listener.AcceptTcpClientAsync();
        try
        {
            var services = FakeUi.Services(out _);
            var vm = new MobileLinkViewModel(services);
            vm.SelectedKind = vm.Kinds.Single(k => k.Kind == LinkKind.Tcp);
            vm.Host = "127.0.0.1";
            vm.Port = port.ToString(System.Globalization.CultureInfo.InvariantCulture);

            var connecting = vm.ToggleCommand.ExecuteAsync(null);
            for (var i = 0; i < 100 && services.Session.State != LinkState.Connecting; i++)
                await Task.Delay(50);
            Assert.AreEqual(LinkState.Connecting, services.Session.State);
            Assert.AreEqual("Cancel", vm.ButtonText);
            // the bug: the button went grey while its own command ran
            Assert.IsTrue(vm.ToggleCommand.CanExecute(null), "Cancel must stay enabled while connecting");

            await vm.ToggleCommand.ExecuteAsync(null);
            var done = await Task.WhenAny(connecting, Task.Delay(15000));
            Assert.AreSame(connecting, done, "the connect did not stop when cancelled");
            Assert.AreEqual(LinkState.Disconnected, services.Session.State);
            Assert.AreEqual("Connect", vm.ButtonText);
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestMethod]
    public void DesktopConnectButtonStaysEnabledWhileConnecting()
    {
        var vm = new ConnectionViewModel(FakeUi.Services(out _));
        var attr = typeof(ConnectionViewModel).GetMethod("ToggleConnectAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetCustomAttributes(typeof(CommunityToolkit.Mvvm.Input.RelayCommandAttribute), false)
            .Cast<CommunityToolkit.Mvvm.Input.RelayCommandAttribute>().Single();
        Assert.IsTrue(attr.AllowConcurrentExecutions, "the Connect button turns into Cancel while connecting and must stay enabled");
        Assert.IsTrue(vm.ToggleConnectCommand.CanExecute(null));
    }

    [TestMethod]
    public void ParamSearchMatchesEveryWordInNameOrTitle()
    {
        var all = new[]
        {
            new MobileParam("BATT_CAPACITY", 3300, null),
            new MobileParam("BATT_MONITOR", 4, null),
            new MobileParam("FS_THR_ENABLE", 1, null),
        };
        CollectionAssert.AreEqual(new[] { "BATT_CAPACITY", "BATT_MONITOR" }, MobileParamsViewModel.Filter(all, "batt").Select(p => p.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "BATT_MONITOR" }, MobileParamsViewModel.Filter(all, "batt mon").Select(p => p.Name).ToArray());
        Assert.AreEqual(3, MobileParamsViewModel.Filter(all, "  ").Count());
        Assert.AreEqual("3300", all[0].ValueText);
    }

    [TestMethod]
    public void SetupListNeedsAVehicleAndGoesBack()
    {
        var services = FakeUi.Services(out _);
        var setup = new MobileSetupViewModel(services, new MobileParamsViewModel(services));
        CollectionAssert.AreEqual(new[] { "Accelerometer", "Compass", "Radio", "Sensors", "Flight modes", "Failsafe", "Battery", "Frame", "Motor test", "Parameters" },
            setup.Entries.Select(e => e.Title).ToArray());
        Assert.IsTrue(setup.Entries.All(e => !e.IsAvailable));
        Assert.AreEqual("Connect to a vehicle to set it up.", setup.Hint);
        string? notified = null;
        services.Notified += (_, n) => notified = n.Text;
        setup.OpenCommand.Execute(setup.Entries[0]);
        Assert.IsTrue(setup.IsList);
        Assert.AreEqual("Connect to a vehicle first.", notified);
        Assert.IsFalse(setup.Back());
        Assert.AreEqual("Setup", setup.Title);
    }

    [TestMethod]
    public void ShellTabsAndOfflineActions()
    {
        var services = FakeUi.Services(out _);
        var shell = new MobileShellViewModel(services);
        Assert.IsTrue(shell.IsFly);
        shell.SelectTabCommand.Execute(MobileTab.Setup);
        Assert.IsTrue(shell.IsSetup);
        shell.SelectTabCommand.Execute(MobileTab.Messages);
        Assert.IsTrue(shell.IsMessages);
        Assert.AreEqual(0, shell.UnreadMessages);
        shell.Tick();

        // not connected: a tap on the map offers nothing, the mode sheet does not open
        shell.MapTapped(-35, 149);
        Assert.IsFalse(shell.HasGoHere);
        shell.ToggleModePickerCommand.Execute(null);
        Assert.IsFalse(shell.IsModePickerOpen);
        Assert.IsTrue(shell.IsToastVisible);

        // landscape: swapping puts the map big and is remembered; the orientation choice is saved
        Assert.IsFalse(shell.IsMapMain);
        shell.SwapViewsCommand.Execute(null);
        Assert.IsTrue(shell.IsMapMain);
        Assert.AreEqual("True", Settings.Instance["next_mobile_map_main"]);
        CollectionAssert.AreEqual(new[] { ScreenOrientationMode.Auto, ScreenOrientationMode.Portrait, ScreenOrientationMode.Landscape },
            shell.Orientations.ToArray());
        Assert.AreEqual(ScreenOrientationMode.Auto, shell.Orientation);
        shell.Orientation = ScreenOrientationMode.Landscape;
        Assert.AreEqual("Landscape", Settings.Instance[MobileShellViewModel.OrientationKey]);
        Assert.AreEqual(ScreenOrientationMode.Landscape, new MobileShellViewModel(services).Orientation);

        Assert.IsTrue(shell.IsHudLarge);
        shell.ToggleHudSizeCommand.Execute(null);
        Assert.IsFalse(shell.IsHudLarge);
        Assert.AreEqual("False", Settings.Instance["next_mobile_hud_large"]);
    }
}
