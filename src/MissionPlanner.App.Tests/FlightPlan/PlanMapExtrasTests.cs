using GMap.NET;
using GMap.NET.MapProviders;
using MissionPlanner.App.Core.Mission;
using MissionPlanner.App.Core.ViewModels.FlightPlan;
using MissionPlanner.App.Tests.FlightData;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Tests.FlightPlan;

[TestClass]
public class PlanMapExtrasTests
{
    private static PureProjection Mercator => GMapProviders.OpenStreetMap.Projection;

    [TestCleanup]
    public void Cleanup() => Settings.Instance.Remove("next_plan_bearing");

    [TestMethod]
    public void PrefetchAreaCountsTilesPerZoom()
    {
        // the whole world: 1 tile at zoom 0, 4 at 1, 16 at 2
        var world = TilePrefetch.ForArea(Mercator, new PointLatLng(85, -179.9), new PointLatLng(-85, 179.9), 0, 2);
        Assert.AreEqual(21, world.Count);
        Assert.AreEqual(1, world.Count(t => t.Zoom == 0));
        Assert.AreEqual(16, world.Count(t => t.Zoom == 2));

        // a small box: corners in either order give the same tiles, more at each deeper zoom
        var a = TilePrefetch.ForArea(Mercator, new PointLatLng(-35.36, 149.16), new PointLatLng(-35.37, 149.17), 14, 16);
        var b = TilePrefetch.ForArea(Mercator, new PointLatLng(-35.37, 149.17), new PointLatLng(-35.36, 149.16), 14, 16);
        CollectionAssert.AreEqual(a, b);
        Assert.IsTrue(a.Count(t => t.Zoom == 16) > a.Count(t => t.Zoom == 14));
        Assert.AreEqual(a.Count, a.Distinct().Count());
    }

    [TestMethod]
    public void PrefetchPathCoversTheRouteOnly()
    {
        // a 10 km leg east: at zoom 15 (~1 km tiles there) the path takes a strip, not the whole box
        var start = new PointLatLng(-35.0, 149.0);
        var end = new PointLatLng(-35.0, 149.11);
        var path = TilePrefetch.ForPath(Mercator, [start, end], 200, 15, 15);
        var box = TilePrefetch.ForArea(Mercator, new PointLatLng(-34.95, 149.0), new PointLatLng(-35.05, 149.11), 15, 15);
        Assert.IsTrue(path.Count > 5);
        Assert.IsTrue(path.Count < box.Count / 3, $"{path.Count} vs {box.Count}");

        var startTile = Mercator.FromPixelToTileXY(Mercator.FromLatLngToPixel(start, 15));
        var endTile = Mercator.FromPixelToTileXY(Mercator.FromLatLngToPixel(end, 15));
        Assert.IsTrue(path.Contains(new TileId(15, startTile.X, startTile.Y)));
        Assert.IsTrue(path.Contains(new TileId(15, endTile.X, endTile.Y)));
        Assert.AreEqual(0, TilePrefetch.ForPath(Mercator, [], 200, 1, 5).Count);
    }

    [TestMethod]
    public async Task PrefetchDownloadsEveryTileAndCountsFailures()
    {
        var tiles = TilePrefetch.ForArea(Mercator, new PointLatLng(-35.36, 149.16), new PointLatLng(-35.37, 149.17), 12, 15);
        var fetched = new System.Collections.Concurrent.ConcurrentBag<TileId>();
        FakeUi.Services(out var ui);
        var vm = new TilePrefetchViewModel(tiles, ui, (t, _) =>
        {
            fetched.Add(t);
            return Task.FromResult(t.Zoom != 15 || t.X % 2 == 0);
        });

        await vm.RunAsync();
        Assert.AreEqual(tiles.Count, fetched.Count);
        Assert.AreEqual(tiles.Count, fetched.Distinct().Count());
        Assert.AreEqual(tiles.Count, vm.Completed);
        Assert.IsFalse(vm.IsRunning);
        Assert.AreEqual("Close", vm.ButtonText);
        var failed = tiles.Count(t => t.Zoom == 15 && t.X % 2 != 0);
        Assert.IsTrue(failed > 0);
        Assert.AreEqual($"Done: {tiles.Count} of {tiles.Count} tiles, {failed} failed", vm.ProgressText);
    }

