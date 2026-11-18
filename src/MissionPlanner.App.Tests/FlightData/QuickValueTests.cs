using MissionPlanner.App.Core.ViewModels.FlightData;

namespace MissionPlanner.App.Tests.FlightData;

[TestClass]
public class QuickValueTests
{
    [TestMethod]
    public void FieldListHasNumericAndBoolPropertiesOnly()
    {
        var fields = QuickValueCatalog.ListFields();
        var names = fields.Select(f => f.Name).ToHashSet();

        foreach (var expected in new[] { "alt", "groundspeed", "wp_dist", "yaw", "verticalspeed", "DistToHome", "armed", "ch3out", "battery_voltage" })
            Assert.Contains(expected, names);

        // strings, objects and the message list are not quick values
        foreach (var excluded in new[] { "mode", "message", "messages", "HomeLocation", "Location" })
            Assert.DoesNotContain(excluded, names);

        Assert.AreEqual(names.Count, fields.Count, "no duplicates");
    }

    [TestMethod]
    public void FieldListIsSortedByLabel()
    {
        var labels = QuickValueCatalog.ListFields().Select(f => f.Label).ToList();
        var sorted = labels.OrderBy(l => l, StringComparer.OrdinalIgnoreCase).ToList();
        CollectionAssert.AreEqual(sorted, labels);
    }

    [TestMethod]
    public void UnnamedCustomFieldsAreHidden()
    {
        var names = QuickValueCatalog.ListFields().Select(f => f.Name).ToList();
        Assert.IsFalse(names.Any(n => n.StartsWith("customfield", StringComparison.Ordinal) &&
                                      !CurrentState.custom_field_names.ContainsKey(n)));
    }

    [TestMethod]
    public void GettersReadTheProperty()
    {
        var cs = new CurrentState();
        cs.altoffsethome = 0;
        cs.battery_voltage3 = 12.6;
        cs.ch3out = 1500;

        Assert.AreEqual(12.6, QuickValueCatalog.GetGetter("battery_voltage3")!(cs), 1e-9);
        Assert.AreEqual(1500, QuickValueCatalog.GetGetter("ch3out")!(cs), 1e-9);
        Assert.AreEqual(cs.alt, QuickValueCatalog.GetGetter("alt")!(cs), 1e-9);
    }

    [TestMethod]
    public void BoolGetterGivesZeroOrOne()
    {
        var cs = new CurrentState();
        var getter = QuickValueCatalog.GetGetter("armed")!;
        cs.armed = false;
        Assert.AreEqual(0, getter(cs));
        cs.armed = true;
        Assert.AreEqual(1, getter(cs));
    }

    [TestMethod]
    public void GettersAreCachedAndUnknownNamesHaveNone()
    {
        Assert.AreSame(QuickValueCatalog.GetGetter("yaw"), QuickValueCatalog.GetGetter("yaw"));
        Assert.IsNull(QuickValueCatalog.GetGetter("no_such_property"));
        Assert.IsNull(QuickValueCatalog.GetGetter("mode"), "strings are not numeric");
    }

    [TestMethod]
    public void LabelsSplitIntoNameAndDisplayUnit()
    {
        var saved = (CurrentState.DistanceUnit, CurrentState.SpeedUnit, CurrentState.AltUnit);
        try
        {
            CurrentState.DistanceUnit = "";
            CurrentState.SpeedUnit = "";
            CurrentState.AltUnit = "";
            Assert.AreEqual(new QuickField("x", "Ground Speed", "m/s"), QuickValueCatalog.SplitLabel("x", "Ground Speed (speed)"));
            Assert.AreEqual(new QuickField("x", "Altitude", "m"), QuickValueCatalog.SplitLabel("x", "Altitude (alt)"));
            Assert.AreEqual(new QuickField("x", "Dist to WP", "m"), QuickValueCatalog.SplitLabel("x", "Dist to WP (dist)"));
            Assert.AreEqual(new QuickField("x", "Yaw", "deg"), QuickValueCatalog.SplitLabel("x", "Yaw (deg)"));
            Assert.AreEqual(new QuickField("x", "Satellites", ""), QuickValueCatalog.SplitLabel("x", "Satellites"));

            CurrentState.AltUnit = "ft";
            Assert.AreEqual("ft", QuickValueCatalog.SplitLabel("x", "Altitude (alt)").Unit);
        }
        finally
        {
            (CurrentState.DistanceUnit, CurrentState.SpeedUnit, CurrentState.AltUnit) = saved;
        }
    }

    [TestMethod]
    public void DefaultTilesAreMissionPlannersSix()
    {
        var names = QuickDefault.All.Select(d => d.Name).ToArray();
        CollectionAssert.AreEqual(new[] { "alt", "groundspeed", "wp_dist", "yaw", "verticalspeed", "DistToHome" }, names);
        foreach (var d in QuickDefault.All)
            Assert.IsNotNull(QuickValueCatalog.GetGetter(d.Name), d.Name + " must also be pickable");
        Assert.AreEqual("Dist to MAV", QuickDefault.All[5].Describe().Label);
    }

    [TestMethod]
    public void TileShowsAnotherPropertyAndBack()
    {
        var cs = new CurrentState { battery_voltage3 = 11.1 };
        var tile = new QuickTile(0, QuickDefault.All[0]);
        Assert.IsTrue(tile.IsDefault);

        Assert.IsTrue(tile.Show("battery_voltage3"));
        Assert.IsFalse(tile.IsDefault);
        tile.Update(cs);
        Assert.AreEqual(11.1, tile.Value, 1e-9);

        Assert.IsFalse(tile.Show("mode"), "not numeric");
        Assert.AreEqual("battery_voltage3", tile.Field.Name, "a failed Show keeps the old field");

        Assert.IsTrue(tile.Show("alt"));
        Assert.IsTrue(tile.IsDefault);
    }

    [TestMethod]
    public void TileUpdateNotifiesOnlyOnChange()
    {
        var cs = new CurrentState { battery_voltage3 = 11.1 };
        var tile = new QuickTile(0, QuickDefault.All[0]);
        tile.Show("battery_voltage3");
        var changes = 0;
        tile.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(QuickTile.Value))
                changes++;
        };
        tile.Update(cs);
        tile.Update(cs);
        tile.Update(cs);
        Assert.AreEqual(1, changes);
    }

    [TestMethod]
    public void PickerFilterMatchesLabelUnitOrName()
    {
        var fields = new[]
        {
            new QuickField("gpshdop", "GPS HDOP", ""),
            new QuickField("alt", "Altitude", "m"),
            new QuickField("ch3out", "Ch 3 Out", ""),
        };
        Assert.HasCount(3, FlightDataViewModel.QuickFilterMatch(fields, ""));
        Assert.AreEqual("gpshdop", FlightDataViewModel.QuickFilterMatch(fields, "gps").Single().Name);
        Assert.AreEqual("alt", FlightDataViewModel.QuickFilterMatch(fields, "ALTI").Single().Name);
        Assert.AreEqual("ch3out", FlightDataViewModel.QuickFilterMatch(fields, "ch3").Single().Name, "property names match too");
        Assert.IsEmpty(FlightDataViewModel.QuickFilterMatch(fields, "zzz"));
    }
}
