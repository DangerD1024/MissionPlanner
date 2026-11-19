using MissionPlanner.App.Core.Maps;
using MissionPlanner.App.Core.ViewModels;
using MissionPlanner.App.Core.ViewModels.FlightData;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Tests.FlightData;

/// <summary>The HUD in its own window: remembered, and kept up to date while another screen is shown.</summary>
[TestClass]
public class HudWindowTests
{
    [ClassInitialize]
    public static void Init(TestContext _) =>
        MapTiles.Initialize(Path.Combine(TestEnvironment.DataRoot, "gmapcache") + Path.DirectorySeparatorChar);

    [TestCleanup]
    public void Cleanup()
    {
        Settings.Instance.Remove(FlightDataViewModel.HudDetachedKey);
        Settings.Instance.Remove(FlightDataViewModel.HudWindowKey);
    }

    [TestMethod]
    public void DetachingIsRememberedForTheNextStart()
    {
        var services = FakeUi.Services(out _);
        var vm = new FlightDataViewModel(services);
        Assert.IsFalse(vm.IsHudDetached);
        Assert.IsFalse(vm.NeedsBackgroundTick);

        vm.DetachHudCommand.Execute(null);
        Assert.IsTrue(vm.IsHudDetached);
        Assert.IsTrue(vm.NeedsBackgroundTick);
        vm.HudWindowPlacement = "100,200,800,600,1";

        var next = new FlightDataViewModel(services);
        Assert.IsTrue(next.IsHudDetached, "opens detached again");
        Assert.IsTrue(next.NeedsBackgroundTick);
        Assert.AreEqual("100,200,800,600,1", next.HudWindowPlacement);

        next.AttachHudCommand.Execute(null);
        Assert.IsFalse(next.NeedsBackgroundTick);
        Assert.IsFalse(new FlightDataViewModel(services).IsHudDetached);
    }

    [TestMethod]
    public void BackgroundUsersAreCountedSeparately()
    {
        var vm = new FlightDataViewModel(FakeUi.Services(out _));
        vm.UseInBackground("3d", true);
        vm.UseInBackground("stream", true);
        vm.UseInBackground("3d", false);
        Assert.IsTrue(vm.NeedsBackgroundTick, "the stream still needs it");
        vm.UseInBackground("stream", false);
        Assert.IsFalse(vm.NeedsBackgroundTick);
    }

    [TestMethod]
    public void TheShellKeepsADetachedHudUpdatedOnOtherScreens()
    {
        var services = FakeUi.Services(out _);
        var shell = new ShellViewModel(services);
        var flight = (FlightDataViewModel)shell.CurrentPage;
        var updates = 0;
        flight.TelemetryUpdated += (_, _) => updates++;

        shell.NavigateCommand.Execute(Screen.Settings);
        Assert.IsNotInstanceOfType<FlightDataViewModel>(shell.CurrentPage);

        // attached: nothing outside the DATA screen needs the HUD
        services.Session.Cs.roll = 15;
        shell.Tick();
        Assert.AreEqual(0, updates);

        flight.IsHudDetached = true;
        shell.Tick();
        Assert.AreEqual(1, updates);
        Assert.AreEqual(15, flight.Hud.Roll, 1e-6);

        services.Session.Cs.roll = -20;
        shell.Tick();
        Assert.AreEqual(-20, flight.Hud.Roll, 1e-6);

        flight.IsHudDetached = false;
        services.Session.Cs.roll = 5;
        shell.Tick();
        Assert.AreEqual(-20, flight.Hud.Roll, 1e-6, "attached again: updated only on the DATA screen");
    }
}
