using System.Diagnostics;
using MissionPlanner.App.Core.ViewModels.Logs;

namespace MissionPlanner.App.Tests.Logs;

[TestClass]
public class LogBrowserViewModelTests
{
    private static async Task<(LogBrowserViewModel Vm, FakeUiServices Ui)> OpenAsync(byte[] data, string extension)
    {
        var path = LogTestFiles.TempPath(extension);
        File.WriteAllBytes(path, data);
        var ui = new FakeUiServices();
        var vm = new LogBrowserViewModel(ui.CreateServices());
        await vm.OpenFileAsync(path);
        return (vm, ui);
    }

    [TestMethod]
    public async Task OpensADataFlashLogIntoATree()
    {
        var (vm, ui) = await OpenAsync(LogTestFiles.BinaryLog(), ".bin");
        using var _ = vm;

        Assert.IsTrue(vm.HasLog);
        Assert.IsFalse(vm.IsLoading);
        Assert.AreEqual(0, ui.Messages.Count);
        StringAssert.StartsWith(vm.Summary, "DataFlash log");
        Assert.AreEqual("Time since boot (s)", vm.XAxisLabel);

        var att = vm.Nodes.Single(n => n.Name == "ATT");
        CollectionAssert.AreEqual(new[] { "TimeUS", "Roll", "Pitch" }, att.Children.Select(c => c.Name).ToArray());
        Assert.IsTrue(att.Children.All(c => c.IsField));

        // instances sit between the type and its fields
        var gps = vm.Nodes.Single(n => n.Name == "GPS");
        CollectionAssert.AreEqual(new[] { "[0]", "[1]" }, gps.Children.Select(c => c.Name).ToArray());
        Assert.AreEqual("GPS[1].Alt", gps.Children[1].Children.Single(c => c.Name == "Alt").Key);

        Assert.IsTrue(vm.HasTrack);
        Assert.AreEqual(5, vm.Track.Count);
        Assert.AreEqual(1.0, vm.TrackPointAt(0.2)!.Value.Time, 1e-9);
        Assert.AreEqual(2.0, vm.TrackPointAt(2.2)!.Value.Time, 1e-9);
    }

    [TestMethod]
    public async Task FilterNarrowsTypesAndFields()
    {
        var (vm, _) = await OpenAsync(LogTestFiles.BinaryLog(), ".bin");
        using var _vm = vm;
        var all = vm.Nodes.Count;

        vm.Filter = "att";
        CollectionAssert.AreEqual(new[] { "ATT" }, vm.Nodes.Select(n => n.Name).ToArray());
        Assert.AreEqual(3, vm.Nodes[0].Children.Count);

        // a field name: every type that has it, showing just that field, expanded
        vm.Filter = "lat";
        var gps = vm.Nodes.Single();
        Assert.AreEqual("GPS", gps.Name);
        Assert.IsTrue(gps.IsExpanded);
        Assert.IsTrue(gps.Children.All(i => i.Children.Single().Name == "Lat"));

        vm.Filter = "ATT.pi";
        CollectionAssert.AreEqual(new[] { "Pitch" }, vm.Nodes.Single().Children.Select(c => c.Name).ToArray());

        vm.Filter = "nothing-like-this";
        Assert.AreEqual(0, vm.Nodes.Count);

        vm.ClearFilterCommand.Execute(null);
        Assert.AreEqual(all, vm.Nodes.Count);
        Assert.AreEqual(3, vm.Nodes.Single(n => n.Name == "ATT").Children.Count);
    }

    [TestMethod]
    public async Task CheckingAFieldGraphsItAndUncheckingRemovesIt()
    {
        var (vm, _) = await OpenAsync(LogTestFiles.BinaryLog(), ".bin");
        using var _vm = vm;

        var roll = vm.FindField("ATT", null, "Roll")!;
        var alt1 = vm.FindField("GPS", "1", "Alt")!;
        // one at a time: there is no UI thread here, so two reads finishing together would add to
        // Series from two thread-pool threads at once (in the app both continuations run on the UI thread)
        roll.IsChecked = true;
        await WaitFor(() => vm.Series.Count == 1 && !roll.IsBusy);
        alt1.IsChecked = true;
        await WaitFor(() => vm.Series.Count == 2 && !alt1.IsBusy);

        var s = vm.Series.Single(x => x.Key == "ATT.Roll");
        Assert.AreEqual(21, s.SourceCount);
        Assert.AreEqual(1.0, s.X[0], 1e-9);
        Assert.AreEqual(1.5, s.Y[0], 1e-5);
        Assert.AreEqual(1.5, s.Min, 1e-5);
        Assert.AreEqual(4.5, s.Max, 1e-5);
        Assert.IsFalse(s.IsThinned);
        Assert.AreEqual(roll.Color, s.Color);
        Assert.AreNotEqual(roll.Color, alt1.Color);
        CollectionAssert.AreEqual(new[] { 1100.0, 1105, 1110, 1115, 1120 }, vm.Series.Single(x => x.Key == "GPS[1].Alt").Y);
        Assert.IsTrue(vm.HasGraph);

        roll.IsChecked = false;
        Assert.AreEqual(1, vm.Series.Count);
        Assert.IsNull(roll.Color);

        // removing the series from the graph unticks the field
        vm.RemoveSeriesCommand.Execute(vm.Series.Single());
        Assert.IsFalse(alt1.IsChecked);
        Assert.AreEqual(0, vm.Series.Count);
        Assert.IsFalse(vm.HasGraph);

        roll.IsChecked = true;
        await WaitFor(() => vm.Series.Count == 1);
        vm.ClearGraphCommand.Execute(null);
        Assert.IsFalse(roll.IsChecked);
        Assert.AreEqual(0, vm.Series.Count);
    }

