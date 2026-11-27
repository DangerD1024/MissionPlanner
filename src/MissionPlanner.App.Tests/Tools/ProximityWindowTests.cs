using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.ViewModels.FlightData;
using MissionPlanner.App.Core.ViewModels.Tools;
using MissionPlanner.App.Tests.FlightData;
using MissionPlanner.Utilities;
using static MAVLink;

namespace MissionPlanner.App.Tests.Tools;

/// <summary>
/// TOOLS > Proximity as Mission Planner has it: a radar window per vehicle, a loaded log shown (and
/// kept while paused), and readings a corrupt packet would turn into NaN geometry left out.
/// </summary>
[TestClass]
public class ProximityWindowTests
{
    private const string RangeKey = "next_proximity_range_cm";

    [TestCleanup]
    public void Cleanup() => Settings.Instance.Remove(RangeKey);

    /// <summary>A loaded, paused log (what the HUD calls "a log is loaded") with two vehicles in it.</summary>
    private static AppServices LogWithTwoVehicles(out FakeUi ui, double age = 60)
    {
        var services = FakeUi.Services(out ui);
        var mav = services.Session.Mav;
        // the setter empties the vehicle list, so set it first; logreadmode stays false: paused
        mav.logplaybackfile = new BinaryReader(new MemoryStream());
        mav.lastlogread = new DateTime(2026, 5, 1, 10, 0, 0);

        var one = new MAVState(mav, 1, 1);
        mav.MAVlist[1, 1] = one;
        one.Proximity.DirectionState.Add(1, MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_YAW_90, 230, DateTime.Now, age);

        var two = new MAVState(mav, 2, 1);
        mav.MAVlist[2, 1] = two;
        two.Proximity.DirectionState.Add(5, 10, 5, 400, DateTime.Now, age);

        mav.sysidcurrent = 2;
        mav.compidcurrent = 1;
        return services;
    }

    // the radar redraws every other UI tick
    private static void Redraw(ProximityViewModel vm)
    {
        vm.Tick();
        vm.Tick();
    }

    [TestMethod]
    public void APinnedRadarReadsOnlyItsVehicle_AndAddsNoVehicleForAnIdItDoesNotKnow()
    {
        var services = LogWithTwoVehicles(out _);
        var mav = services.Session.Mav;

        var one = new ProximityViewModel(services, (1, 1));
        Redraw(one);
        var sector = one.Frame.Sectors.Single();
        Assert.IsFalse(sector.IsCustom, "vehicle 1's distance sensor, not the selected vehicle 2's obstacle reading");
        Assert.AreEqual(90, sector.CenterDeg);
        StringAssert.Contains(one.Title, "1-1");
        StringAssert.Contains(one.Summary, "1 readings");

        var unknown = new ProximityViewModel(services, (7, 1));
        Redraw(unknown);
        Assert.IsEmpty(unknown.Frame.Sectors);
        StringAssert.Contains(unknown.Summary, "7-1");
        Assert.IsFalse(mav.MAVlist.Contains(7, 1), "looking for a vehicle must not create it");
        Assert.AreEqual(2, mav.MAVlist.Count);

        // a vehicle that appears later (the list is emptied on connect and load) is found then
        var seven = new MAVState(mav, 7, 1);
        mav.MAVlist[7, 1] = seven;
        seven.Proximity.DirectionState.Add(2, MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_YAW_180, 150, DateTime.Now, 60);
        mav.lastlogread = mav.lastlogread.AddSeconds(1); // the log moved on
        Redraw(unknown);
        Assert.AreEqual(180, unknown.Frame.Sectors.Single().CenterDeg);
    }

