using MissionPlanner.App.Core.Params;
using MissionPlanner.App.Core.ViewModels.ConfigScreens;
using static MissionPlanner.App.Tests.ConfigScreens.ParamTestSupport;
using ParamType = MAVLink.MAV_PARAM_TYPE;

namespace MissionPlanner.App.Tests.ConfigScreens;

/// <summary>The value cell's editors: documented values, the bitmask editor, and Reboot.</summary>
[TestClass]
public class ParamEditorTests
{
    private static readonly KeyValuePair<int, string>[] LogBits =
    [
        new(0, "Fast Attitude"), new(1, "Medium Attitude"), new(2, "GPS"), new(3, "PM"), new(4, "Control Tuning"),
    ];

    private static readonly KeyValuePair<double, string>[] FenceValues = [new(0, "Disabled"), new(1, "Enabled")];

    private static ParamMeta LogMeta() => Meta("LOG_BITMASK", "Log bitmask", "What to log", bits: LogBits);

    // ---- bit arithmetic -------------------------------------------------------------------

    [TestMethod]
    public void BitsAreTakenInTheParameterWidth()
    {
        Assert.AreEqual(0xFFUL, ParamBitmask.ToBits(-1, ParamType.INT8));
        Assert.AreEqual(0x80UL, ParamBitmask.ToBits(-128, ParamType.INT8));
        Assert.AreEqual(0xFFFFUL, ParamBitmask.ToBits(-1, ParamType.INT16));
        Assert.AreEqual(0x80000000UL, ParamBitmask.ToBits(int.MinValue, ParamType.INT32));
        Assert.AreEqual(0x80000000UL, ParamBitmask.ToBits(2147483648.0, ParamType.UINT32));
        Assert.AreEqual(176126UL, ParamBitmask.ToBits(176126, ParamType.INT32));
        Assert.AreEqual(5UL, ParamBitmask.ToBits(4.9999999, ParamType.REAL32), "float noise is rounded away");
        Assert.AreEqual(0UL, ParamBitmask.ToBits(double.NaN, ParamType.INT32));
    }

    [TestMethod]
    public void SignedTypesGetTheirTopBitAsANegativeValue()
    {
        // Mission Planner's MavlinkCheckBoxBitMask: INT8 255 = -1
        Assert.AreEqual(-1, ParamBitmask.FromBits(0xFF, ParamType.INT8));
        Assert.AreEqual(255, ParamBitmask.FromBits(0xFF, ParamType.UINT8));
        Assert.AreEqual(-32768, ParamBitmask.FromBits(0x8000, ParamType.INT16));
        Assert.AreEqual(int.MinValue, ParamBitmask.FromBits(0x80000000, ParamType.INT32));
        Assert.AreEqual(2147483648.0, ParamBitmask.FromBits(0x80000000, ParamType.UINT32));
        Assert.AreEqual(int.MinValue, ParamBitmask.FromBits(0x80000000, ParamType.REAL32), "unknown type: INT32 like Mission Planner");
        Assert.AreEqual(6, ParamBitmask.FromBits(6, ParamType.INT32));
    }

    [TestMethod]
    public void DescribeNamesTheSetBits()
    {
        CollectionAssert.AreEqual(new[] { "Medium Attitude", "GPS", "bit 9" }, ParamBitmask.Describe(6 + 512, LogBits).ToArray());
        CollectionAssert.AreEqual(new[] { 0, 3, 31 }, ParamBitmask.SetBits(0x80000009).ToArray());
        Assert.AreEqual(0, ParamBitmask.Describe(0, LogBits).Count);
    }

    // ---- the Set Bitmask dialog ------------------------------------------------------------

