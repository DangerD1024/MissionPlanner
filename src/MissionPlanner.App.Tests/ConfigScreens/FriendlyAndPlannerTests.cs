using MissionPlanner.App.Core.ViewModels.ConfigScreens;
using MissionPlanner.Utilities;
using static MissionPlanner.App.Tests.ConfigScreens.ParamTestSupport;

namespace MissionPlanner.App.Tests.ConfigScreens;

[TestClass]
public class FriendlyAndPlannerTests
{
    [TestMethod]
    public void StandardShowsStandardAdvancedShowsEverythingDocumented()
    {
        var standard = Meta("BATT_MONITOR", "Battery monitoring", user: "Standard");
        var advanced = Meta("INS_GYRO_FILTER", "Gyro filter cutoff frequency", user: "Advanced");
        var unlabelled = Meta("SCR_USER1", "Scripting User Parameter1", user: "");
        var undocumented = Meta("X_Y", "", user: "Standard");

        Assert.IsTrue(FriendlyParamsViewModel.Includes(standard, includeAdvanced: false));
        Assert.IsFalse(FriendlyParamsViewModel.Includes(advanced, includeAdvanced: false));
        Assert.IsFalse(FriendlyParamsViewModel.Includes(unlabelled, includeAdvanced: false));
        Assert.IsTrue(FriendlyParamsViewModel.Includes(standard, includeAdvanced: true));
        Assert.IsTrue(FriendlyParamsViewModel.Includes(advanced, includeAdvanced: true));
        Assert.IsTrue(FriendlyParamsViewModel.Includes(unlabelled, includeAdvanced: true));
        Assert.IsFalse(FriendlyParamsViewModel.Includes(undocumented, includeAdvanced: true), "needs a display name");
        Assert.IsFalse(FriendlyParamsViewModel.Includes(null, includeAdvanced: true));
    }

    [TestMethod]
    public void EditorFollowsTheDocumentation()
    {
        var edits = new List<(string, double)>();
        void Edit(FriendlyParamItem i, double v) => edits.Add((i.Name, v));

        var options = new FriendlyParamItem("FS_THR_ENABLE", Meta("FS_THR_ENABLE", "Throttle Failsafe Enable",
            values: [new(0, "Disabled"), new(1, "Enabled always RTL"), new(2, "Enabled Continue with Mission in Auto Mode")]), 1, Edit);
        Assert.IsTrue(options.IsOptions);
        Assert.AreEqual("Enabled always RTL", options.SelectedOption!.Label);
        Assert.AreEqual(0, edits.Count, "showing the value is not an edit");
        options.SelectedOption = options.Options[2];
        CollectionAssert.AreEqual(new[] { ("FS_THR_ENABLE", 2.0) }, edits);
        options.SelectedOption = null;
        Assert.AreEqual(1, edits.Count, "null from a recycled combo box is ignored");

        var unknown = new FriendlyParamItem("FS_THR_ENABLE", options.Meta, 7, Edit);
        Assert.AreEqual(4, unknown.Options.Count, "an undocumented value is still shown");
        Assert.AreEqual(7, unknown.SelectedOption!.Value);

        var bits = new FriendlyParamItem("LOG_BITMASK", Meta("LOG_BITMASK", "Log bitmask",
            bits: [new(0, "Fast Attitude"), new(2, "GPS"), new(4, "CTUN")]), 5, Edit);
        Assert.IsTrue(bits.IsBitmask);
        CollectionAssert.AreEqual(new[] { true, true, false }, bits.Bits.Select(b => b.IsChecked).ToArray());
        edits.Clear();
        bits.Bits[2].IsChecked = true;
        CollectionAssert.AreEqual(new[] { ("LOG_BITMASK", 21.0) }, edits);
        bits.SetPending(21);
        bits.Bits[0].IsChecked = false;
        Assert.AreEqual(20.0, edits.Last().Item2, "bits build on the pending value");

        var number = new FriendlyParamItem("WPNAV_SPEED", Meta("WPNAV_SPEED", "Waypoint Horizontal Speed Target", units: "cm/s",
            min: 20, max: 2000, increment: 50), 1000, Edit);
        Assert.IsTrue(number.IsNumber);
        Assert.AreEqual(1000m, number.NumericValue);
        Assert.AreEqual(20m, number.Minimum);
        Assert.AreEqual(2000m, number.Maximum);
        Assert.AreEqual(50m, number.Increment);
        Assert.AreEqual("20 to 2000, step 50", number.RangeText);
        edits.Clear();
        number.NumericValue = 1500;
        CollectionAssert.AreEqual(new[] { ("WPNAV_SPEED", 1500.0) }, edits);

        // a value already outside the range widens the box instead of being clamped
        var outside = new FriendlyParamItem("WPNAV_SPEED", number.Meta, 5000, Edit);
        Assert.AreEqual(5000m, outside.Maximum);
        Assert.IsTrue(outside.IsOutOfRange);
        Assert.AreEqual(0.15m, FriendlyParamItem.ToDecimal((float)0.15));
    }