    [TestMethod]
    public async Task PrefetchStopsWhenCancelled()
    {
        var tiles = Enumerable.Range(0, 500).Select(i => new TileId(18, i, 0)).ToList();
        FakeUi.Services(out var ui);
        TilePrefetchViewModel? vm = null;
        var n = 0;
        vm = new TilePrefetchViewModel(tiles, ui, async (_, c) =>
        {
            if (Interlocked.Increment(ref n) == 20)
                vm!.Cancel();
            await Task.Delay(1, c);
            return true;
        });

        await vm.RunAsync();
        Assert.IsTrue(vm.Completed < tiles.Count, vm.Completed.ToString());
        StringAssert.StartsWith(vm.ProgressText, "Cancelled: ");
    }

    [TestMethod]
    public void ElevationProfileMakesAltitudesAbsolute()
    {
        var home = new PointLatLng(-35.0, 149.0);
        var mission = new List<MissionItem>
        {
            new() { Seq = 1, Command = MAVLink.MAV_CMD.WAYPOINT, Lat = -35.0, Lng = 149.01, Alt = 100, Frame = AltFrame.Relative },
            new() { Seq = 2, Command = MAVLink.MAV_CMD.DO_SET_SERVO, Frame = AltFrame.Relative },
            new() { Seq = 3, Command = MAVLink.MAV_CMD.WAYPOINT, Lat = -35.0, Lng = 149.02, Alt = 700, Frame = AltFrame.Absolute },
            new() { Seq = 4, Command = MAVLink.MAV_CMD.WAYPOINT, Lat = -35.0, Lng = 149.03, Alt = 50, Frame = AltFrame.Terrain },
        };

        // ground rises 1 m per 0.0001 degrees of longitude east of home
        double? Ground(double lat, double lng) => 500 + (lng - 149.0) * 10000;
        var (samples, wps) = ElevationProfile.Build(home, 500, mission, Ground, step: 50);

        CollectionAssert.AreEqual(new[] { 0, 1, 3, 4 }, wps.Select(w => w.Seq).ToArray());
        Assert.AreEqual(500, wps[0].Path, 1e-9);
        Assert.AreEqual(600, wps[1].Path, 1e-9); // home + 100
        Assert.AreEqual(700, wps[2].Path, 1e-9); // absolute
        Assert.AreEqual(500 + 300 + 50, wps[3].Path, 1e-6); // ground there + 50
        Assert.AreEqual(wps[3].Distance, samples[^1].Distance, 1e-6);
        Assert.AreEqual(0.03 * 111_320 * Math.Cos(35 * Math.PI / 180), wps[3].Distance, 50);
        Assert.IsTrue(samples.Zip(samples.Skip(1)).All(p => p.Second.Distance > p.First.Distance));
        Assert.IsTrue(samples.Count > 40);

        // halfway along the first leg: path and ground interpolated
        var mid = samples.OrderBy(s => Math.Abs(s.Distance - wps[1].Distance / 2)).First();
        Assert.AreEqual(550, mid.Path, 3);
        Assert.AreEqual(550, mid.Ground!.Value, 3);

        var low = ElevationProfile.LowestClearance(samples)!.Value;
        Assert.AreEqual(0, low.Clearance, 1e-6); // at home, on the ground
        StringAssert.Contains(ElevationGraphViewModel.Describe(samples, wps), "lowest height above ground 0 m");
    }

    [TestMethod]
    public void ElevationSummaryWarnsAndCopesWithoutTerrain()
    {
        var home = new PointLatLng(-35.0, 149.0);
        var mission = new List<MissionItem> { new() { Seq = 1, Command = MAVLink.MAV_CMD.WAYPOINT, Lat = -35.0, Lng = 149.01, Alt = 10, Frame = AltFrame.Relative } };
        var (s1, w1) = ElevationProfile.Build(home, 500, mission, (_, lng) => lng > 149.005 ? 600 : 500);
        StringAssert.Contains(ElevationGraphViewModel.Describe(s1, w1), "BELOW THE GROUND");

        var (s2, w2) = ElevationProfile.Build(home, 500, mission, (_, _) => null);
        StringAssert.Contains(ElevationGraphViewModel.Describe(s2, w2), "No terrain data");
        Assert.IsNull(ElevationProfile.LowestClearance(s2));

        var (s3, w3) = ElevationProfile.Build(null, 0, [], (_, _) => 0);
        Assert.AreEqual(0, s3.Count);
        StringAssert.Contains(ElevationGraphViewModel.Describe(s3, w3), "at least one waypoint");
    }