    [TestMethod]
    public void TheToolsPageFollowsTheSelectedVehicle()
    {
        var services = LogWithTwoVehicles(out _);
        var mav = services.Session.Mav;
        var page = new ProximityViewModel(services);
        Assert.IsNull(page.Pinned);
        Assert.AreEqual("Proximity", page.Title);

        Redraw(page);
        Assert.IsTrue(page.Frame.Sectors.Single().IsCustom, "vehicle 2 is selected");

        mav.sysidcurrent = 1;
        Redraw(page);
        Assert.AreEqual(90, page.Frame.Sectors.Single().CenterDeg, "now vehicle 1, although the log is paused");
    }

    [TestMethod]
    public void APausedLogKeepsItsPicture_UntilTheLogMovesOrIsClosed()
    {
        // OBSTACLE_DISTANCE readings live 0.2 s by the wall clock
        var services = LogWithTwoVehicles(out _, age: 0.2);
        var mav = services.Session.Mav;
        var page = new ProximityViewModel(services);
        Redraw(page);
        Assert.HasCount(1, page.Frame.Sectors);
        var summary = page.Summary;
        Assert.IsTrue(summary.Length > 0, "a summary while a log is loaded, not only when connected");

        Thread.Sleep(400);
        Assert.IsEmpty(mav.MAV.Proximity.DirectionState.GetRaw(), "the readings themselves have expired");
        Redraw(page);
        Assert.HasCount(1, page.Frame.Sectors, "paused: the picture stays");
        Assert.AreEqual(summary, page.Summary);

        // a seek (or playing) moves the log time: read again
        mav.lastlogread = DateTime.MinValue;
        Redraw(page);
        Assert.IsEmpty(page.Frame.Sectors);

        // the log is closed: nothing to show, and the page says why
        mav.logplaybackfile = null;
        Redraw(page);
        Assert.IsEmpty(page.Frame.Sectors);
        StringAssert.Contains(page.Summary, "load a log");
    }

    [TestMethod]
    public void TheRadarNeedsNoVehicleToBeShown()
    {
        var page = new ProximityViewModel(FakeUi.Services(out _));
        Assert.IsFalse(page.NeedsVehicle, "it has no commands");
        Assert.IsFalse(page.ShowNotConnected, "no \"commands need a vehicle\" note");
        Redraw(page);
        Assert.IsTrue(page.Summary.Length > 0, "says how to get a picture instead");
    }

    [TestMethod]
    public void OpenInWindowShowsANewRadarPinnedToTheSelectedVehicle_EachTime()
    {
        var services = FakeUi.Services(out var ui);
        var mav = services.Session.Mav;
        mav.sysidcurrent = 3;
        mav.compidcurrent = 1;
        var page = new ProximityViewModel(services);
        Assert.IsTrue(page.CanOpenWindow);

        page.OpenInWindowCommand.Execute(null);
        page.OpenInWindowCommand.Execute(null);
        var windows = ui.Shown.OfType<ProximityViewModel>().ToList();
        Assert.HasCount(2, windows, "several radar windows, as Mission Planner allows");
        Assert.AreNotSame(windows[0], windows[1]);
        foreach (var w in windows)
        {
            Assert.AreNotSame(page, w, "never the menu's cached page");
            Assert.AreEqual(((byte)3, (byte)1), w.Pinned);
            Assert.IsTrue(w.IsWindow);
            Assert.IsFalse(w.CanOpenWindow, "no \"Open in window\" inside a window");
            StringAssert.Contains(w.Title, "3-1");
        }

        // another vehicle selected later: the window stays on its own
        mav.sysidcurrent = 4;
        Assert.AreEqual(((byte)3, (byte)1), windows[0].Pinned);

        // nothing selected yet: a window that follows the selection
        mav.sysidcurrent = 0;
        var follows = ProximityViewModel.OpenWindow(services);
        Assert.IsNull(follows.Pinned);
        Assert.IsTrue(follows.IsWindow);
        Assert.AreSame(follows, ui.Shown[^1]);
    }

