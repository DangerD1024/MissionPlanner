using GMap.NET;
using MissionPlanner.App.Core.Mission;
using MissionPlanner.App.Core.ViewModels.FlightPlan;
using MissionPlanner.Utilities;
using static MissionPlanner.App.Tests.FlightPlan.PlanTestSupport;
using Cmd = MAVLink.MAV_CMD;

namespace MissionPlanner.App.Tests.FlightPlan;

[TestClass]
public class SurveyPlannerTests
{
    private static readonly PointLatLng Base = P(-35.363261, 149.165230);

    /// <summary>A grid point <paramref name="north"/>/<paramref name="east"/> metres from <see cref="Base"/>.</summary>
    private static PointLatLngAlt G(double north, double east, string tag, double alt = 80)
    {
        var p = Offset(Base, north, east);
        return new PointLatLngAlt(p.Lat, p.Lng, alt, tag);
    }

    /// <summary>
    /// Two lanes as Grid.CreateGrid tags them. Lane 1 runs north with a 10 m overshoot (ME and E
    /// differ); lane 2 runs back south with no overshoot or lead-in (S = SM, ME = E).
    /// </summary>
    private static List<PointLatLngAlt> TwoLanes() =>
    [
        G(0, 0, "S"), G(0, 0, "SM"), G(30, 0, "M"), G(60, 0, "M"), G(90, 0, "ME"), G(100, 0, "E"),
        G(100, 50, "S"), G(100, 50, "SM"), G(70, 50, "M"), G(40, 50, "M"), G(10, 50, "M"), G(0, 50, "ME"), G(0, 50, "E"),
    ];

    private static readonly SurveyOptions NoCamera = new() { Trigger = SurveyTrigger.None, Frame = AltFrame.Relative };
    private static readonly SurveyOptions Camera = new() { Trigger = SurveyTrigger.CameraDistance, TriggerDistance = 30 };

    private static void AssertWp(MissionItem item, PointLatLngAlt at, Cmd cmd = Cmd.WAYPOINT)
    {
        Assert.AreEqual(cmd, item.Command);
        Assert.AreEqual(at.Lat, item.Lat, 1e-9);
        Assert.AreEqual(at.Lng, item.Lng, 1e-9);
        Assert.AreEqual((float)at.Alt, item.Alt);
    }

    [TestMethod]
    public void WithoutCameraOnlyLaneStartsAndEndsBecomeWaypoints()
    {
        var grid = TwoLanes();
        var items = SurveyPlanner.ToMissionItems(grid, NoCamera, new PointLatLngAlt(Base.Lat, Base.Lng));

        Assert.HasCount(4, items);
        AssertWp(items[0], grid[0]);
        AssertWp(items[1], grid[5]); // lane 1 end (overshoot)
        AssertWp(items[2], grid[6]); // lane 2 start
        // lane 2's end sits on its ME, which is not a waypoint here: the end is still added
        AssertWp(items[3], grid[12]);
        Assert.IsTrue(items.All(i => i.Frame == AltFrame.Relative));
    }

    [TestMethod]
    public void CameraTriggerDistanceStartsAfterTheFirstWaypointAndStopsAtTheEnd()
    {
        var grid = TwoLanes();
        var items = SurveyPlanner.ToMissionItems(grid, Camera, new PointLatLngAlt(Base.Lat, Base.Lng));

        CollectionAssert.AreEqual(
            new[] { Cmd.WAYPOINT, Cmd.DO_SET_CAM_TRIGG_DIST, Cmd.WAYPOINT, Cmd.WAYPOINT, Cmd.WAYPOINT, Cmd.WAYPOINT, Cmd.DO_SET_CAM_TRIGG_DIST },
            items.Select(i => i.Command).ToArray());
        AssertWp(items[0], grid[0]);
        Assert.AreEqual(30f, items[1].P1, "trigger distance");
        Assert.AreEqual(1f, items[1].P3, "trigger once now");
        AssertWp(items[2], grid[4]); // lane 1 camera end (ME)
        AssertWp(items[3], grid[5]); // lane 1 end, past the overshoot
        AssertWp(items[4], grid[6]); // lane 2 start
        AssertWp(items[5], grid[11]); // lane 2 ME; its E is at the same place and is skipped
        Assert.AreEqual(0f, items[6].P1, "camera off");
        Assert.AreEqual(1f, items[6].P3);
    }

    [TestMethod]
    public void ZeroTriggerDistanceMeansNoCamera()
    {
        var items = SurveyPlanner.ToMissionItems(TwoLanes(), Camera with { TriggerDistance = 0 }, new PointLatLngAlt());
        Assert.IsFalse(items.Any(i => i.Command == Cmd.DO_SET_CAM_TRIGG_DIST));
        Assert.HasCount(4, items);
    }

