using MissionPlanner.App.Core.Maps;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.ViewModels;
using MissionPlanner.App.Core.ViewModels.FlightData;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Tests.FlightData;

/// <summary>UI services that run inline and record what the view-model asked for.</summary>
internal sealed class FakeUi : IUiDispatcher, IDialogService, IWindowService
{
    public List<string> Confirms { get; } = new();
    public List<PageViewModel> Shown { get; } = new();
    public bool ConfirmAnswer { get; set; } = true;

    public bool CheckAccess() => true;
    public void Post(Action action) => action();
    public Task InvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();

    public Task ShowMessageAsync(string title, string message, MessageKind kind = MessageKind.Info) => Task.CompletedTask;
    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK")
    {
        Confirms.Add(title);
        return Task.FromResult(ConfirmAnswer);
    }

    public Task<string?> PromptAsync(string title, string message, string initialValue = "") => Task.FromResult<string?>(initialValue);
    public Task<string?> OpenFileAsync(string title, params FileFilter[] filters) => Task.FromResult<string?>(null);
    public Task<string?> SaveFileAsync(string title, string suggestedName, params FileFilter[] filters) => Task.FromResult<string?>(null);
    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

    public void Show(PageViewModel page, double width = 1100, double height = 750) => Shown.Add(page);
    public Task<bool> ShowDialogAsync(PageViewModel page, double width = 1000, double height = 700) => Task.FromResult(false);

    public static AppServices Services(out FakeUi ui)
    {
        ui = new FakeUi();
        return new AppServices(ui, ui, ui);
    }
}

[TestClass]
public class FlightDataViewModelTests
{
    [ClassInitialize]
    public static void Init(TestContext _) =>
        MapTiles.Initialize(Path.Combine(TestEnvironment.DataRoot, "gmapcache") + Path.DirectorySeparatorChar);

    [TestCleanup]
    public void Cleanup()
    {
        for (var i = 0; i < 6; i++)
            Settings.Instance.Remove("next_quick" + i);
    }

    [TestMethod]
    public void HudVersionMovesOnlyWhenTelemetryChanges()
    {
        var vm = new FlightDataViewModel(FakeUi.Services(out _));
        vm.Tick();
        var v = vm.Hud.Version;
        vm.Tick();
        vm.Tick();
        Assert.AreEqual(v, vm.Hud.Version, "nothing changed, so the HUD must not redraw");

        vm.Services.Session.Cs.roll = 12;
        vm.Tick();
        Assert.AreEqual(v + 1, vm.Hud.Version);
        Assert.AreEqual(12, vm.Hud.Roll, 1e-6);
    }

    [TestMethod]
    public void HudShowsNoLinkWithoutAVehicle()
    {
        var vm = new FlightDataViewModel(FakeUi.Services(out _));
        vm.Tick();
        Assert.IsFalse(vm.Hud.Connected);
        Assert.IsFalse(vm.IsConnected);
        Assert.IsFalse(vm.CanCommand);
    }

    [TestMethod]
    public void QuickChoicesPersistInSettings()
    {
        var services = FakeUi.Services(out _);
        var vm = new FlightDataViewModel(services);
        var tile = vm.QuickTiles[2];

        vm.EditQuickTileCommand.Execute(tile);
        Assert.IsTrue(vm.IsQuickPickerOpen);
        Assert.IsNotEmpty(vm.QuickFieldOptions);

        vm.QuickFilter = "battery_voltage3";
        var field = vm.QuickFieldOptions.First(f => f.Name == "battery_voltage3");
        vm.PickQuickFieldCommand.Execute(field);
        Assert.IsFalse(vm.IsQuickPickerOpen);
        Assert.AreEqual("battery_voltage3", tile.Field.Name);
        Assert.AreEqual("battery_voltage3", Settings.Instance["next_quick2"]);

        // a new page (next start) restores it
        var again = new FlightDataViewModel(services);
        Assert.AreEqual("battery_voltage3", again.QuickTiles[2].Field.Name);
        Assert.AreEqual("alt", again.QuickTiles[0].Field.Name);

        // back to the default forgets the choice
        again.EditQuickTileCommand.Execute(again.QuickTiles[2]);
        again.ResetQuickTileCommand.Execute(null);
        Assert.AreEqual("wp_dist", again.QuickTiles[2].Field.Name);
        Assert.IsTrue(string.IsNullOrEmpty(Settings.Instance["next_quick2"]));
    }

    [TestMethod]
    public void UnknownSavedQuickChoiceFallsBackToTheDefault()
    {
        Settings.Instance["next_quick1"] = "not_a_property";
        var vm = new FlightDataViewModel(FakeUi.Services(out _));
        Assert.AreEqual("groundspeed", vm.QuickTiles[1].Field.Name);
    }

    [TestMethod]
    public void QuickTilesReadValuesOnTick()
    {
        var vm = new FlightDataViewModel(FakeUi.Services(out _));
        var cs = vm.Services.Session.Cs;
        cs.yaw = 123;
        vm.Tick();
        Assert.AreEqual(123, vm.QuickTiles[3].Value, 1e-6, "yaw tile");
    }