    [TestMethod]
    public void TheClassicProximityButtonOpensAWindow()
    {
        var services = FakeUi.Services(out var ui);
        services.Session.Mav.sysidcurrent = 1;
        services.Session.Mav.compidcurrent = 1;
        var tools = new ToolsViewModel(services) { ClassicLayout = true };
        var button = tools.ClassicButtons.Single(b => b.Title == "Proximity");
        button.Command.Execute(button.Parameter);

        var window = ui.Shown.OfType<ProximityViewModel>().Single();
        Assert.AreEqual(((byte)1, (byte)1), window.Pinned);
        Assert.IsTrue(tools.ShowButtonWall, "the buttons stay; the radar is in its own window");
        Assert.IsTrue(tools.Items.Any(i => i.Title == "Proximity"), "the TOOLS menu keeps its page");
        tools.ClassicLayout = false;
    }

    [TestMethod]
    public void ReadingsThatCannotBeDrawnAreLeftOut()
    {
        var state = new Proximity.directionState();
        var now = DateTime.Now;
        // OBSTACLE_DISTANCE: angle and increment come from floats of the packet
        state.Add(1, double.NaN, 5, 400, now, 60);
        state.Add(2, 10, double.PositiveInfinity, 400, now, 60);
        state.Add(3, double.NegativeInfinity, 5, 400, now, 60);
        state.Add(4, 20, double.NaN, 400, now, 60);
        state.Add(5, 30, 5, -5, now, 60);
        state.Add(6, 40, 5, 0, now, 60);
        state.Add(7, 50, 5, double.NaN, now, 60);
        state.Add(8, 60, 5, double.PositiveInfinity, now, 60);
        // DISTANCE_SENSOR
        state.Add(9, MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_YAW_45, double.NaN, now, 60);
        state.Add(10, MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_YAW_135, -1, now, 60);
        // drawable ones: a counter-clockwise increment is as wide, a width is held to (0, 360]
        state.Add(11, 70, -3, 300, now, 60);
        state.Add(12, 80, 720, 300, now, 60);
        state.Add(13, 90, 0, 300, now, 60);
        state.Add(14, MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_YAW_270, 120, now, 60);

        var sectors = ProximityGeometry.Sectors(state.GetRaw());
        CollectionAssert.AreEquivalent(new uint[] { 11, 12, 13, 14 }, sectors.Select(s => s.SensorId).ToArray());
        Assert.AreEqual(3, sectors.Single(s => s.SensorId == 11).WidthDeg);
        Assert.AreEqual(360, sectors.Single(s => s.SensorId == 12).WidthDeg);
        Assert.AreEqual(ProximityGeometry.MinWidthDeg, sectors.Single(s => s.SensorId == 13).WidthDeg);
        Assert.IsTrue(sectors.All(s => double.IsFinite(s.StartDeg) && double.IsFinite(s.EndDeg) && s.DistanceCm > 0));
        Assert.AreEqual(120, ProximityGeometry.Closest(sectors));
        Assert.IsNull(ProximityGeometry.ToSector(null));

        Assert.AreEqual(0, ProximityGeometry.Normalize(-1e-15), "never 360");
        Assert.AreEqual(350, ProximityGeometry.Normalize(-10));
        Assert.AreEqual(10, ProximityGeometry.Normalize(1e6 * 360 + 10), 1e-6);
    }

