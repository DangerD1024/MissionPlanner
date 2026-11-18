using MissionPlanner.App.Core.ViewModels.FlightData;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Tests.FlightData;

[TestClass]
public class TuningSampleBufferTests
{
    private static TuningSampleBuffer Make(int capacity = 8, double window = 30, params string[] keys)
    {
        var b = new TuningSampleBuffer(capacity, window);
        b.SetSeries(keys.Length == 0 ? ["a", "b"] : keys);
        return b;
    }

    private static double[] Times(TuningSampleBuffer b)
    {
        var t = new double[b.Capacity];
        return t[..b.CopyTimes(t)];
    }

    private static double[] Values(TuningSampleBuffer b, int series)
    {
        var v = new double[b.Capacity];
        return v[..b.CopyValues(series, v)];
    }

    [TestMethod]
    public void KeepsSamplesInOrder()
    {
        var b = Make();
        for (var i = 0; i < 5; i++)
            b.Add(i, [i * 10, -i]);

        Assert.AreEqual(5, b.Count);
        CollectionAssert.AreEqual(new double[] { 0, 1, 2, 3, 4 }, Times(b));
        CollectionAssert.AreEqual(new double[] { 0, 10, 20, 30, 40 }, Values(b, 0));
        CollectionAssert.AreEqual(new double[] { 0, -1, -2, -3, -4 }, Values(b, 1));
        Assert.AreEqual(4, b.LatestTime);
        Assert.AreEqual(0, b.OldestTime);
        Assert.AreEqual(5, b.ValidCount(0));
    }

    [TestMethod]
    public void WrapsAroundWhenFullDroppingTheOldest()
    {
        var b = Make(capacity: 4, window: 1000);
        for (var i = 0; i < 10; i++)
            b.Add(i, [i, i * 2]);

        Assert.AreEqual(4, b.Count);
        CollectionAssert.AreEqual(new double[] { 6, 7, 8, 9 }, Times(b), "copied oldest first across the wrap");
        CollectionAssert.AreEqual(new double[] { 12, 14, 16, 18 }, Values(b, 1));
        Assert.AreEqual(6, b.TimeAt(0));
        Assert.AreEqual(18, b.ValueAt(1, 3));
        Assert.AreEqual(4, b.ValidCount(0));
    }

    [TestMethod]
    public void DropsSamplesOlderThanTheWindow()
    {
        var b = Make(capacity: 100, window: 30);
        for (var t = 0.0; t <= 60; t += 5)
            b.Add(t, [t, 0]);

        CollectionAssert.AreEqual(new double[] { 30, 35, 40, 45, 50, 55, 60 }, Times(b));
        Assert.AreEqual(7, b.ValidCount(1));
    }

    [TestMethod]
    public void TimeGoingBackwardsStartsAgain()
    {
        var b = Make();
        b.Add(10, [1, 1]);
        b.Add(11, [2, 2]);
        b.Add(3, [3, 3]);

        CollectionAssert.AreEqual(new double[] { 3 }, Times(b));
        Assert.AreEqual(1, b.ValidCount(0));
    }

    [TestMethod]
    public void SeriesThatStayKeepTheirHistoryAndNewOnesStartEmpty()
    {
        var b = Make(capacity: 6, window: 1000, "roll", "pitch");
        for (var i = 0; i < 4; i++)
            b.Add(i, [i, 100 + i]);

        b.SetSeries(["pitch", "alt"]);
        Assert.AreEqual(4, b.ValidCount(0), "pitch kept its samples");
        Assert.AreEqual(0, b.ValidCount(1), "alt has none yet");
        CollectionAssert.AreEqual(new double[] { 100, 101, 102, 103 }, Values(b, 0));

        for (var i = 4; i < 9; i++)
            b.Add(i, [100 + i, 200 + i]);

        Assert.AreEqual(6, b.Count);
        Assert.AreEqual(6, b.ValidCount(0));
        Assert.AreEqual(5, b.ValidCount(1), "only the samples taken since it was added");
        var alt = Values(b, 1);
        CollectionAssert.AreEqual(new double[] { 204, 205, 206, 207, 208 }, alt[^5..]);
        CollectionAssert.AreEqual(new[] { "pitch", "alt" }, b.SeriesKeys.ToArray());
    }

    [TestMethod]
    public void ClearEmptiesEverySeries()
    {
        var b = Make();
        b.Add(1, [1, 2]);
        b.Clear();

        Assert.AreEqual(0, b.Count);
        Assert.AreEqual(0, b.ValidCount(0));
        Assert.IsTrue(double.IsNaN(b.LatestTime));
        b.Add(0.5, [3, 4]);
        CollectionAssert.AreEqual(new double[] { 0.5 }, Times(b));
    }