    [TestMethod]
    public void TakeoffAndSpeedComeFirstAndTheCruiseSpeedIsRestored()
    {
        var o = NoCamera with { AddTakeoff = true, TakeoffAltitude = 25, ChangeSpeed = true, Speed = 7, Finish = SurveyFinish.ReturnToLaunch };
        var items = SurveyPlanner.ToMissionItems(TwoLanes(), o, new PointLatLngAlt(), restoreSpeed: 10);

        Assert.HasCount(4 + 4, items);
        Assert.AreEqual(Cmd.TAKEOFF, items[0].Command);
        Assert.AreEqual(25f, items[0].Alt);
        Assert.AreEqual(20f, items[0].P1, "Mission Planner's takeoff pitch");
        Assert.AreEqual(Cmd.DO_CHANGE_SPEED, items[1].Command);
        Assert.AreEqual(7f, items[1].P2);
        Assert.AreEqual(Cmd.WAYPOINT, items[2].Command);
        Assert.AreEqual(Cmd.DO_CHANGE_SPEED, items[^2].Command);
        Assert.AreEqual(10f, items[^2].P2, "back to WPNAV_SPEED / WP_SPD");
        Assert.AreEqual(Cmd.RETURN_TO_LAUNCH, items[^1].Command);
    }

    [TestMethod]
    public void SpeedIsNotRestoredWithoutAKnownCruiseSpeed()
    {
        var o = NoCamera with { ChangeSpeed = true, Speed = 7 };
        var items = SurveyPlanner.ToMissionItems(TwoLanes(), o, new PointLatLngAlt(), restoreSpeed: null);
        Assert.AreEqual(1, items.Count(i => i.Command == Cmd.DO_CHANGE_SPEED));
    }

    [TestMethod]
    public void LandEndsAtHomeOnTheGround()
    {
        var home = new PointLatLngAlt(-35.36, 149.16, 584);
        var items = SurveyPlanner.ToMissionItems(TwoLanes(), Camera with { Finish = SurveyFinish.LandAtHome }, home);

        var land = items[^1];
        Assert.AreEqual(Cmd.LAND, land.Command);
        Assert.AreEqual(home.Lat, land.Lat);
        Assert.AreEqual(home.Lng, land.Lng);
        Assert.AreEqual(0f, land.Alt);
        Assert.AreEqual(Cmd.DO_SET_CAM_TRIGG_DIST, items[^2].Command, "camera off before landing");
    }

    [TestMethod]
    public void SplineMakesLaneStartsSplineWaypoints()
    {
        var items = SurveyPlanner.ToMissionItems(TwoLanes(), NoCamera with { UseSpline = true, Frame = AltFrame.Terrain }, new PointLatLngAlt());

        CollectionAssert.AreEqual(
            new[] { Cmd.SPLINE_WAYPOINT, Cmd.WAYPOINT, Cmd.SPLINE_WAYPOINT, Cmd.WAYPOINT },
            items.Select(i => i.Command).ToArray());
        Assert.IsTrue(items.All(i => i.Frame == AltFrame.Terrain));
    }

    [TestMethod]
    public void EmptyGridGivesNoItems()
    {
        Assert.IsEmpty(SurveyPlanner.ToMissionItems([], Camera with { AddTakeoff = true, Finish = SurveyFinish.ReturnToLaunch }, new PointLatLngAlt()));
    }

    [TestMethod]
    public void StatsFollowTheRouteFromHomeAndBack()
    {
        var grid = TwoLanes();
        var home = G(-20, 0, "");
        var o = Camera with { Speed = 5 };

        var stats = SurveyPlanner.ComputeStats([G(0, 0, ""), G(100, 0, ""), G(100, 50, ""), G(0, 50, "")], grid, home, o);

        // home → lane 1 (20) + lane 1 (100) + turn (50) + lane 2 (100) + back home (≈54)
        var back = Math.Sqrt(20 * 20 + 50 * 50);
        Assert.AreEqual(20 + 100 + 50 + 100 + back, stats.DistanceM, 0.5);
        Assert.AreEqual(2, stats.Lanes);
        Assert.AreEqual(5, stats.Images);
        Assert.AreEqual(5000, stats.AreaM2, 25);
        Assert.AreEqual(stats.DistanceM / (5 * SurveyPlanner.SpeedFactor), stats.FlightTime.TotalSeconds, 0.01);

        var noCamera = SurveyPlanner.ComputeStats([], grid, home, NoCamera);
        Assert.AreEqual(0, noCamera.Images, "no photos without the camera");
    }

