using MissionPlanner.App.Core.Params;
using MissionPlanner.App.Core.ViewModels.ConfigScreens;
using MissionPlanner.ArduPilot;
using MissionPlanner.Utilities;
using static MissionPlanner.App.Tests.ConfigScreens.ParamTestSupport;

namespace MissionPlanner.App.Tests.ConfigScreens;

[TestClass]
public class ParamListLogicTests
{
    [TestMethod]
    public void NaturalOrderPutsSmallNumbersFirst()
    {
        var names = new List<string> { "SERVO10_MIN", "SERVO2_MIN", "SERVO1_MIN", "serial1_baud", "SERIAL0_BAUD", "ACRO_YAW_P" };
        names.Sort(NaturalStringComparer.Instance);
        CollectionAssert.AreEqual(new[] { "ACRO_YAW_P", "SERIAL0_BAUD", "serial1_baud", "SERVO1_MIN", "SERVO2_MIN", "SERVO10_MIN" }, names);
    }

    [TestMethod]
    public void GroupsArePrefixesBeforeTheFirstUnderscore()
    {
        Assert.AreEqual("BATT", ParamGroups.GroupOf("BATT_MONITOR"));
        Assert.AreEqual("BATT2", ParamGroups.GroupOf("BATT2_MONITOR"));
        Assert.AreEqual("Q", ParamGroups.GroupOf("Q_A_RAT_RLL_P"));
        Assert.AreEqual("SYSIDTHISMAV", ParamGroups.GroupOf("SYSIDTHISMAV"), "no underscore: its own group");

        var groups = ParamGroups.Build(["BATT_MONITOR", "ACRO_YAW_P", "BATT_CAPACITY", "SERVO10_MIN", "SERVO2_MIN", "BATT2_MONITOR"]);

        CollectionAssert.AreEqual(new[] { "All", "ACRO", "BATT", "BATT2", "SERVO2", "SERVO10" }, groups.Select(g => g.Name).ToArray());
        Assert.IsTrue(groups[0].IsAll);
        Assert.AreEqual(6, groups[0].Count);
        Assert.AreEqual(2, groups.Single(g => g.Name == "BATT").Count);
        Assert.IsTrue(groups.Single(g => g.Name == "BATT").Matches("BATT_CAPACITY"));
        Assert.IsFalse(groups.Single(g => g.Name == "BATT").Matches("BATT2_MONITOR"));
        Assert.IsTrue(groups[0].Matches("ANYTHING"));
    }

    private static List<ParamRow> SampleRows()
    {
        var rows = new List<ParamRow>
        {
            new("ATC_RAT_RLL_P", 0.135, 0.135, Meta("ATC_RAT_RLL_P", "Roll axis rate controller P gain", "Roll axis rate controller P gain.  Corrects in proportion to the difference")),
            new("ATC_RAT_PIT_P", 0.2, 0.135, Meta("ATC_RAT_PIT_P", "Pitch axis rate controller P gain", "Pitch axis rate controller P gain.")),
            new("BATT_MONITOR", 4, 0, Meta("BATT_MONITOR", "Battery monitoring", "Controls enabling monitoring of the battery's voltage and current")),
            new("BATT_CAPACITY", 3300, null, Meta("BATT_CAPACITY", "Battery capacity", "Capacity of the battery in mAh when full")),
            new("FRAME_CLASS", 1, 0, null),
        };
        return rows;
    }

    [TestMethod]
    public void SearchMatchesNameAndDocumentation()
    {
        var rows = SampleRows();

        CollectionAssert.AreEqual(new[] { "ATC_RAT_RLL_P", "ATC_RAT_PIT_P" },
            ParamFilter.Apply(rows, new ParamFilterOptions(Search: "rat_")).Select(r => r.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "BATT_MONITOR", "BATT_CAPACITY" },
            ParamFilter.Apply(rows, new ParamFilterOptions(Search: "battery")).Select(r => r.Name).ToArray(), "description, any case");
        CollectionAssert.AreEqual(new[] { "ATC_RAT_PIT_P" },
            ParamFilter.Apply(rows, new ParamFilterOptions(Search: "pitch gain")).Select(r => r.Name).ToArray(), "every word must match");
        CollectionAssert.AreEqual(new[] { "ATC_RAT_RLL_P", "ATC_RAT_PIT_P" },
            ParamFilter.Apply(rows, new ParamFilterOptions(Search: "ATC*_P")).Select(r => r.Name).ToArray(), "wildcards");
        Assert.AreEqual(5, ParamFilter.Apply(rows, new ParamFilterOptions(Search: "  ")).Count);
        Assert.AreEqual(0, ParamFilter.Apply(rows, new ParamFilterOptions(Search: "zzz")).Count);
    }