    [TestMethod]
    public void RejectsAWrongNumberOfValues()
    {
        var b = Make();
        Assert.ThrowsExactly<ArgumentException>(() => b.Add(0, [1]));
    }
}

[TestClass]
public class TuningGraphViewModelTests
{
    private double _now;

    [TestInitialize]
    public void Init() => Settings.Instance.Remove(TuningGraphViewModel.SettingsKey);

    [TestCleanup]
    public void Cleanup() => Settings.Instance.Remove(TuningGraphViewModel.SettingsKey);

    private TuningGraphViewModel Make() => new(FakeUi.Services(out _), () => _now);

    [TestMethod]
    public void DefaultsToMissionPlannersFields()
    {
        var vm = Make();

        CollectionAssert.AreEqual(new[] { "roll", "pitch", "nav_roll", "nav_pitch" }, vm.Series.Select(s => s.Name).ToArray());
        Assert.AreEqual(4, vm.Series.Select(s => s.Color).Distinct().Count(), "one colour each");
        Assert.IsTrue(vm.Series.All(s => s.LegendText.Length > 0));
    }

    [TestMethod]
    public void TickSamplesTheSelectedValues()
    {
        var vm = Make();
        var cs = vm.Services.Session.Cs;
        var samples = 0;
        vm.Sampled += (_, _) => samples++;

        cs.roll = 10;
        cs.pitch = -5;
        cs.nav_roll = 12;
        cs.nav_pitch = -4;
        _now = 1;
        vm.Tick();
        cs.roll = 11;
        _now = 1.05;
        vm.Tick();

        Assert.AreEqual(2, samples);
        Assert.AreEqual(2, vm.Buffer.Count);
        Assert.AreEqual(1.05, vm.Buffer.LatestTime, 1e-9);
        Assert.AreEqual(10, vm.Buffer.ValueAt(0, 0), 1e-6);
        Assert.AreEqual(11, vm.Buffer.ValueAt(0, 1), 1e-6);
        Assert.AreEqual(-5, vm.Buffer.ValueAt(1, 1), 1e-6);
        Assert.AreEqual(12, vm.Buffer.ValueAt(2, 1), 1e-6);
        Assert.AreEqual(-4, vm.Buffer.ValueAt(3, 1), 1e-6);
    }

    [TestMethod]
    public void KeepsTheLastThirtySeconds()
    {
        var vm = Make();
        for (var i = 0; i <= 40 * 20; i++)
        {
            _now = i / 20.0;
            vm.Tick();
        }

        Assert.AreEqual(40, vm.Buffer.LatestTime, 1e-9);
        Assert.IsTrue(vm.Buffer.OldestTime >= 10 - 1e-9);
        Assert.AreEqual(601, vm.Buffer.Count, "30 s at 20 Hz, both ends included");
    }

