using MissionPlanner.App.Core.ViewModels.ConfigScreens;
using MissionPlanner.Utilities;
using static MissionPlanner.App.Tests.ConfigScreens.ParamTestSupport;

namespace MissionPlanner.App.Tests.ConfigScreens;

[TestClass]
public class UserParamsTests
{
    [TestCleanup]
    public void Cleanup() => Settings.Instance.Remove(UserParamList.SettingsKey);

    [TestMethod]
    public void TheListIsKeptInTheSettings()
    {
        Settings.Instance.Remove(UserParamList.SettingsKey);
        CollectionAssert.AreEqual(UserParamList.Defaults.ToArray(), UserParamList.Load().ToArray(), "Mission Planner's defaults first");
        CollectionAssert.Contains(UserParamList.Defaults.ToList(), "RC9_OPTION");

        UserParamList.Save(["RTL_ALT", "FS_GCS_ENABLE"]);
        Assert.AreEqual("RTL_ALT,FS_GCS_ENABLE", Settings.Instance[UserParamList.SettingsKey]);
        CollectionAssert.AreEqual(new[] { "RTL_ALT", "FS_GCS_ENABLE" }, UserParamList.Load().ToArray());

        // an emptied list stays empty instead of falling back to the defaults
        UserParamList.Save([]);
        Assert.AreEqual(0, UserParamList.Load().Count);

        // hand-edited settings: any separator, case and duplicates
        CollectionAssert.AreEqual(new[] { "RTL_ALT", "LOG_BITMASK", "SERVO1_MIN" },
            UserParamList.Parse(" rtl_alt;LOG_BITMASK\r\nSERVO1_MIN, RTL_ALT ,").ToArray());
    }

    private static async Task<(UserParamsViewModel Vm, FakeDialogs Dialogs)> PageAsync()
    {
        var (services, dialogs, _) = CreateServices();
        AddParams(services, ("RC7_OPTION", 0, null), ("RC8_OPTION", 0, null), ("RTL_ALT", 1500, null),
            ("RTL_SPEED", 0, null), ("LOG_BITMASK", 6, null), ("BATT_MONITOR", 4, null));
        var vm = new UserParamsViewModel(services);
        await vm.RebuildAsync();
        return (vm, dialogs);
    }

    [TestMethod]
    public async Task ShowsTheChosenParametersTheVehicleHas()
    {
        Settings.Instance.Remove(UserParamList.SettingsKey);
        var (vm, _) = await PageAsync();

        CollectionAssert.AreEqual(new[] { "RC7_OPTION", "RC8_OPTION" }, vm.Rows.Select(r => r.Name).ToArray());
        StringAssert.Contains(vm.MissingText, "CH6_OPT");
        Assert.AreEqual("2 parameters", vm.Summary);

        // the picker offers what is not chosen yet
        CollectionAssert.AreEqual(new[] { "BATT_MONITOR", "LOG_BITMASK", "RTL_ALT", "RTL_SPEED" }, vm.PickerResults.Select(p => p.Name).ToArray());
        vm.PickerSearch = "rtl";
        vm.ApplyPicker();
        CollectionAssert.AreEqual(new[] { "RTL_ALT", "RTL_SPEED" }, vm.PickerResults.Select(p => p.Name).ToArray());
        Assert.AreEqual("2 matches", vm.PickerSummary);
    }