    [TestMethod]
    public void GroupModifiedAndNonDefaultFiltersCombine()
    {
        var rows = SampleRows();
        var batt = ParamGroups.Build(rows.Select(r => r.Name)).Single(g => g.Name == "BATT");

        CollectionAssert.AreEqual(new[] { "BATT_MONITOR", "BATT_CAPACITY" },
            ParamFilter.Apply(rows, new ParamFilterOptions(Group: batt)).Select(r => r.Name).ToArray());

        // non-default: known default and different value (BATT_CAPACITY has no default)
        CollectionAssert.AreEqual(new[] { "ATC_RAT_PIT_P", "BATT_MONITOR", "FRAME_CLASS" },
            ParamFilter.Apply(rows, new ParamFilterOptions(NonDefaultOnly: true)).Select(r => r.Name).ToArray());

        Assert.AreEqual(0, ParamFilter.Apply(rows, new ParamFilterOptions(ModifiedOnly: true)).Count);
        rows[0].PendingValue = 0.2;
        rows[3].PendingValue = 5000;
        CollectionAssert.AreEqual(new[] { "ATC_RAT_RLL_P", "BATT_CAPACITY" },
            ParamFilter.Apply(rows, new ParamFilterOptions(ModifiedOnly: true)).Select(r => r.Name).ToArray());

        // an edit away from the default makes the row non-default
        Assert.IsTrue(rows[0].IsNonDefault);
        CollectionAssert.AreEqual(new[] { "BATT_CAPACITY" },
            ParamFilter.Apply(rows, new ParamFilterOptions(Group: batt, Search: "capacity", ModifiedOnly: true)).Select(r => r.Name).ToArray());
    }

    [TestMethod]
    public void FileLoadSortsIntoEditsUnchangedMissingAndReadOnly()
    {
        var vehicle = new Dictionary<string, double> { ["A"] = 1, ["B"] = (float)0.1, ["C"] = 3, ["RO"] = 7 };
        var file = new Dictionary<string, double> { ["A"] = 2, ["B"] = 0.1, ["C"] = 3, ["RO"] = 8, ["GONE"] = 1, ["GONE2"] = 5 };

        var result = ParamFileLoad.Compute(file, n => vehicle.TryGetValue(n, out var v) ? v : null,
            n => n == "RO" ? Meta("RO", readOnly: true) : null);

        CollectionAssert.AreEquivalent(new[] { "A" }, result.Edits.Keys.ToArray());
        Assert.AreEqual(2, result.Edits["A"]);
        CollectionAssert.AreEquivalent(new[] { "B", "C" }, result.Unchanged.ToArray());
        CollectionAssert.AreEqual(new[] { "GONE", "GONE2" }, result.Missing.ToArray());
        CollectionAssert.AreEqual(new[] { "RO" }, result.ReadOnlySkipped.ToArray());
    }

    [TestMethod]
    public void ParamFileIsReadIntoValues()
    {
        var path = TempFile("# saved\nATC_RAT_RLL_P,0.135\nBATT_MONITOR 4\nSYSID_SW_MREV,120\nFRAME_CLASS\t1\n");
        var file = ParamService.LoadFile(path);

        Assert.AreEqual(3, file.Count, "SYSID_SW_MREV is never loaded");
        Assert.AreEqual(0.135, file["ATC_RAT_RLL_P"], 1e-9);
        Assert.AreEqual(4, file["BATT_MONITOR"]);
        Assert.AreEqual(1, file["FRAME_CLASS"]);
    }

    [TestMethod]
    public void CompareFindsDifferencesBeyondFloatNoise()
    {
        var current = new Dictionary<string, double> { ["A"] = (float)0.1, ["B"] = 2, ["SERVO10_MIN"] = 1100, ["SERVO2_MIN"] = 1000 };
        var file = new Dictionary<string, double> { ["A"] = 0.1, ["B"] = 3, ["SERVO10_MIN"] = 1000, ["SERVO2_MIN"] = 1100, ["X"] = 1, ["Y"] = 2 };

        var diffs = ParamCompare.Diff(current, file, out var onlyInFile);

        CollectionAssert.AreEqual(new[] { "B", "SERVO2_MIN", "SERVO10_MIN" }, diffs.Select(d => d.Name).ToArray());
        Assert.AreEqual(2, diffs[0].Current);
        Assert.AreEqual(3, diffs[0].FromFile);
        Assert.AreEqual(2, onlyInFile);
    }

