using MissionPlanner.App.Core.Params;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.ViewModels.ConfigScreens;
using MissionPlanner.Utilities;
using static MissionPlanner.App.Tests.ConfigScreens.ParamTestSupport;

namespace MissionPlanner.App.Tests.ConfigScreens;

[TestClass]
public class FullParameterListViewModelTests
{
    private static async Task<(FullParameterListViewModel Vm, FakeDialogs Dialogs, FakeWindows Windows, AppServices Services)> WithVehicleAsync()
    {
        var (services, dialogs, windows) = CreateServices();
        AddParams(services,
            ("ATC_RAT_RLL_P", 0.135, 0.135),
            ("ATC_RAT_PIT_P", 0.2, 0.135),
            ("BATT_MONITOR", 4, 0),
            ("BATT_CAPACITY", 3300, 3300),
            ("SERVO10_MIN", 1100, 1100),
            ("SERVO2_MIN", 1000, 1100));
        var vm = new FullParameterListViewModel(services);
        await vm.RebuildAsync();
        return (vm, dialogs, windows, services);
    }

    [TestMethod]
    public async Task BuildsRowsGroupsAndDefaults()
    {
        var (vm, _, _, _) = await WithVehicleAsync();

        CollectionAssert.AreEqual(new[] { "ATC_RAT_PIT_P", "ATC_RAT_RLL_P", "BATT_CAPACITY", "BATT_MONITOR", "SERVO2_MIN", "SERVO10_MIN" },
            vm.AllRows.Select(r => r.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "All", "ATC", "BATT", "SERVO2", "SERVO10" }, vm.Groups.Select(g => g.Name).ToArray());
        Assert.IsTrue(vm.SelectedGroup!.IsAll);
        Assert.AreEqual(6, vm.VisibleRows.Count);
        Assert.IsTrue(vm.HasDefaults);
        Assert.IsFalse(vm.IsOffline);
        Assert.AreEqual("6 parameters", vm.Summary);
    }

    [TestMethod]
    public async Task TreeSearchAndFiltersNarrowTheGrid()
    {
        var (vm, _, _, _) = await WithVehicleAsync();

        vm.SelectedGroup = vm.Groups.Single(g => g.Name == "BATT");
        CollectionAssert.AreEqual(new[] { "BATT_CAPACITY", "BATT_MONITOR" }, vm.VisibleRows.Select(r => r.Name).ToArray());
        Assert.AreEqual("2 of 6 parameters", vm.Summary);

        vm.SearchText = "monitor";
        vm.ApplyFilter();
        CollectionAssert.AreEqual(new[] { "BATT_MONITOR" }, vm.VisibleRows.Select(r => r.Name).ToArray());

        vm.SelectedGroup = vm.Groups[0];
        vm.SearchText = "";
        vm.ShowNonDefaultOnly = true;
        CollectionAssert.AreEqual(new[] { "ATC_RAT_PIT_P", "BATT_MONITOR", "SERVO2_MIN" }, vm.VisibleRows.Select(r => r.Name).ToArray());

        vm.ShowNonDefaultOnly = false;
        vm.ShowModifiedOnly = true;
        Assert.AreEqual(0, vm.VisibleRows.Count);
        Assert.IsTrue(vm.NoMatches);
    }

    [TestMethod]
    public async Task EditsAreValidatedAndQueued()
    {
        var (vm, dialogs, _, _) = await WithVehicleAsync();
        var row = vm.AllRows.Single(r => r.Name == "BATT_CAPACITY");

        Assert.IsFalse(await vm.CommitEditAsync(row, "lots"));
        Assert.IsTrue(row.IsInvalid);
        Assert.AreEqual(0, vm.PendingCount);

        Assert.IsTrue(await vm.CommitEditAsync(row, "5000"));
        Assert.IsFalse(row.IsInvalid);
        Assert.IsTrue(row.IsModified);
        Assert.AreEqual("5000", row.ValueText);
        Assert.AreEqual(5000, vm.Edits.Pending["BATT_CAPACITY"]);
        Assert.AreEqual("6 parameters · 1 unsaved", vm.Summary);

        // back to the vehicle's value: no longer an edit
        Assert.IsFalse(await vm.CommitEditAsync(row, "3300"));
        Assert.IsFalse(row.IsModified);
        Assert.AreEqual(0, vm.PendingCount);

        // documented rows: read-only refused, out of range asks
        var ranged = new ParamRow("ATC_RAT_YAW_P", 0.18, null, Meta("ATC_RAT_YAW_P", min: 0, max: 2.5));
        dialogs.ConfirmAnswer = false;
        Assert.IsFalse(await vm.CommitEditAsync(ranged, "3"));
        Assert.AreEqual(1, dialogs.Confirms.Count);
        Assert.IsFalse(ranged.IsModified);
        dialogs.ConfirmAnswer = true;
        Assert.IsTrue(await vm.CommitEditAsync(ranged, "3"));
        Assert.AreEqual(3, ranged.PendingValue);

        var ro = new ParamRow("STAT_RUNTIME", 100, null, Meta("STAT_RUNTIME", readOnly: true));
        Assert.IsFalse(await vm.CommitEditAsync(ro, "0"));
        Assert.IsFalse(ro.IsModified);
        Assert.AreEqual("ReadOnly", dialogs.Messages.Last().Title);
    }