    [TestMethod]
    public async Task AddRemoveAndMoveAreSaved()
    {
        UserParamList.Save(["RTL_ALT"]);
        var (vm, _) = await PageAsync();
        CollectionAssert.AreEqual(new[] { "RTL_ALT" }, vm.Rows.Select(r => r.Name).ToArray());

        vm.AddCommand.Execute(vm.PickerResults.Single(p => p.Name == "LOG_BITMASK"));
        vm.AddName("BATT_MONITOR");
        CollectionAssert.AreEqual(new[] { "RTL_ALT", "LOG_BITMASK", "BATT_MONITOR" }, vm.Rows.Select(r => r.Name).ToArray());
        Assert.AreEqual("RTL_ALT,LOG_BITMASK,BATT_MONITOR", Settings.Instance[UserParamList.SettingsKey]);
        Assert.IsFalse(vm.PickerResults.Any(p => p.Name == "LOG_BITMASK"), "a chosen parameter leaves the picker");
        Assert.AreEqual("BATT_MONITOR", vm.SelectedRow?.Name);

        vm.MoveUpCommand.Execute(vm.Rows[2]);
        CollectionAssert.AreEqual(new[] { "RTL_ALT", "BATT_MONITOR", "LOG_BITMASK" }, vm.Rows.Select(r => r.Name).ToArray());
        vm.MoveUpCommand.Execute(vm.Rows[0]);
        Assert.AreEqual("RTL_ALT", vm.Rows[0].Name, "the first stays first");
        Assert.AreEqual("RTL_ALT,BATT_MONITOR,LOG_BITMASK", Settings.Instance[UserParamList.SettingsKey]);

        vm.RemoveCommand.Execute(vm.Rows.Single(r => r.Name == "RTL_ALT"));
        CollectionAssert.AreEqual(new[] { "BATT_MONITOR", "LOG_BITMASK" }, vm.Rows.Select(r => r.Name).ToArray());
        Assert.AreEqual("BATT_MONITOR,LOG_BITMASK", Settings.Instance[UserParamList.SettingsKey]);

        // a new page (next start) reads the same list
        var (again, _) = await PageAsync();
        CollectionAssert.AreEqual(new[] { "BATT_MONITOR", "LOG_BITMASK" }, again.Rows.Select(r => r.Name).ToArray());
    }

    [TestMethod]
    public async Task MovingSkipsNamesTheVehicleLacks()
    {
        UserParamList.Save(["RTL_ALT", "NOT_HERE", "BATT_MONITOR"]);
        var (vm, _) = await PageAsync();

        vm.MoveDownCommand.Execute(vm.Rows[0]);

        CollectionAssert.AreEqual(new[] { "BATT_MONITOR", "RTL_ALT" }, vm.Rows.Select(r => r.Name).ToArray());
        Assert.AreEqual("BATT_MONITOR,NOT_HERE,RTL_ALT", Settings.Instance[UserParamList.SettingsKey]);
        StringAssert.Contains(vm.MissingText, "NOT_HERE");
    }

    [TestMethod]
    public async Task EditsAreQueuedAndDroppedWithTheirRow()
    {
        UserParamList.Save(["RTL_ALT", "BATT_MONITOR"]);
        var (vm, _) = await PageAsync();

        Assert.IsTrue(await vm.CommitEditAsync(vm.Rows[0], "2500"));
        Assert.IsTrue(await vm.CommitEditAsync(vm.Rows[1], "3"));
        Assert.AreEqual("2 parameters · 2 unsaved", vm.Summary);

        // adding re-lists the rows: the edits stay
        vm.AddName("RTL_SPEED");
        Assert.IsTrue(vm.Rows.Single(r => r.Name == "RTL_ALT").IsModified);
        Assert.AreEqual(2, vm.PendingCount);

        // removing a row drops its edit
        vm.RemoveCommand.Execute(vm.Rows.Single(r => r.Name == "BATT_MONITOR"));
        CollectionAssert.AreEquivalent(new[] { "RTL_ALT" }, vm.Edits.Pending.Keys.ToArray());
    }

    [TestMethod]
    public async Task RestoreDefaultsAsksFirst()
    {
        UserParamList.Save(["RTL_ALT"]);
        var (vm, dialogs) = await PageAsync();

        dialogs.ConfirmAnswer = false;
        await vm.RestoreDefaultsCommand.ExecuteAsync(null);
        CollectionAssert.AreEqual(new[] { "RTL_ALT" }, vm.Names.ToArray());

        dialogs.ConfirmAnswer = true;
        await vm.RestoreDefaultsCommand.ExecuteAsync(null);
        CollectionAssert.AreEqual(UserParamList.Defaults.ToArray(), vm.Names.ToArray());
        CollectionAssert.AreEqual(new[] { "RC7_OPTION", "RC8_OPTION" }, vm.Rows.Select(r => r.Name).ToArray());
    }
}