    [TestMethod]
    public void CompareDialogAppliesOnlyTickedRows()
    {
        var diffs = new List<ParamDifference> { new("A", 1, 2), new("B", 1, 3), new("RO", 1, 4) };
        var page = new ParamCompareViewModel("x.param", diffs, 1, n => n == "RO" ? Meta("RO", readOnly: true) : null);

        Assert.IsFalse(page.Rows.Single(r => r.Name == "RO").Use, "read-only rows start unticked");
        page.Rows.Single(r => r.Name == "B").Use = false;
        CollectionAssert.AreEquivalent(new[] { "A" }, page.Selected.Keys.ToArray());

        page.AllChecked = true;
        page.AllChecked = false;
        Assert.AreEqual(0, page.Selected.Count);
        page.AllChecked = true;
        CollectionAssert.AreEquivalent(new[] { "A", "B" }, page.Selected.Keys.ToArray(), "select all skips read-only");

        bool? closed = null;
        page.CloseRequested += (_, ok) => closed = ok;
        page.ApplyCommand.Execute(null);
        Assert.IsTrue(closed);
    }

    [TestMethod]
    public void FavouritesAreStoredAndListedFirst()
    {
        try
        {
            ParamFavourites.Set("BATT_MONITOR", true);
            ParamFavourites.Set("ZZZ_LAST", true);
            ParamFavourites.Set("BATT_MONITOR", true);
            Assert.AreEqual(2, Settings.Instance.GetList(ParamFavourites.SettingsKey).Count());

            var favs = ParamFavourites.Load();
            var names = new List<string> { "ACRO_YAW_P", "BATT_MONITOR", "ZZZ_LAST", "AHRS_EKF_TYPE" };
            names.Sort(ParamFavourites.Order(favs));
            CollectionAssert.AreEqual(new[] { "BATT_MONITOR", "ZZZ_LAST", "ACRO_YAW_P", "AHRS_EKF_TYPE" }, names);

            ParamFavourites.Set("BATT_MONITOR", false);
            ParamFavourites.Set("ZZZ_LAST", false);
            Assert.IsFalse(Settings.Instance.ContainsKey(ParamFavourites.SettingsKey), "the last removal clears the key");
        }
        finally
        {
            Settings.Instance.Remove(ParamFavourites.SettingsKey);
        }
    }

    [TestMethod]
    public void OptionsTextShowsRangeValuesAndBits()
    {
        var withValues = Meta("FS_THR_ENABLE", min: 0, max: 2,
            values: [new(0, "Disabled"), new(1, "Enabled always RTL")]);
        var (shortText, full) = ParamText.Options(withValues);
        Assert.AreEqual("0 to 2  0:Disabled, 1:Enabled always RTL", shortText);
        Assert.AreEqual("Range: 0 to 2\n0:Disabled\n1:Enabled always RTL", full);

        var bits = Meta("LOG_BITMASK", bits: [new(0, "Fast Attitude"), new(2, "GPS")]);
        Assert.AreEqual("Bitmask 0:Fast Attitude, 2:GPS", ParamText.Options(bits).Short);

        Assert.AreEqual(("", ""), ParamText.Options(null));
    }

    [TestMethod]
    public void GuessesTheVehicleOfAParameterFile()
    {
        Assert.AreEqual(Firmwares.ArduPlane, FirmwareGuess.FromParamNames(["TECS_SPDWEIGHT", "Q_ENABLE"]));
        Assert.AreEqual(Firmwares.ArduRover, FirmwareGuess.FromParamNames(["CRUISE_SPEED", "CRUISE_THROTTLE"]));
        Assert.AreEqual(Firmwares.ArduSub, FirmwareGuess.FromParamNames(["SURFACE_DEPTH"]));
        Assert.AreEqual(Firmwares.ArduCopter2, FirmwareGuess.FromParamNames(["ATC_RAT_RLL_P", "FRAME_CLASS"]));
    }
}