    [TestMethod]
    public async Task LoadingAFileMarksDifferencesAsEdits()
    {
        var (vm, dialogs, _, services) = await WithVehicleAsync();
        var path = TempFile("ATC_RAT_RLL_P,0.15\nBATT_MONITOR,4\nSERVO2_MIN,1100\nNOT_HERE,3\n");

        await vm.LoadFileAsync(path);

        CollectionAssert.AreEquivalent(new[] { "ATC_RAT_RLL_P", "SERVO2_MIN" }, vm.Edits.Pending.Keys.ToArray());
        Assert.AreEqual(0.15, vm.Edits.Pending["ATC_RAT_RLL_P"], 1e-9);
        Assert.IsTrue(vm.ShowModifiedOnly, "the grid switches to the loaded changes");
        CollectionAssert.AreEqual(new[] { "ATC_RAT_RLL_P", "SERVO2_MIN" }, vm.VisibleRows.Select(r => r.Name).ToArray());
        Assert.IsTrue(dialogs.Messages.Last().Message.Contains("NOT_HERE"), dialogs.Messages.Last().Message);
        Assert.AreEqual(0.135, services.Params.Get("ATC_RAT_RLL_P")!.Value, 1e-6, "nothing is written");
    }

    [TestMethod]
    public async Task WithoutAVehicleALoadedFileIsShown()
    {
        var (services, _, _) = CreateServices();
        var vm = new FullParameterListViewModel(services);
        await vm.RebuildAsync();
        Assert.IsTrue(vm.IsEmpty);

        await vm.LoadFileAsync(TempFile("ACRO_YAW_P,4.5\nBATT_MONITOR,4\n"));

        Assert.IsTrue(vm.IsOffline);
        Assert.IsFalse(vm.IsEmpty);
        CollectionAssert.AreEqual(new[] { "ACRO_YAW_P", "BATT_MONITOR" }, vm.AllRows.Select(r => r.Name).ToArray());
        Assert.AreEqual(0, vm.PendingCount);
        Assert.IsFalse(vm.WriteCommand.CanExecute(null));

        // edits offline can be saved to a file
        Assert.IsTrue(await vm.CommitEditAsync(vm.AllRows[0], "5"));
        var output = Path.Combine(TestEnvironment.DataRoot, "offline-" + Guid.NewGuid().ToString("N") + ".param");
        await vm.SaveFileAsync(output);
        var saved = ParamFile.loadParamFile(output);
        Assert.AreEqual(5, saved["ACRO_YAW_P"]);
        Assert.AreEqual(4, saved["BATT_MONITOR"]);
    }

    [TestMethod]
    public async Task CompareQueuesTheTickedValues()
    {
        var (vm, _, windows, _) = await WithVehicleAsync();
        var path = TempFile("ATC_RAT_RLL_P,0.135\nBATT_MONITOR,3\nSERVO2_MIN,1200\nEXTRA,1\n");
        ParamCompareViewModel? shown = null;
        windows.OnDialog = page =>
        {
            shown = (ParamCompareViewModel)page;
            shown.Rows.Single(r => r.Name == "SERVO2_MIN").Use = false;
            return true;
        };

        await vm.CompareWithFileAsync(path);

        Assert.IsNotNull(shown);
        CollectionAssert.AreEqual(new[] { "BATT_MONITOR", "SERVO2_MIN" }, shown.Rows.Select(r => r.Name).ToArray());
        Assert.AreEqual(1, shown.OnlyInFile);
        CollectionAssert.AreEquivalent(new[] { "BATT_MONITOR" }, vm.Edits.Pending.Keys.ToArray());
        Assert.AreEqual(3, vm.AllRows.Single(r => r.Name == "BATT_MONITOR").DisplayValue);
    }

    [TestMethod]
    public async Task CancelledCompareChangesNothing()
    {
        var (vm, _, windows, _) = await WithVehicleAsync();
        windows.OnDialog = _ => false;

        await vm.CompareWithFileAsync(TempFile("BATT_MONITOR,3\n"));

        Assert.AreEqual(0, vm.PendingCount);
    }

    [TestMethod]
    public async Task WriteNeedsAConnection()
    {
        var (vm, dialogs, _, _) = await WithVehicleAsync();
        await vm.CommitEditAsync(vm.AllRows.Single(r => r.Name == "BATT_MONITOR"), "3");

        Assert.IsFalse(vm.WriteCommand.CanExecute(null), "disabled while disconnected");
        var written = await ParamWriteFlow.WriteAsync(vm.Services, vm.Edits);

        Assert.AreEqual(0, written.Count);
        Assert.AreEqual(1, vm.PendingCount, "the edit stays queued");
        Assert.AreEqual(MessageKind.Error, dialogs.Messages.Last().Kind);
        Assert.AreEqual(0, dialogs.Confirms.Count, "nothing to confirm without a vehicle");
    }

    [TestMethod]
    public async Task FavouritesMoveToTheTop()
    {
        var (vm, _, _, _) = await WithVehicleAsync();
        try
        {
            vm.AllRows.Single(r => r.Name == "SERVO2_MIN").IsFavourite = true;

            Assert.AreEqual("SERVO2_MIN", vm.VisibleRows[0].Name);
            Assert.IsTrue(ParamFavourites.Load().Contains("SERVO2_MIN"));

            // a rebuild keeps the order and the tick
            await vm.RebuildAsync();
            Assert.AreEqual("SERVO2_MIN", vm.AllRows[0].Name);
            Assert.IsTrue(vm.AllRows[0].IsFavourite);
        }
        finally
        {
            Settings.Instance.Remove(ParamFavourites.SettingsKey);
        }
    }

    [TestMethod]
    public async Task RebuildKeepsPendingEdits()
    {
        var (vm, _, _, _) = await WithVehicleAsync();
        await vm.CommitEditAsync(vm.AllRows.Single(r => r.Name == "BATT_MONITOR"), "3");

        await vm.RebuildAsync();

        var row = vm.AllRows.Single(r => r.Name == "BATT_MONITOR");
        Assert.IsTrue(row.IsModified);
        Assert.AreEqual(3, row.PendingValue);
    }
}