    [TestMethod]
    public void StatusListIsBuiltWhenTheTabOpensAndFilters()
    {
        var vm = new FlightDataViewModel(FakeUi.Services(out _));
        Assert.IsEmpty(vm.StatusEntries, "not built until shown");

        vm.SelectedTabIndex = (int)FlightDataTab.Status;
        Assert.IsGreaterThan(100, vm.StatusEntries.Count);
        var yaw = vm.StatusEntries.Single(e => e.Name == "yaw");
        Assert.AreNotEqual("", yaw.Value, "filled on open");

        vm.StatusFilter = "batt";
        Assert.IsTrue(vm.StatusEntries.All(e => e.Name.Contains("batt", StringComparison.OrdinalIgnoreCase)));
        Assert.IsNotEmpty(vm.StatusEntries);
    }

    [TestMethod]
    public void StatusRowsFormatEveryPropertyWithoutThrowing()
    {
        var cs = new CurrentState();
        var entries = StatusCatalog.CreateEntries();
        foreach (var e in entries)
            e.Update(cs);

        var byName = entries.ToDictionary(e => e.Name);
        Assert.AreEqual("False", byName["armed"].Value);
        Assert.IsTrue(byName.ContainsKey("mode"));
        Assert.IsTrue(byName.ContainsKey("HomeLocation"));
        CollectionAssert.AllItemsAreUnique(entries.Select(e => e.Name).ToList());
    }

    [TestMethod]
    public void StatusRowNotifiesOnlyWhenTheValueChanges()
    {
        var cs = new CurrentState();
        var entry = StatusCatalog.CreateEntries().Single(e => e.Name == "ch3out");
        var changes = 0;
        entry.PropertyChanged += (_, _) => changes++;
        cs.ch3out = 1100;
        entry.Update(cs);
        entry.Update(cs);
        Assert.AreEqual(1, changes);
        Assert.AreEqual("1100", entry.Value);
        cs.ch3out = 1200.5f;
        entry.Update(cs);
        Assert.AreEqual("1200.5", entry.Value);

        var yaw = StatusCatalog.CreateEntries().Single(e => e.Name == "yaw");
        cs.yaw = 58.95f;
        yaw.Update(cs);
        Assert.AreEqual("58.95", yaw.Value, "floats at float precision");
    }

    [TestMethod]
    public void WaypointChoicesFollowTheMission()
    {
        var services = FakeUi.Services(out _);
        var vm = new FlightDataViewModel(services);
        CollectionAssert.AreEqual(new[] { 0 }, vm.WaypointNumbers.ToArray());
        services.Mission.Mission.Add(services.Mission.NewWaypoint(1, 1));
        services.Mission.Mission.Add(services.Mission.NewWaypoint(1, 2));
        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, vm.WaypointNumbers.ToArray());
    }

    [TestMethod]
    public void MapModelTracksTheVehicleAndAutoPans()
    {
        var vm = new FlightDataViewModel(FakeUi.Services(out _));
        var cs = vm.Services.Session.Cs;
        vm.Tick();
        vm.Tick();
        Assert.IsFalse(vm.MapModel.HasPosition);

        cs.lat = -35.0;
        cs.lng = 149.0;
        cs.yaw = 90;
        vm.Tick();
        vm.Tick();
        Assert.IsTrue(vm.MapModel.HasPosition);
        Assert.AreEqual(90, vm.MapModel.Heading, 1e-6);
        Assert.AreEqual(1, vm.MapModel.Track.Count);
        Assert.AreEqual(-35.0, vm.CenterLat, 1e-9, "auto pan centres on the vehicle");

        vm.OnUserPanned();
        Assert.IsFalse(vm.AutoPan);
        cs.lat = -35.01;
        vm.Tick();
        vm.Tick();
        Assert.AreEqual(-35.0, vm.CenterLat, 1e-9, "no auto pan after the user panned");
        Assert.AreEqual(2, vm.MapModel.Track.Count);

        var version = vm.MapModel.Version;
        vm.Tick();
        vm.Tick();
        Assert.AreEqual(version, vm.MapModel.Version, "a still vehicle does not redraw the map");

        vm.ClearTrack();
        Assert.AreEqual(0, vm.MapModel.Track.Count);
    }

    [TestMethod]
    public void VehicleCommandsNeedALink()
    {
        var vm = new FlightDataViewModel(FakeUi.Services(out var ui));
        vm.Tick();
        vm.ArmDisarmCommand.Execute(null);
        Assert.IsEmpty(ui.Confirms, "no confirmation asked without a vehicle");
        StringAssert.Contains(vm.ActionStatus, "Connect");
        Assert.IsTrue(vm.ActionStatusIsError);
        Assert.IsTrue(vm.IsToastVisible);
    }

    [TestMethod]
    public void DataFlashButtonsOpenTheLogWindows()
    {
        var vm = new FlightDataViewModel(FakeUi.Services(out var ui));
        vm.OpenLogDownloadCommand.Execute(null);
        vm.OpenLogBrowserCommand.Execute(null);
        Assert.IsInstanceOfType<MissionPlanner.App.Core.ViewModels.Logs.LogDownloadViewModel>(ui.Shown[0]);
        Assert.IsInstanceOfType<MissionPlanner.App.Core.ViewModels.Logs.LogBrowserViewModel>(ui.Shown[1]);
    }
}
