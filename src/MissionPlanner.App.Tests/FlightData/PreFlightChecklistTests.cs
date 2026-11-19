using MissionPlanner.App.Core.Maps;
using MissionPlanner.App.Core.ViewModels.FlightData;

namespace MissionPlanner.App.Tests.FlightData;

[TestClass]
public class PreFlightChecklistTests
{
    [ClassInitialize]
    public static void Init(TestContext _) =>
        MapTiles.Initialize(Path.Combine(TestEnvironment.DataRoot, "gmapcache") + Path.DirectorySeparatorChar);

    [TestCleanup]
    public void Cleanup()
    {
        if (File.Exists(ChecklistFile.UserPath))
            File.Delete(ChecklistFile.UserPath);
    }

    private static ChecklistItem Item(string name, ChecklistCondition c, double trigger, string text = "{value}") =>
        new() { Description = name, Name = name, ConditionType = c, TriggerValue = trigger, Text = text };

    [TestMethod]
    public void BuiltInDefaultIsMissionPlannersList()
    {
        var items = ChecklistFile.BuiltInDefault();
        Assert.HasCount(12, items);
        Assert.AreEqual("gpsstatus", items[0].Name);
        Assert.AreEqual(ChecklistCondition.GTEQ, items[0].ConditionType);
        Assert.AreEqual(3, items[0].TriggerValue);
        Assert.AreEqual("{value} >= 3", items[0].Text, "XML entities decoded");
        Assert.AreEqual("Green", items[0].TrueColor);
        Assert.AreEqual("Red", items[0].FalseColor);
        Assert.AreEqual(ChecklistCondition.NONE, items[^1].ConditionType);
        Assert.AreEqual("", items[^1].Text, "<Text /> is empty");
    }