    [TestMethod]
    public void EditorTicksTheDocumentedBitsAndKeepsUndocumentedOnes()
    {
        // bits 1, 2 documented; 9 and 17 set but not documented
        var value = 2 + 4 + 512 + 131072;
        var editor = new BitmaskEditorViewModel("LOG_BITMASK", LogMeta(), value, ParamType.INT32);

        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4 }, editor.Documented.Select(b => b.Bit).ToArray());
        CollectionAssert.AreEqual(new[] { false, true, true, false, false }, editor.Documented.Select(b => b.IsChecked).ToArray());
        CollectionAssert.AreEqual(new[] { 9, 17 }, editor.Undocumented.Select(b => b.Bit).ToArray());
        Assert.IsTrue(editor.Undocumented.All(b => b.IsChecked && !b.IsDocumented));
        Assert.IsTrue(editor.HasUndocumented);
        Assert.AreEqual(value, editor.Value, "nothing changed: the same value, undocumented bits included");
        Assert.IsFalse(editor.IsChanged);
        Assert.AreEqual("Log bitmask", editor.DisplayName);

        var changes = new List<string?>();
        editor.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        editor.Documented[0].IsChecked = true;
        Assert.AreEqual(value + 1, editor.Value);
        Assert.IsTrue(editor.IsChanged);
        CollectionAssert.Contains(changes, nameof(BitmaskEditorViewModel.ValueText));

        // clearing an undocumented bit drops it
        editor.Undocumented[1].IsChecked = false;
        Assert.AreEqual(1 + 2 + 4 + 512, editor.Value);
        StringAssert.Contains(editor.ValueText, "0x207");
    }

    [TestMethod]
    public void AllAndNoneTouchOnlyDocumentedBits()
    {
        var editor = new BitmaskEditorViewModel("LOG_BITMASK", LogMeta(), 512, ParamType.INT32);

        editor.SelectAllCommand.Execute(null);
        Assert.AreEqual(31 + 512, editor.Value);

        editor.ClearAllCommand.Execute(null);
        Assert.AreEqual(512, editor.Value, "the undocumented bit stays");
    }

    [TestMethod]
    public void EditorRespectsTheParameterType()
    {
        var bits = new KeyValuePair<int, string>[] { new(0, "A"), new(7, "Top"), new(9, "Too high for INT8") };
        var editor = new BitmaskEditorViewModel("X_OPTIONS", Meta("X_OPTIONS", bits: bits), -128, ParamType.INT8);

        CollectionAssert.AreEqual(new[] { 0, 7 }, editor.Documented.Select(b => b.Bit).ToArray(), "bits beyond 8 cannot be set");
        Assert.IsTrue(editor.Documented[1].IsChecked);
        editor.Documented[0].IsChecked = true;
        Assert.AreEqual(-127, editor.Value, "0x81 as INT8");
    }

    [TestMethod]
    public void EditorClosesWithItsResult()
    {
        var editor = new BitmaskEditorViewModel("LOG_BITMASK", LogMeta(), 0, ParamType.INT32);
        Assert.AreEqual(250 + 54 + 3 * 30, editor.DialogHeight, "5 bits in two columns, with a description");
        var many = Enumerable.Range(0, 32).Select(b => new KeyValuePair<int, string>(b, "bit")).ToList();
        Assert.AreEqual(720, new BitmaskEditorViewModel("X", Meta("X", bits: many), 0, ParamType.INT32).DialogHeight);
        bool? result = null;
        editor.CloseRequested += (_, r) => result = r;

        editor.CancelCommand.Execute(null);
        Assert.IsFalse(result);
        editor.ApplyCommand.Execute(null);
        Assert.IsTrue(result);
    }

    // ---- rows ------------------------------------------------------------------------------

    [TestMethod]
    public void RowsPickTheirEditorAndExplainTheValue()
    {
        var number = new ParamRow("RTL_ALT", 1500, null, Meta("RTL_ALT", min: 200, max: 30000));
        Assert.AreEqual(ParamEditorKind.Number, number.Kind);
        Assert.AreEqual("", number.ValueLabel);
        Assert.IsNull(number.ValueTip);

        var options = new ParamRow("FENCE_ENABLE", 1, null, Meta("FENCE_ENABLE", values: FenceValues));
        Assert.IsTrue(options.IsOptions);
        Assert.AreEqual("Enabled", options.ValueLabel);
        CollectionAssert.AreEqual(new[] { "0: Disabled", "1: Enabled" }, options.Choices.Select(c => c.Text).ToArray());
        var changes = new List<string?>();
        options.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        options.PendingValue = 0;
        Assert.AreEqual("Disabled", options.ValueLabel);
        CollectionAssert.Contains(changes, nameof(ParamRow.ValueLabel));
        options.PendingValue = 7;
        Assert.AreEqual("", options.ValueLabel, "an undocumented value has no label");
        StringAssert.Contains(options.ValueTip, "not a documented value");

        var mask = new ParamRow("LOG_BITMASK", 6, null, LogMeta());
        Assert.IsTrue(mask.IsBitmask);
        Assert.AreEqual("Medium Attitude, GPS", mask.ValueLabel);
        Assert.AreEqual("6\nMedium Attitude\nGPS", mask.ValueTip);
        mask.Value = 0;
        Assert.AreEqual("none", mask.ValueLabel);
    }

    [TestMethod]
    public void TypedLabelsOfDocumentedValuesAreAccepted()
    {
        var meta = Meta("FENCE_ENABLE", values: FenceValues);

        Assert.AreEqual(1, ParamEditBuffer.Validate("FENCE_ENABLE", "enabled", meta).Value);
        Assert.AreEqual(0, ParamEditBuffer.Validate("FENCE_ENABLE", "0: Disabled", meta).Value);
        Assert.AreEqual(1, ParamEditBuffer.Validate("FENCE_ENABLE", "1", meta).Value);
        Assert.AreEqual(EditCheck.NotANumber, ParamEditBuffer.Validate("FENCE_ENABLE", "maybe", meta).Result);
        Assert.AreEqual(EditCheck.NotANumber, ParamEditBuffer.Validate("FENCE_ENABLE", "5: Disabled", meta).Result,
            "a number before the colon must be documented");
        Assert.AreEqual(EditCheck.NotANumber, ParamEditBuffer.Validate("RTL_ALT", "enabled", null).Result);

        var twice = Meta("X", values: [new(0, "Off"), new(1, "Off")]);
        Assert.IsFalse(ParamEditBuffer.TryParseOption("off", twice, out _), "an ambiguous label is refused");
    }

    // ---- through a page (with the documentation shipped with the app) -----------------------

    private static async Task<(FullParameterListViewModel Vm, FakeDialogs Dialogs, FakeWindows Windows)> PageAsync()
    {
        var (services, dialogs, windows) = CreateServices();
        // LOG_BITMASK: bits 1 and 2 documented, bit 16 is not
        AddParams(services, ("FS_GCS_ENABLE", 0, null), ("LOG_BITMASK", 2 + 4 + 65536, null), ("RC_SPEED", 490, null));
        await ParamPagesTestSupport.LoadOfflineDocsAsync(services);
        var vm = new FullParameterListViewModel(services);
        await vm.RebuildAsync();
        return (vm, dialogs, windows);
    }

    [TestMethod]
    public async Task RowsOfAPageGetTheirEditorsFromTheDocumentation()
    {
        var (vm, _, _) = await PageAsync();

        Assert.AreEqual(ParamEditorKind.Options, vm.AllRows.Single(r => r.Name == "FS_GCS_ENABLE").Kind);
        Assert.AreEqual(ParamEditorKind.Bitmask, vm.AllRows.Single(r => r.Name == "LOG_BITMASK").Kind);
        Assert.AreEqual(ParamEditorKind.Number, vm.AllRows.Single(r => r.Name == "RC_SPEED").Kind);
        Assert.AreEqual("Disabled/NoAction", vm.AllRows.Single(r => r.Name == "FS_GCS_ENABLE").ValueLabel);
        // the bit labels differ between the offline file and a downloaded pdef (ATTITUDE_MED / Medium Attitude)
        var mask = vm.AllRows.Single(r => r.Name == "LOG_BITMASK").ValueLabel;
        StringAssert.EndsWith(mask, ", GPS, bit 16");
        Assert.AreEqual(3, mask.Split(", ").Length, mask);
    }

    [TestMethod]
    public async Task PickingADocumentedValueQueuesIt()
    {
        var (vm, _, _) = await PageAsync();
        var row = vm.AllRows.Single(r => r.Name == "FS_GCS_ENABLE");
        var land = row.Choices.Single(c => c.Label == "Land");

        Assert.IsTrue(await row.ChooseAsync(land), "the value cell's list queues through the page");
        Assert.AreEqual(5, vm.Edits.Pending["FS_GCS_ENABLE"]);
        Assert.AreEqual("Land", row.ValueLabel);
        Assert.IsTrue(row.IsModified);
        Assert.AreEqual("3 parameters · 1 unsaved", vm.Summary);

        // a typed label works as well; back to the vehicle's value drops the edit
        Assert.IsFalse(await vm.CommitEditAsync(row, "Disabled/NoAction"));
        Assert.IsFalse(row.IsModified);
        Assert.AreEqual(0, vm.PendingCount);
    }

    [TestMethod]
    public async Task ReadOnlyAndOutOfRangeChecksApplyToPickedValues()
    {
        var (vm, dialogs, _) = await PageAsync();

        var ro = new ParamRow("STAT_BOOTCNT", 3, null, Meta("STAT_BOOTCNT", readOnly: true, values: [new(0, "Zero")]));
        Assert.IsFalse(await vm.QueueValueAsync(ro, 0));
        Assert.AreEqual("ReadOnly", dialogs.Messages.Last().Title);

        var speed = vm.AllRows.Single(r => r.Name == "RC_SPEED");
        dialogs.ConfirmAnswer = false;
        Assert.IsFalse(await vm.QueueValueAsync(speed, 5), "below the documented 50 to 490");
        Assert.AreEqual("Out of range", dialogs.Confirms.Last().Title);
        Assert.AreEqual(0, vm.PendingCount);
    }

    [TestMethod]
    public async Task SetBitmaskQueuesTheCombinedValue()
    {
        var (vm, _, windows) = await PageAsync();
        var row = vm.AllRows.Single(r => r.Name == "LOG_BITMASK");
        BitmaskEditorViewModel? shown = null;
        windows.OnDialog = page =>
        {
            shown = (BitmaskEditorViewModel)page;
            shown.Documented.Single(b => b.Bit == 0).IsChecked = true;
            shown.Documented.Single(b => b.Bit == 2).IsChecked = false;
            return true;
        };

        // the "Set bitmask" button
        await row.EditBitmaskCommand.ExecuteAsync(null);

        Assert.IsNotNull(shown);
        Assert.AreEqual("LOG_BITMASK", shown.Name);
        CollectionAssert.AreEqual(new[] { 16 }, shown.Undocumented.Select(b => b.Bit).ToArray());
        var expected = 1 + 2 + 65536;
        Assert.AreEqual(expected, vm.Edits.Pending["LOG_BITMASK"], "bit 0 set, bit 2 cleared, bit 16 kept");
        Assert.AreEqual(expected, row.DisplayValue);
        Assert.AreEqual($"LOG_BITMASK = {expected} (not written yet)", vm.StatusText);
    }

    [TestMethod]
    public async Task CancelledOrReadOnlyBitmaskChangesNothing()
    {
        var (vm, dialogs, windows) = await PageAsync();
        windows.OnDialog = page =>
        {
            ((BitmaskEditorViewModel)page).Documented[0].IsChecked = true;
            return false;
        };

        Assert.IsFalse(await vm.EditBitmaskAsync(vm.AllRows.Single(r => r.Name == "LOG_BITMASK")));
        Assert.AreEqual(0, vm.PendingCount);

        var shownBefore = windows.Shown.Count;
        var ro = new ParamRow("LOG_BITMASK", 518, null, Meta("LOG_BITMASK", bits: LogBits, readOnly: true));
        Assert.IsFalse(await vm.EditBitmaskAsync(ro));
        Assert.AreEqual(shownBefore, windows.Shown.Count, "no editor for a read-only parameter");
        Assert.AreEqual("ReadOnly", dialogs.Messages.Last().Title);
    }

    [TestMethod]
    public async Task RebootNeedsAConnectionAndWarnsWhenArmed()
    {
        var (vm, dialogs, _) = await PageAsync();
        vm.Tick();
        Assert.IsFalse(vm.RebootCommand.CanExecute(null), "disabled while disconnected");

        await vm.RebootCommand.ExecuteAsync(null);
        Assert.AreEqual(0, dialogs.Confirms.Count, "nothing to confirm without a vehicle");
        Assert.AreEqual("Not connected", dialogs.Messages.Last().Title);

        var (title, message) = ParamPageViewModel.RebootConfirmText(armed: true, pendingEdits: 2);
        StringAssert.Contains(title, "ARMED");
        StringAssert.Contains(message, "DON'T DO THIS IF YOU ARE IN THE AIR");
        StringAssert.Contains(message, "2 edits have not been written");

        (title, message) = ParamPageViewModel.RebootConfirmText(armed: false, pendingEdits: 0);
        Assert.AreEqual("Reboot", title);
        Assert.IsFalse(message.Contains("ARMED"));
        Assert.IsFalse(message.Contains("not been written"));
    }
}