    [TestMethod]
    public void TickAllocatesNothing()
    {
        var vm = Make();
        for (var i = 0; i < 50; i++)
        {
            _now = i / 20.0;
            vm.Tick();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 50; i < 1050; i++)
        {
            _now = i / 20.0;
            vm.Tick();
        }

        Assert.AreEqual(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [TestMethod]
    public void PickerListsNumericValuesAndSearches()
    {
        var vm = Make();
        vm.OpenPickerCommand.Execute(null);

        Assert.IsTrue(vm.IsPickerOpen);
        var names = vm.FieldOptions.Select(o => o.Name).ToHashSet();
        foreach (var expected in new[] { "roll", "pitch", "nav_roll", "alt", "ch3out", "groundspeed", "armed" })
            Assert.Contains(expected, names);
        foreach (var excluded in new[] { "mode", "messages", "Location" })
            Assert.DoesNotContain(excluded, names);
        Assert.IsTrue(vm.FieldOptions.Single(o => o.Name == "roll").IsSelected);
        Assert.IsFalse(vm.FieldOptions.Single(o => o.Name == "alt").IsSelected);

        vm.FieldFilter = "nav_";
        Assert.IsTrue(vm.FieldOptions.Count > 0);
        Assert.IsTrue(vm.FieldOptions.All(o => o.Name.Contains("nav_", StringComparison.OrdinalIgnoreCase) ||
                                               o.Field.Label.Contains("nav_", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(vm.FieldOptions.Any(o => o.Name == "nav_pitch"));

        vm.FieldFilter = "";
        Assert.AreEqual(names.Count, vm.FieldOptions.Count);
    }

    [TestMethod]
    public void TickingAFieldGraphsItAndRemembersTheChoice()
    {
        var vm = Make();
        vm.OpenPickerCommand.Execute(null);
        var resets = 0;
        vm.Reset += (_, _) => resets++;

        vm.FieldOptions.Single(o => o.Name == "alt").IsSelected = true;
        vm.FieldOptions.Single(o => o.Name == "pitch").IsSelected = false;

        CollectionAssert.AreEqual(new[] { "roll", "nav_roll", "nav_pitch", "alt" }, vm.Series.Select(s => s.Name).ToArray());
        Assert.AreEqual("roll|nav_roll|nav_pitch|alt", Settings.Instance[TuningGraphViewModel.SettingsKey]);
        Assert.AreEqual(2, resets);
        Assert.AreEqual("4 of 20 selected", vm.SelectionSummary);

        // a new graph (next start) gets the same values back
        var again = Make();
        CollectionAssert.AreEqual(new[] { "roll", "nav_roll", "nav_pitch", "alt" }, again.Series.Select(s => s.Name).ToArray());
    }

    [TestMethod]
    public void KeptSeriesKeepTheirColourAndSamples()
    {
        var vm = Make();
        _now = 1;
        vm.Tick();
        var rollColour = vm.Series[0].Color;

        vm.SetFieldSelected("pitch", false);
        vm.SetFieldSelected("alt", true);

        Assert.AreEqual(rollColour, vm.Series.Single(s => s.Name == "roll").Color);
        Assert.AreEqual(1, vm.Buffer.ValidCount(0), "roll's sample survived");
        Assert.AreEqual(0, vm.Buffer.ValidCount(3), "alt starts empty");
        Assert.AreEqual(4, vm.Series.Select(s => s.Color).Distinct().Count());
    }

    [TestMethod]
    public void AtMostTwentyValues()
    {
        var vm = Make();
        vm.OpenPickerCommand.Execute(null);
        foreach (var option in vm.FieldOptions.Where(o => !o.IsSelected).Take(30).ToList())
            option.IsSelected = true;

        Assert.AreEqual(TuningGraphViewModel.MaxSeries, vm.Series.Count);
        Assert.AreEqual(TuningGraphViewModel.MaxSeries, vm.FieldOptions.Count(o => o.IsSelected), "the extra ticks were undone");
        StringAssert.Contains(vm.PickerMessage, "20");
    }

    [TestMethod]
    public void UnknownSavedNamesAreSkipped()
    {
        Settings.Instance[TuningGraphViewModel.SettingsKey] = "roll|no_such_value|mode|alt";
        var vm = Make();
        CollectionAssert.AreEqual(new[] { "roll", "alt" }, vm.Series.Select(s => s.Name).ToArray());
    }

    [TestMethod]
    public void DefaultAndNoneButtons()
    {
        var vm = Make();
        vm.OpenPickerCommand.Execute(null);

        vm.ClearFieldsCommand.Execute(null);
        Assert.AreEqual(0, vm.Series.Count);
        Assert.IsFalse(vm.FieldOptions.Any(o => o.IsSelected));
        vm.Tick(); // nothing to sample, nothing breaks
        Assert.AreEqual("", Settings.Instance[TuningGraphViewModel.SettingsKey]);
        Assert.AreEqual(0, Make().Series.Count, "an empty choice is remembered too");

        vm.ResetFieldsCommand.Execute(null);
        CollectionAssert.AreEqual(TuningGraphViewModel.DefaultFields.ToArray(), vm.Series.Select(s => s.Name).ToArray());
        Assert.AreEqual(4, vm.FieldOptions.Count(o => o.IsSelected));
    }

    [TestMethod]
    public void PauseAndClear()
    {
        var vm = Make();
        _now = 1;
        vm.Tick();

        vm.TogglePauseCommand.Execute(null);
        Assert.IsTrue(vm.IsPaused);
        Assert.AreEqual("Resume", vm.PauseText);
        _now = 2;
        vm.Tick();
        Assert.AreEqual(2, vm.Buffer.Count, "sampling goes on while the chart is frozen");

        var resets = 0;
        vm.Reset += (_, _) => resets++;
        vm.ClearCommand.Execute(null);
        Assert.AreEqual(0, vm.Buffer.Count);
        Assert.AreEqual(1, resets);

        vm.TogglePauseCommand.Execute(null);
        Assert.AreEqual("Pause", vm.PauseText);
    }

    [TestMethod]
    public void ShowingThePanelStartsAFreshTrace()
    {
        var vm = Make();
        _now = 1;
        vm.Tick();
        vm.OnHidden();
        _now = 100;
        vm.OnShown();
        Assert.AreEqual(0, vm.Buffer.Count);
    }
}