    [TestMethod]
    public void BuiltInCopyMatchesTheRepositoryFile()
    {
        // the repository root holds the original next to the WinForms project
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, ChecklistFile.DefaultFileName)))
            dir = dir.Parent;
        if (dir == null)
            Assert.Inconclusive("checklistDefault.xml not found above the test folder");

        var original = ChecklistFile.Parse(File.ReadAllText(Path.Combine(dir!.FullName, ChecklistFile.DefaultFileName)));
        var copy = ChecklistFile.BuiltInDefault();
        Assert.HasCount(original.Count, copy);
        for (var i = 0; i < copy.Count; i++)
        {
            Assert.AreEqual(original[i].Description, copy[i].Description);
            Assert.AreEqual(original[i].Name, copy[i].Name);
            Assert.AreEqual(original[i].Text, copy[i].Text);
            Assert.AreEqual(original[i].ConditionType, copy[i].ConditionType);
            Assert.AreEqual(original[i].TriggerValue, copy[i].TriggerValue);
        }
    }

    [TestMethod]
    public void SaveAndParseRoundTripIncludingNestedConditions()
    {
        var items = new List<ChecklistItem>
        {
            Item("satcount", ChecklistCondition.GTEQ, 6.5, "{value} sats"),
            new()
            {
                Description = "Fence {FENCE_ENABLE}", Name = ChecklistEvaluator.ParamSource, ConditionType = ChecklistCondition.EQ,
                TriggerValue = 1, Text = "<{value}> & more", TrueColor = "Blue", FalseColor = "Orange",
                Child = Item("armed", ChecklistCondition.EQ, 0),
            },
        };
        var xml = ChecklistFile.Serialize(items);
        StringAssert.StartsWith(xml, "<?xml");
        StringAssert.Contains(xml, "<ArrayOfCheckListItem");

        var back = ChecklistFile.Parse(xml);
        Assert.HasCount(2, back);
        Assert.AreEqual(6.5, back[0].TriggerValue);
        Assert.AreEqual("<{value}> & more", back[1].Text);
        Assert.AreEqual("Blue", back[1].TrueColor);
        Assert.IsNotNull(back[1].Child);
        Assert.AreEqual("armed", back[1].Child!.Name);
        Assert.IsNull(back[0].Child);
    }

    [TestMethod]
    public void LoadPrefersTheUserFileThenTheDefaultThenTheBuiltInCopy()
    {
        var dir = Path.Combine(TestEnvironment.DataRoot, "checklist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var user = Path.Combine(dir, "checklist.xml");
        var def = Path.Combine(dir, "checklistDefault.xml");

        var list = ChecklistFile.Load(user, def, out var source);
        Assert.AreEqual("built-in", source);
        Assert.HasCount(12, list);

        ChecklistFile.Save([Item("roll", ChecklistCondition.NONE, 0)], def);
        list = ChecklistFile.Load(user, def, out source);
        Assert.AreEqual(def, source);
        Assert.HasCount(1, list);

        ChecklistFile.Save([Item("pitch", ChecklistCondition.NONE, 0), Item("yaw", ChecklistCondition.NONE, 0)], user);
        list = ChecklistFile.Load(user, def, out source);
        Assert.AreEqual(user, source);
        Assert.HasCount(2, list);

        // a broken user file falls back rather than failing
        File.WriteAllText(user, "<not xml");
        list = ChecklistFile.Load(user, def, out source);
        Assert.AreEqual(def, source);
    }

    [TestMethod]
    public void ConditionsCompareLikeMissionPlanner()
    {
        Assert.IsTrue(ChecklistEvaluator.Compare(ChecklistCondition.GTEQ, 3, 3));
        Assert.IsFalse(ChecklistEvaluator.Compare(ChecklistCondition.GT, 3, 3));
        Assert.IsTrue(ChecklistEvaluator.Compare(ChecklistCondition.LT, 2, 3));
        Assert.IsTrue(ChecklistEvaluator.Compare(ChecklistCondition.LTEQ, 3, 3));
        Assert.IsTrue(ChecklistEvaluator.Compare(ChecklistCondition.EQ, 1, 1));
        Assert.IsTrue(ChecklistEvaluator.Compare(ChecklistCondition.NEQ, 1, 2));
        Assert.IsFalse(ChecklistEvaluator.Compare(ChecklistCondition.NONE, 1, 1), "NONE is a manual tick box");
    }

    [TestMethod]
    public void LiveConditionsFollowTheVehicle()
    {
        var cs = new CurrentState();
        var gps = Item("gpsstatus", ChecklistCondition.GTEQ, 3, "{value} >= {trigger}");
        var sats = Item("satcount", ChecklistCondition.GTEQ, 5, "{value} Sats");
        var link = Item("linkqualitygcs", ChecklistCondition.GT, 95, "{value}%");

        cs.gpsstatus = 2;
        cs.satcount = 4;
        Assert.IsFalse(ChecklistEvaluator.Check(gps, cs, null));
        Assert.IsFalse(ChecklistEvaluator.Check(sats, cs, null));

        cs.gpsstatus = 3;
        cs.satcount = 11;
        Assert.IsTrue(ChecklistEvaluator.Check(gps, cs, null));
        Assert.IsTrue(ChecklistEvaluator.Check(sats, cs, null));
        Assert.AreEqual("3 >= 3", ChecklistEvaluator.DisplayText(gps, cs, null));
        Assert.AreEqual("11 Sats", ChecklistEvaluator.DisplayText(sats, cs, null));

        cs.linkqualitygcs = 100;
        Assert.IsTrue(ChecklistEvaluator.Check(link, cs, null));
        Assert.AreEqual("100%", ChecklistEvaluator.DisplayText(link, cs, null));
    }

    [TestMethod]
    public void TextValuesAndRounding()
    {
        var cs = new CurrentState { mode = "Loiter", roll = 1.23456f };
        Assert.AreEqual("Loiter", ChecklistEvaluator.DisplayText(Item("mode", ChecklistCondition.NONE, 0), cs, null));
        Assert.AreEqual("roll is 1.23", ChecklistEvaluator.DisplayText(Item("roll", ChecklistCondition.NONE, 0, "{name} is {value}"), cs, null));
        Assert.AreEqual(0, ChecklistEvaluator.Value(Item("mode", ChecklistCondition.EQ, 0), cs, null), "text compares as 0");
        Assert.AreEqual("Error", ChecklistEvaluator.DisplayText(Item("no_such_value", ChecklistCondition.NONE, 0), cs, null));
        Assert.AreEqual("", ChecklistEvaluator.DisplayText(Item("roll", ChecklistCondition.NONE, 0, ""), cs, null));
    }

    [TestMethod]
    public void ParameterItemsReadTheNameInBraces()
    {
        var cs = new CurrentState();
        var item = new ChecklistItem
        {
            Description = "Fence on {FENCE_ENABLE}", Name = ChecklistEvaluator.ParamSource, ConditionType = ChecklistCondition.EQ,
            TriggerValue = 1, Text = "{value}",
        };
        Assert.AreEqual("FENCE_ENABLE", ChecklistEvaluator.ParamName(item));
        Assert.IsFalse(ChecklistEvaluator.Check(item, cs, _ => null), "missing parameter reads 0");
        Assert.IsTrue(ChecklistEvaluator.Check(item, cs, n => n == "FENCE_ENABLE" ? 1 : null));
        Assert.AreEqual("1", ChecklistEvaluator.DisplayText(item, cs, _ => 1));
    }

    [TestMethod]
    public void NestedConditionMustAlsoHold()
    {
        var cs = new CurrentState { gpsstatus = 3, satcount = 4 };
        var item = Item("gpsstatus", ChecklistCondition.GTEQ, 3);
        item.Child = Item("satcount", ChecklistCondition.GTEQ, 6);
        Assert.IsFalse(ChecklistEvaluator.Check(item, cs, null));
        cs.satcount = 9;
        Assert.IsTrue(ChecklistEvaluator.Check(item, cs, null));
    }

    [TestMethod]
    public void ColoursFollowTheThemeForWhiteAndBlack()
    {
        Assert.IsNull(ChecklistEvaluator.ColorHex("White"));
        Assert.IsNull(ChecklistEvaluator.ColorHex("Black"));
        Assert.IsNull(ChecklistEvaluator.ColorHex(""));
        Assert.IsNotNull(ChecklistEvaluator.ColorHex("Green"));
        Assert.IsNotNull(ChecklistEvaluator.ColorHex("red"));
        Assert.AreEqual("#123456", ChecklistEvaluator.ColorHex("#123456"));
        Assert.Contains(ChecklistEvaluator.ParamSource, ChecklistEvaluator.Sources);
        Assert.Contains("gpsstatus", ChecklistEvaluator.Sources);
        Assert.Contains("mode", ChecklistEvaluator.Sources);
    }

    [TestMethod]
    public void TabTicksAutomaticLinesAndLeavesManualOnesToThePilot()
    {
        var services = FakeUi.Services(out _);
        var vm = new PreFlightViewModel(services);
        Assert.HasCount(12, vm.Rows);
        var gps = vm.Rows[0];
        var manual = vm.Rows[^1];
        Assert.IsTrue(gps.IsAutomatic);
        Assert.IsTrue(manual.IsManual);

        services.Session.Cs.gpsstatus = 3;
        vm.Update();
        Assert.IsTrue(gps.IsChecked);
        Assert.IsNotNull(gps.Color, "green when passed");

        services.Session.Cs.gpsstatus = 1;
        vm.Update();
        Assert.IsFalse(gps.IsChecked);

        manual.IsChecked = true;
        vm.Update();
        Assert.IsTrue(manual.IsChecked, "a manual tick survives updates");
        StringAssert.Contains(vm.Summary, "of 12");

        vm.ClearTicksCommand.Execute(null);
        Assert.IsFalse(manual.IsChecked);
    }

    [TestMethod]
    public void EditorSavesToTheDataFolderAndTheTabReloads()
    {
        var services = FakeUi.Services(out _);
        var vm = new PreFlightViewModel(services);
        var editor = new ChecklistEditorViewModel(services, vm.Rows.Select(r => r.Item));
        var closed = (bool?)null;
        editor.CloseRequested += (_, r) => closed = r;

        editor.AddCommand.Execute(null);
        editor.MoveUpCommand.Execute(editor.Rows[^1]);
        Assert.AreEqual("New check", editor.Rows[^2].Description);
        editor.RemoveCommand.Execute(editor.Rows[0]);
        editor.SaveCommand.Execute(null);

        Assert.IsTrue(closed);
        Assert.IsTrue(File.Exists(ChecklistFile.UserPath));
        vm.Reload();
        Assert.HasCount(12, vm.Rows);
        Assert.AreEqual(ChecklistFile.UserPath, vm.Source);
        Assert.AreEqual("Gps Sat Count", vm.Rows[0].Description);
        Assert.AreEqual("New check", vm.Rows[^2].Description);
    }
}