    [TestMethod]
    public void AreaOfARectangle()
    {
        var poly = new[] { Offset(Base, 0, 0), Offset(Base, 300, 0), Offset(Base, 300, 200), Offset(Base, 0, 200) }
            .Select(p => new PointLatLngAlt(p.Lat, p.Lng)).ToList();
        Assert.AreEqual(60_000, SurveyPlanner.Area(poly), 300);
        Assert.AreEqual("60,000 m² · 6.00 ha", SurveyGridViewModel.FormatArea(60_000));
        Assert.AreEqual("1,250,000 m² · 125.0 ha", SurveyGridViewModel.FormatArea(1_250_000));
    }

    [TestMethod]
    public void AutoAngleIsTheLongestEdgeOnTheUtmGrid()
    {
        // on a zone's central meridian (147°E for zone 55) grid north is true north
        var meridian = P(-35.36, 147.0);
        PointLatLngAlt M(double north, double east)
        {
            var p = Offset(meridian, north, east);
            return new PointLatLngAlt(p.Lat, p.Lng);
        }

        // long edge 400 m east-west
        Assert.AreEqual(90, SurveyPlanner.AutoAngle([M(0, 0), M(0, 400), M(100, 400), M(100, 0)]), 0.05);
        // long edge 500 m north-south (the closing edge)
        var northSouth = SurveyPlanner.AutoAngle([M(0, 0), M(0, 100), M(500, 100), M(500, 0)]);
        Assert.AreEqual(0, northSouth > 90 ? northSouth - 180 : northSouth, 0.05);

        // long edge at 30° (and 210° the other way round): folded to one direction
        // (Offset is spherical, the UTM grid ellipsoidal: a tenth of a degree apart)
        var d = 600.0;
        List<PointLatLngAlt> tilted = [M(0, 0), M(d * Math.Cos(Math.PI / 6), d * Math.Sin(Math.PI / 6)), M(0, 120)];
        var forward = SurveyPlanner.AutoAngle(tilted);
        Assert.AreEqual(30, forward, 0.25);
        Assert.AreEqual(forward, SurveyPlanner.AutoAngle(tilted.AsEnumerable().Reverse().ToList()), 0.01);
    }

    [TestMethod]
    public void AutoAngleLanesRunParallelToTheLongestEdge()
    {
        // away from the central meridian the UTM grid is turned about 1.3° here: lanes still follow the edge
        var d = 600.0;
        var polygon = new List<PointLatLngAlt> { G(0, 0, ""), G(d * Math.Cos(Math.PI / 6), d * Math.Sin(Math.PI / 6), ""), G(0, 150, "") };
        var o = NoCamera with { AutoAngle = true, LaneSpacing = 30 };

        var grid = SurveyPlanner.Generate(polygon, o, polygon[0]);

        var edge = polygon[0].GetBearing(polygon[1]) % 180;
        var s = grid.First(p => p.Tag == "S");
        var e = grid.First(p => p.Tag == "E");
        var lane = s.GetBearing(e) % 180;
        Assert.AreEqual(edge, lane, 0.1, $"edge {edge:0.00}°, lanes {lane:0.00}°");
    }

    [TestMethod]
    public void GeneratedGridConvertsLaneByLane()
    {
        // 300 m north-south by 200 m east-west, lanes north-south every 50 m
        var poly = new[] { G(0, 0, ""), G(300, 0, ""), G(300, 200, ""), G(0, 200, "") }.ToList();
        var o = Camera with { Altitude = 60, LaneSpacing = 50, TriggerDistance = 25, AutoAngle = false, Angle = 0, StartFrom = Grid.StartPosition.BottomLeft };
        var home = G(-50, 0, "");

        var grid = SurveyPlanner.Generate(poly, o, home);

        var lanes = grid.Count(p => p.Tag == "S");
        Assert.IsTrue(lanes is >= 4 and <= 5, $"lanes: {lanes}");
        Assert.AreEqual(lanes, grid.Count(p => p.Tag == "E"));
        Assert.IsTrue(grid.All(p => p.Alt == 60));
        Assert.IsTrue(grid.Count(p => p.Tag == "M") >= lanes * 10, "about 12 photos per 300 m lane");
        // starts at the bottom-left corner
        Assert.IsTrue(grid[0].GetDistance(poly[0]) < 50, "starts next to the bottom-left corner");

        var items = SurveyPlanner.ToMissionItems(grid, o, home);
        var waypoints = items.Where(i => i.Command == Cmd.WAYPOINT).ToList();
        // first point, then per lane its camera end and (with no overshoot) nothing more, then each later start
        Assert.AreEqual(2 * lanes, waypoints.Count);
        Assert.AreEqual(2, items.Count(i => i.Command == Cmd.DO_SET_CAM_TRIGG_DIST));

        var stats = SurveyPlanner.ComputeStats(poly, grid, home, o);
        Assert.AreEqual(lanes, stats.Lanes);
        Assert.AreEqual(grid.Count(p => p.Tag == "M"), stats.Images);
        Assert.IsTrue(stats.DistanceM > lanes * 290 && stats.DistanceM < lanes * 300 + 1000, $"distance {stats.DistanceM}");
    }