    [TestMethod]
    public async Task EventTogglesFilterMarkers()
    {
        var (vm, _) = await OpenAsync(LogTestFiles.BinaryLog(), ".bin");
        using var _vm = vm;

        Assert.AreEqual(4, vm.Events.Count);
        // modes, errors and messages by default; EV events off
        CollectionAssert.AreEquivalent(new[] { LogEventKind.Message, LogEventKind.Mode, LogEventKind.Error },
            vm.VisibleEvents.Select(e => e.Kind).ToArray());

        vm.ShowMessages = false;
        vm.ShowEvents = true;
        CollectionAssert.AreEquivalent(new[] { LogEventKind.Mode, LogEventKind.Error, LogEventKind.Event },
            vm.VisibleEvents.Select(e => e.Kind).ToArray());

        double? focused = null;
        vm.FocusTimeRequested += (_, t) => focused = t;
        vm.FocusEventCommand.Execute(vm.VisibleEvents[0]);
        Assert.AreEqual(vm.VisibleEvents[0].Time, focused);
    }

    [TestMethod]
    public async Task OpensATelemetryLog()
    {
        var (vm, _) = await OpenAsync(LogTestFiles.Tlog(new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc)), ".tlog");
        using var _vm = vm;

        Assert.AreEqual("Time since start of log (s)", vm.XAxisLabel);
        StringAssert.StartsWith(vm.Summary, "Telemetry log");
        var hb = vm.Nodes.Single(n => n.Name == "HEARTBEAT");
        CollectionAssert.AreEqual(new[] { "[1:1]", "[255:190]" }, hb.Children.Select(c => c.Name).ToArray());

        var roll = vm.FindField("ATTITUDE", null, "roll")!;
        roll.IsChecked = true;
        await WaitFor(() => vm.Series.Count == 1);
        Assert.AreEqual(10, vm.Series[0].SourceCount);
        Assert.AreEqual(0.9, vm.Series[0].Max, 1e-5);
        Assert.IsTrue(vm.Events.Any(e => e.Kind == LogEventKind.Mode));
        Assert.AreEqual(10, vm.Track.Count);
    }

    [TestMethod]
    public async Task ABadFileReportsAndLeavesTheBrowserEmpty()
    {
        var (vm, ui) = await OpenAsync([1, 2, 3], ".bin");
        using var _ = vm;
        Assert.IsFalse(vm.IsLoading);
        Assert.IsTrue(vm.HasLog ? vm.Nodes.Count == 0 : ui.Messages.Count == 1, "an unreadable log shows an error or an empty tree");
    }

    [TestMethod]
    public async Task OpeningAnotherLogReplacesTheFirst()
    {
        var (vm, _) = await OpenAsync(LogTestFiles.BinaryLog(), ".bin");
        using var _vm = vm;
        var roll = vm.FindField("ATT", null, "Roll")!;
        roll.IsChecked = true;
        await WaitFor(() => vm.Series.Count == 1);
        var first = vm.Source!;

        var path = LogTestFiles.TempPath(".log");
        File.WriteAllText(path, LogTestFiles.TextLog());
        await vm.OpenFileAsync(path);

        Assert.AreNotSame(first, vm.Source);
        Assert.AreEqual(0, vm.Series.Count);
        Assert.IsNull(first.GetSeries("ATT", null, "Roll"), "the first log is closed");
        Assert.AreEqual(Path.GetFileName(path), vm.FileName);
        Assert.IsFalse(vm.HasTrack);
    }

    private static async Task WaitFor(Func<bool> condition, int seconds = 20)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(seconds))
                Assert.Fail("timed out");
            await Task.Delay(10);
        }
    }
}
