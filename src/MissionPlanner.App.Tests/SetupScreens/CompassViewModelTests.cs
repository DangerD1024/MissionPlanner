using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.ViewModels;
using MissionPlanner.App.Core.ViewModels.SetupScreens;

namespace MissionPlanner.App.Tests.SetupScreens;

[TestClass]
public sealed class CompassViewModelTests
{
    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action action) => action();

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();
    }

    private sealed class NoDialogs : IDialogService
    {
        public List<string> Confirms { get; } = new();

        public Task ShowMessageAsync(string title, string message, MessageKind kind = MessageKind.Info) => Task.CompletedTask;

        public Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK")
        {
            Confirms.Add(message);
            return Task.FromResult(false);
        }

        public Task<string?> PromptAsync(string title, string message, string initialValue = "") => Task.FromResult<string?>(null);

        public Task<string?> OpenFileAsync(string title, params FileFilter[] filters) => Task.FromResult<string?>(null);

        public Task<string?> SaveFileAsync(string title, string suggestedName, params FileFilter[] filters) => Task.FromResult<string?>(null);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
    }

    private sealed class NoWindows : IWindowService
    {
        public void Show(PageViewModel page, double width = 1100, double height = 750)
        {
        }

        public Task<bool> ShowDialogAsync(PageViewModel page, double width = 1000, double height = 700) => Task.FromResult(false);
    }

    private static (CompassViewModel Vm, NoDialogs Dialogs) Create(params (string Name, double Value)[] values)
    {
        var dialogs = new NoDialogs();
        var services = new AppServices(new InlineDispatcher(), dialogs, new NoWindows());
        foreach (var (name, value) in values)
            services.Session.Vehicle.param.Add(new MAVLink.MAVLinkParam(name, value, MAVLink.MAV_PARAM_TYPE.REAL32));
        var vm = new CompassViewModel(services, new FakeCalibrationLink());
        vm.Rebuild();
        return (vm, dialogs);
    }

    [TestMethod]
    public void Load_ListsPrioritiesThenOthers_WithNothingPending()
    {
        var (vm, _) = Create(
            ("COMPASS_PRIO1_ID", 97539), ("COMPASS_PRIO2_ID", 658945), ("COMPASS_PRIO3_ID", 0),
            ("COMPASS_DEV_ID", 658945), ("COMPASS_DEV_ID2", 97539), ("COMPASS_DEV_ID4", 590114), ("COMPASS_ORIENT2", 6));

        Assert.IsTrue(vm.IsPriorityLayout);
        CollectionAssert.AreEqual(new uint[] { 97539, 658945, 590114 }, vm.Rows.Select(r => r.DevId).ToArray());
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, vm.Rows.Select(r => r.Priority).ToArray());
        Assert.IsFalse(vm.HasPendingOrder, "nothing is proposed until the user reorders");
        Assert.AreEqual("6", vm.Rows[0].Orientation, "the number until the documentation has loaded");
        Assert.AreEqual("UAVCAN", vm.Rows[0].BusType);
        Assert.AreEqual("SENSOR_ID#1", vm.Rows[0].DevType);
    }

    [TestMethod]
    public void MoveDown_ProposesTheNewPriorityValues()
    {
        var (vm, _) = Create(
            ("COMPASS_PRIO1_ID", 97539), ("COMPASS_PRIO2_ID", 658945), ("COMPASS_PRIO3_ID", 0),
            ("COMPASS_DEV_ID", 658945), ("COMPASS_DEV_ID2", 97539));
        Assert.IsFalse(vm.HasPendingOrder);

        vm.Rows[0].MoveDownCommand.Execute(null);

        CollectionAssert.AreEqual(new uint[] { 658945, 97539 }, vm.Rows.Select(r => r.DevId).ToArray());
        CollectionAssert.AreEqual(new[] { 1, 2 }, vm.Rows.Select(r => r.Priority).ToArray());
        Assert.IsTrue(vm.HasPendingOrder);
        Assert.AreEqual("COMPASS_PRIO1_ID = 658945, COMPASS_PRIO2_ID = 97539", vm.PendingText);

        vm.UndoPriorityCommand.Execute(null);
        CollectionAssert.AreEqual(new uint[] { 97539, 658945 }, vm.Rows.Select(r => r.DevId).ToArray());
        Assert.IsFalse(vm.HasPendingOrder);
    }

    [TestMethod]
    public void RemoveMissing_ClearsTheirSlot()
    {
        var (vm, _) = Create(
            ("COMPASS_PRIO1_ID", 131874), ("COMPASS_PRIO2_ID", 658945), ("COMPASS_PRIO3_ID", 0),
            ("COMPASS_DEV_ID", 658945));
        Assert.IsTrue(vm.AnyMissing);
        Assert.IsTrue(vm.Rows[0].Missing);

        vm.RemoveMissingCommand.Execute(null);

        Assert.AreEqual(658945u, vm.Rows.Single().DevId);
        Assert.AreEqual("COMPASS_PRIO1_ID = 658945, COMPASS_PRIO2_ID = 0", vm.PendingText);
    }

    [TestMethod]
    public async Task WritingPriority_AsksFirst_AndWritesNothingWhenDeclined()
    {
        var (vm, dialogs) = Create(
            ("COMPASS_PRIO1_ID", 97539), ("COMPASS_PRIO2_ID", 658945), ("COMPASS_PRIO3_ID", 0),
            ("COMPASS_DEV_ID", 658945), ("COMPASS_DEV_ID2", 97539));
        vm.Rows[1].MoveUpCommand.Execute(null);

        // not connected: the command is disabled; call the write path through the command anyway
        await vm.SavePriorityCommand.ExecuteAsync(null);

        Assert.HasCount(1, dialogs.Confirms);
        StringAssert.Contains(dialogs.Confirms[0], "COMPASS_PRIO1_ID = 658945");
        Assert.IsFalse(vm.RebootRequired);
        Assert.IsTrue(vm.HasPendingOrder);
    }

    [TestMethod]
    public void OldFirmware_UsesTheOffsetsLayout()
    {
        var (vm, _) = Create(
            ("COMPASS_OFS_X", 120), ("COMPASS_OFS_Y", -44), ("COMPASS_OFS_Z", 650),
            ("COMPASS_EXTERN2", 0), ("COMPASS_OFS2_X", 0), ("COMPASS_OFS2_Y", 0), ("COMPASS_OFS2_Z", 0),
            ("COMPASS_USE", 1), ("COMPASS_DEC", 0.1));

        Assert.IsTrue(vm.IsLegacyLayout);
        Assert.HasCount(2, vm.LegacyVisible);
        Assert.IsTrue(vm.LegacyVisible[0].OffsetsBad, "650 is above 600");
        Assert.IsTrue(vm.LegacyVisible[1].OffsetsBad, "all zero: not calibrated");
        Assert.AreEqual(true, vm.LegacyVisible[0].Use.IsChecked);
        Assert.AreEqual("5° 44'", vm.Declination);
    }
}