    [TestMethod]
    public void CrossgridAddsLanesAtRightAngles()
    {
        var poly = new[] { G(0, 0, ""), G(300, 0, ""), G(300, 300, ""), G(0, 300, "") }.ToList();
        var o = NoCamera with { LaneSpacing = 60, AutoAngle = false, Angle = 0 };
        var single = SurveyPlanner.Generate(poly, o, G(-50, 0, ""));
        var cross = SurveyPlanner.Generate(poly, o with { Crossgrid = true }, G(-50, 0, ""));

        var singleLanes = single.Count(p => p.Tag == "S");
        Assert.IsTrue(cross.Count(p => p.Tag == "S") >= 2 * singleLanes - 1);

        // the second pass is square to the first (both are laid out on the UTM grid, which is
        // turned a little from true north here, so compare them with each other)
        static double LaneBearing(IEnumerable<PointLatLngAlt> pass) =>
            pass.First(p => p.Tag == "S").GetBearing(pass.First(p => p.Tag == "E")) % 180;
        var turn = (LaneBearing(cross.Skip(single.Count)) - LaneBearing(single) + 360) % 180;
        Assert.AreEqual(90, turn, 0.2);
    }

    [TestMethod]
    public void TinyLaneSpacingIsRefused()
    {
        var poly = new[] { G(0, 0, ""), G(5000, 0, ""), G(5000, 5000, ""), G(0, 5000, "") }.ToList();
        Assert.IsNotNull(SurveyPlanner.CheckSize(poly, NoCamera with { LaneSpacing = 1 }));
        Assert.IsNotNull(SurveyPlanner.CheckSize(poly, Camera with { LaneSpacing = 50, TriggerDistance = 1 }));
        Assert.IsNull(SurveyPlanner.CheckSize(poly, Camera with { LaneSpacing = 50, TriggerDistance = 30 }));
        Assert.ThrowsExactly<SurveyTooLargeException>(() => SurveyPlanner.Generate(poly, NoCamera with { LaneSpacing = 1 }, G(0, 0, "")));
    }

    [TestMethod]
    public void PreviewSplitsLanesTurnsAndHomeLegs()
    {
        var grid = TwoLanes();
        var home = G(-20, 0, "");
        var preview = SurveyPlanner.BuildPreview([G(0, 0, ""), G(100, 0, ""), G(100, 50, "")], grid, home);

        Assert.HasCount(2, preview.Lanes);
        Assert.HasCount(1, preview.Extensions, "lane 1's overshoot");
        Assert.HasCount(1, preview.Transits);
        Assert.HasCount(2, preview.HomeLegs);
        Assert.HasCount(5, preview.Triggers);
        Assert.AreEqual(new PointLatLng(grid[0].Lat, grid[0].Lng), preview.Start);
        Assert.AreEqual(new PointLatLng(grid[^1].Lat, grid[^1].Lng), preview.End);
    }

    [TestMethod]
    public void OptionsRoundTripThroughSettings()
    {
        var store = new Dictionary<string, string>();
        var o = new SurveyOptions
        {
            Altitude = 75.5, LaneSpacing = 22, TriggerDistance = 12, AutoAngle = false, Angle = 33, Overshoot1 = 5,
            Overshoot2 = 6, LeadIn1 = 7, LeadIn2 = 8, StartFrom = Grid.StartPosition.TopRight, MinLaneSeparation = 2,
            Crossgrid = true, AddTakeoff = true, TakeoffAltitude = 15, ChangeSpeed = true, Speed = 9,
            Trigger = SurveyTrigger.None, UseSpline = true, Finish = SurveyFinish.LandAtHome,
        };
        o.Save((k, v) => store[k] = v);
        Assert.IsTrue(store.Keys.All(k => k.StartsWith("next_survey_")));

        var back = SurveyOptions.Load(k => store.GetValueOrDefault(k));
        Assert.AreEqual(o, back);

        store["next_survey_alt"] = "rubbish";
        store["next_survey_startfrom"] = "Point";
        var d = new SurveyOptions();
        var fallback = SurveyOptions.Load(k => store.GetValueOrDefault(k), d);
        Assert.AreEqual(d.Altitude, fallback.Altitude);
        Assert.AreEqual(d.StartFrom, fallback.StartFrom);
    }
}