    [TestMethod]
    public void PlaceSearchReadsNominatim()
    {
        const string json = """
            [{"place_id":1,"lat":"-31.9403","lon":"115.9669","display_name":"Perth Airport",
              "boundingbox":["-31.99","-31.90","115.93","116.01"]}]
            """;
        var r = PlaceSearch.Parse(json)!.Value;
        Assert.AreEqual(-31.9403, r.Centre.Lat, 1e-9);
        Assert.AreEqual(115.9669, r.Centre.Lng, 1e-9);
        Assert.AreEqual(new PointLatLng(-31.99, 115.93), r.Box[0]);
        Assert.AreEqual(new PointLatLng(-31.90, 116.01), r.Box[1]);
        Assert.IsNull(PlaceSearch.Parse("[]"));
        Assert.AreEqual(1, PlaceSearch.Parse("""[{"lat":"1","lon":"2"}]""")!.Value.Box.Length);
    }

    [TestMethod]
    public async Task ZoomToCoordsRotateAndKmlOverlay()
    {
        var vm = new FlightPlanViewModel(FakeUi.Services(out var ui));
        IReadOnlyList<PointLatLng>? zoom = null;
        vm.ZoomRequested += (_, pts) => zoom = pts;
        var changed = 0;
        vm.MapChanged += (_, _) => changed++;

        ui.PromptAnswer = "-35.363 149.165";
        await vm.ZoomToCoordsAsync();
        Assert.IsNotNull(zoom);
        Assert.AreEqual(-35.363, zoom!.Average(p => p.Lat), 1e-9);
        Assert.AreEqual(149.165, zoom.Average(p => p.Lng), 1e-9);

        ui.PromptAnswer = "450";
        await vm.RotateMapAsync();
        Assert.AreEqual(90, vm.MapBearing, 1e-9);
        Assert.AreEqual("90", Settings.Instance["next_plan_bearing"]);
        ui.PromptAnswer = "-90";
        await vm.RotateMapAsync();
        Assert.AreEqual(270, vm.MapBearing, 1e-9);

        var shapes = PlanFiles.ReadKml("""
            <kml xmlns="http://www.opengis.net/kml/2.2"><Document><Placemark><LineString>
            <coordinates>149.1,-35.3,0 149.2,-35.4,0</coordinates></LineString></Placemark></Document></kml>
            """);
        changed = 0;
        vm.SetKmlOverlay(shapes);
        Assert.AreEqual(1, vm.KmlOverlay.Count);
        Assert.AreEqual(0, vm.Store.Mission.Count); // drawn only
        vm.ClearKmlOverlay();
        Assert.AreEqual(0, vm.KmlOverlay.Count);
        Assert.AreEqual(2, changed);
    }

    [TestMethod]
    public async Task PrefetchPathAndElevationOpenTheirWindows()
    {
        var vm = new FlightPlanViewModel(FakeUi.Services(out var ui));
        vm.Store.SetHome(-35.36, 149.16, 584);
        vm.Store.Mission.Add(vm.Store.NewWaypoint(-35.361, 149.161));
        ui.PromptAnswer = "12";
        await vm.PrefetchPathAsync();
        var prefetch = ui.Dialogs.OfType<TilePrefetchViewModel>().Single();
        Assert.AreEqual("zoom 1 to 12", prefetch.ZoomText);
        Assert.IsTrue(prefetch.Total >= 12);

        await vm.ShowElevationGraphAsync();
        var graph = ui.Dialogs.OfType<ElevationGraphViewModel>().Single();
        await graph.LoadAsync();
        Assert.AreEqual(2, graph.Waypoints.Count);
    }
}
