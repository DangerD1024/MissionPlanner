using MissionPlanner.App.Core.ViewModels.ConfigScreens;
using static MissionPlanner.App.Tests.ConfigScreens.ParamTestSupport;

namespace MissionPlanner.App.Tests.ConfigScreens;

[TestClass]
public class ParamTreeTests
{
    [TestMethod]
    public void GroupsByPrefixAndSubPrefix()
    {
        var tree = ParamTree.Build([
            "ATC_RAT_PIT_P", "ATC_RAT_PIT_I", "ATC_RAT_RLL_P", "ATC_RAT_RLL_I", "ATC_ANGLE_MAX", "ATC_ACCEL_P_MAX",
            "SYSID_THISMAV", "SYSID_MYGCS", "FORMAT_VERSION", "SERVO10_MIN", "SERVO2_MIN", "SERVO2_MAX",
        ]);

        Assert.AreEqual(
            "ATC(ATC_ACCEL_P_MAX,ATC_ANGLE_MAX,ATC_RAT(ATC_RAT_PIT(ATC_RAT_PIT_I,ATC_RAT_PIT_P),ATC_RAT_RLL(ATC_RAT_RLL_I,ATC_RAT_RLL_P))) " +
            "FORMAT_VERSION SERVO2(SERVO2_MAX,SERVO2_MIN) SERVO10_MIN SYSID(SYSID_MYGCS,SYSID_THISMAV)",
            string.Join(" ", tree));
        Assert.AreEqual(6, tree[0].Count);
        Assert.IsFalse(tree[1].IsGroup, "a prefix of one parameter is no group");
    }

    [TestMethod]
    public void AGroupHoldingOnlyASubGroupIsMerged()
    {
        var tree = ParamTree.Build(["Q_TAILSIT_ANGLE", "Q_TAILSIT_ENABLE", "Q_TAILSIT_INPUT"]);

        Assert.AreEqual("Q_TAILSIT(Q_TAILSIT_ANGLE,Q_TAILSIT_ENABLE,Q_TAILSIT_INPUT)", string.Join(" ", tree));
    }

    [TestMethod]
    public void ANameEqualToAPrefixStaysAParameter()
    {
        // Mission Planner's edge case: Q_PLT_Y_RATE and Q_PLT_Y_RATE_TC
        var tree = ParamTree.Build(["Q_PLT_Y_RATE", "Q_PLT_Y_RATE_TC", "Q_PLT_Y_EXPO", "BATT_MONITOR", "BATT", "BATT_CAPACITY"]);

        Assert.AreEqual("BATT BATT(BATT_CAPACITY,BATT_MONITOR) Q_PLT_Y(Q_PLT_Y_EXPO,Q_PLT_Y_RATE,Q_PLT_Y_RATE_TC)", string.Join(" ", tree));
        Assert.AreEqual(6, tree.Sum(n => n.Count));
    }

    private static async Task<(FullParameterTreeViewModel Vm, FakeDialogs Dialogs)> TreeAsync()
    {
        var (services, dialogs, _) = CreateServices();
        AddParams(services,
            ("ATC_RAT_PIT_P", 0.135, null), ("ATC_RAT_PIT_I", 0.135, null), ("ATC_RAT_RLL_P", 0.135, null),
            ("ATC_RAT_RLL_I", 0.135, null), ("ATC_ANGLE_MAX", 30, null),
            ("BATT_MONITOR", 4, null), ("BATT_CAPACITY", 3300, null), ("FORMAT_VERSION", 120, null));
        var vm = new FullParameterTreeViewModel(services);
        await vm.RebuildAsync();
        return (vm, dialogs);
    }

    private static string Lines(FullParameterTreeViewModel vm) => string.Join(" ", vm.Items.Select(i => new string('.', i.Depth) + i.Label));