    [TestMethod]
    public async Task FriendlyPageQueuesEditsAndDropsReverts()
    {
        var (services, _, _) = CreateServices();
        AddParams(services, ("BATT_MONITOR", 4, null));
        var page = new StandardParamsViewModel(services);
        await page.RebuildAsync();
        Assert.AreEqual(0, page.AllItems.Count, "nothing documented yet: no metadata loaded in tests");

        var item = new FriendlyParamItem("BATT_MONITOR", Meta("BATT_MONITOR", "Battery monitoring", min: 0, max: 30), 4,
            (i, v) => page.Edits.Set(i.Name, v, i.Value));
        item.NumericValue = 3;
        Assert.AreEqual(1, page.PendingCount);
        item.NumericValue = 4;
        Assert.AreEqual(0, page.PendingCount);
    }

    [TestMethod]
    public void UnitsFollowMissionPlannersMultipliers()
    {
        Assert.AreEqual((1f, "m"), PlannerSettings.Distance(nameof(distances.Meters)));
        Assert.AreEqual((3.2808399f, "ft"), PlannerSettings.Distance(nameof(distances.Feet)));
        Assert.AreEqual((1f, "m"), PlannerSettings.Distance(null));
        Assert.AreEqual((3.6f, "kph"), PlannerSettings.Speed(nameof(speeds.kph)));
        Assert.AreEqual((1.94384449f, "kts"), PlannerSettings.Speed(nameof(speeds.knots)));
        Assert.AreEqual((1f, "m/s"), PlannerSettings.Speed("rubbish"));

        var saved = (Settings.Instance[PlannerSettings.DistanceKey], Settings.Instance[PlannerSettings.SpeedKey]);
        try
        {
            Settings.Instance[PlannerSettings.DistanceKey] = nameof(distances.Feet);
            Settings.Instance[PlannerSettings.SpeedKey] = nameof(speeds.mph);
            PlannerSettings.ApplyUnits();
            Assert.AreEqual(3.2808399f, CurrentState.multiplierdist);
            Assert.AreEqual("ft", CurrentState.DistanceUnit);
            Assert.AreEqual("mph", CurrentState.SpeedUnit);
            Assert.AreEqual("m", CurrentState.AltUnit);
        }
        finally
        {
            Restore(PlannerSettings.DistanceKey, saved.Item1);
            Restore(PlannerSettings.SpeedKey, saved.Item2);
            PlannerSettings.ApplyUnits();
        }
    }

    [TestMethod]
    public void StoredStreamRatesBecomeTheConnectionDefaults()
    {
        var before = CurrentState.ratercbackup;
        try
        {
            Settings.Instance["CMB_raterc"] = "10";
            PlannerSettings.ApplyStored();
            Assert.AreEqual(10, CurrentState.ratercbackup);

            var cs = new CurrentState();
            var streams = PlannerSettings.Apply(cs, PlannerSettings.StreamGroup.Attitude, 8);
            Assert.AreEqual(8, cs.rateattitude);
            CollectionAssert.AreEqual(new[] { MAVLink.MAV_DATA_STREAM.EXTRA1, MAVLink.MAV_DATA_STREAM.EXTRA2 }, streams);
        }
        finally
        {
            Settings.Instance.Remove("CMB_raterc");
            CurrentState.ratercbackup = before;
        }
    }

    private static void Restore(string key, string? value)
    {
        if (value == null)
            Settings.Instance.Remove(key);
        else
            Settings.Instance[key] = value;
    }
}