    /// <summary>A tlog with a side sensor, a good OBSTACLE_DISTANCE and two corrupt ones (NaN increment, NaN offset).</summary>
    private static string ProximityTlog()
    {
        var path = TlogWriter.TempPath("proximity");
        using var w = new TlogWriter(path);
        var t = TlogWriter.T0;
        w.Heartbeat(t, gcs: true);
        w.Heartbeat(t);

        static ushort[] Distances(int count, ushort cm)
        {
            var d = Enumerable.Repeat(ushort.MaxValue, 72).ToArray();
            for (var i = 0; i < count; i++)
                d[i] = cm;
            return d;
        }

        var frd = (byte)MAV_FRAME.BODY_FRD;
        for (var k = 1; k <= 10; k++)
        {
            t = TlogWriter.T0.AddMilliseconds(100 * k);
            w.Write(t, MAVLINK_MSG_ID.DISTANCE_SENSOR, new mavlink_distance_sensor_t(
                (uint)(100 * k), 20, 1000, 230, 0, 1, (byte)MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_YAW_90, 0, 0, 0, new float[4], 0));
            w.Write(t, MAVLINK_MSG_ID.OBSTACLE_DISTANCE, new mavlink_obstacle_distance_t(
                (ulong)(100_000 * k), Distances(3, 300), 20, 1000, 0, 5, 0, 0, frd));
            w.Write(t, MAVLINK_MSG_ID.OBSTACLE_DISTANCE, new mavlink_obstacle_distance_t(
                (ulong)(100_000 * k), Distances(4, 400), 20, 1000, 1, 0, float.NaN, 0, frd));
            w.Write(t, MAVLINK_MSG_ID.OBSTACLE_DISTANCE, new mavlink_obstacle_distance_t(
                (ulong)(100_000 * k), Distances(4, 400), 20, 1000, 2, 5, 0, float.PositiveInfinity, frd));
        }

        w.Heartbeat(t);
        return path;
    }

    [TestMethod]
    public async Task AReplayedLogWithCorruptObstaclePacketsDrawsOnlyTheGoodReadings_AndKeepsThemAtTheEnd()
    {
        var services = FakeUi.Services(out var ui);
        var mav = services.Session.Mav;
        using var engine = new TlogPlaybackEngine(mav, new FakeTlogClock());
        engine.Load(ProximityTlog());
        engine.Play();
        await engine.WaitForLoopAsync();
        Assert.IsFalse(mav.logreadmode, "at the end of the log: like paused");
        Assert.AreEqual(TlogWriter.VehicleSysId, (byte)mav.sysidcurrent);

        var page = new ProximityViewModel(services);
        page.OpenInWindowCommand.Execute(null);
        var window = (ProximityViewModel)ui.Shown.Single();
        Assert.AreEqual((TlogWriter.VehicleSysId, TlogWriter.VehicleCompId), window.Pinned);

        foreach (var vm in new[] { page, window })
        {
            Redraw(vm);
            var sectors = vm.Frame.Sectors;
            Assert.HasCount(1, sectors.Where(s => !s.IsCustom), "the side sensor");
            CollectionAssert.AreEquivalent(new double[] { 0, 5, 10 }, sectors.Where(s => s.IsCustom).Select(s => s.CenterDeg).ToArray(),
                "the good obstacle readings only");
            Assert.IsTrue(sectors.All(s => double.IsFinite(s.StartDeg) && double.IsFinite(s.EndDeg) && double.IsFinite(s.DistanceCm)));
        }

        // OBSTACLE_DISTANCE readings expire after 0.2 s of wall-clock time: the stopped log keeps its picture
        Thread.Sleep(400);
        Redraw(page);
        Redraw(window);
        Assert.HasCount(4, page.Frame.Sectors);
        Assert.HasCount(4, window.Frame.Sectors);
    }

    [TestMethod]
    public void AnInfiniteStoredRangeFallsBackToTheDefault()
    {
        Settings.Instance[RangeKey] = "Infinity";
        var page = new ProximityViewModel(FakeUi.Services(out _));
        Assert.AreEqual(500, page.Frame.RangeCm);
        Assert.IsEmpty(ProximityGeometry.Rings(double.PositiveInfinity), "no endless ring loop");
        Assert.IsEmpty(ProximityGeometry.Rings(double.NaN));

        Settings.Instance[RangeKey] = "1e12";
        Assert.AreEqual(100_000, new ProximityViewModel(FakeUi.Services(out _)).Frame.RangeCm, "held at the largest radius");
    }
}