    [TestMethod]
    public async Task StartsClosedAndOpensGroupByGroup()
    {
        var (vm, _) = await TreeAsync();

        Assert.AreEqual("ATC BATT FORMAT_VERSION", Lines(vm));
        Assert.AreEqual("8 parameters", vm.Summary);
        var atc = vm.Items[0];
        Assert.AreEqual("5", atc.CountText);

        atc.ToggleCommand.Execute(null);
        Assert.AreEqual("ATC .ATC_ANGLE_MAX .ATC_RAT BATT FORMAT_VERSION", Lines(vm));
        Assert.AreEqual(ParamTreeItem.IndentStep, vm.Items[1].Indent);

        vm.Toggle(vm.Items[2]);
        Assert.AreEqual("ATC .ATC_ANGLE_MAX .ATC_RAT ..ATC_RAT_PIT ..ATC_RAT_RLL BATT FORMAT_VERSION", Lines(vm));

        // closing removes the whole branch; opening again restores the open sub-group
        vm.Toggle(atc);
        Assert.AreEqual("ATC BATT FORMAT_VERSION", Lines(vm));
        vm.Toggle(atc);
        Assert.AreEqual("ATC .ATC_ANGLE_MAX .ATC_RAT ..ATC_RAT_PIT ..ATC_RAT_RLL BATT FORMAT_VERSION", Lines(vm));

        // open groups stay open when the list is rebuilt
        await vm.RebuildAsync();
        Assert.AreEqual("ATC .ATC_ANGLE_MAX .ATC_RAT ..ATC_RAT_PIT ..ATC_RAT_RLL BATT FORMAT_VERSION", Lines(vm));
    }

    [TestMethod]
    public async Task SearchOpensTheGroupsHoldingMatches()
    {
        var (vm, _) = await TreeAsync();

        vm.SearchText = "rll";
        vm.ApplyFilter();
        Assert.AreEqual("ATC .ATC_RAT ..ATC_RAT_RLL ...ATC_RAT_RLL_I ...ATC_RAT_RLL_P", Lines(vm));
        Assert.AreEqual("2 of 8 parameters", vm.Summary);
        Assert.AreEqual("2 of 5", vm.Items[0].CountText);

        // closing during a search works; the next search opens everything again
        vm.Toggle(vm.Items[1]);
        Assert.AreEqual("ATC .ATC_RAT", Lines(vm));
        vm.SearchText = "capacity";
        vm.ApplyFilter();
        Assert.AreEqual("BATT .BATT_CAPACITY", Lines(vm));

        vm.SearchText = "nothing like this";
        vm.ApplyFilter();
        Assert.IsTrue(vm.NoMatches);

        // without a search, the groups the user had open
        vm.SearchText = "";
        vm.ApplyFilter();
        Assert.AreEqual("ATC BATT FORMAT_VERSION", Lines(vm));
        Assert.AreEqual("5", vm.Items[0].CountText);
    }

    [TestMethod]
    public async Task ExpandAndCollapseAll()
    {
        var (vm, _) = await TreeAsync();

        vm.ExpandAllCommand.Execute(null);
        Assert.AreEqual(13, vm.Items.Count, "5 groups and 8 parameters");
        vm.CollapseAllCommand.Execute(null);
        Assert.AreEqual("ATC BATT FORMAT_VERSION", Lines(vm));
    }

    [TestMethod]
    public async Task EditsInTheTreeAreQueuedForWriting()
    {
        var (vm, _) = await TreeAsync();
        var row = vm.AllRows.Single(r => r.Name == "BATT_CAPACITY");

        Assert.IsTrue(await vm.CommitEditAsync(row, "5000"));
        Assert.AreEqual(5000, vm.Edits.Pending["BATT_CAPACITY"]);
        Assert.AreEqual("8 parameters · 1 unsaved", vm.Summary);

        // the edit survives a rebuild (new rows, same queue)
        await vm.RebuildAsync();
        Assert.IsTrue(vm.AllRows.Single(r => r.Name == "BATT_CAPACITY").IsModified);

        vm.DiscardEditsCommand.Execute(null);
        Assert.AreEqual(0, vm.PendingCount);
        Assert.IsFalse(vm.AllRows.Single(r => r.Name == "BATT_CAPACITY").IsModified);
        Assert.IsFalse(vm.WriteCommand.CanExecute(null), "no writing without a vehicle");
    }
}
