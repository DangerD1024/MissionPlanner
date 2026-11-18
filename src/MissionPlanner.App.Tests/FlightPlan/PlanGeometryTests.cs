using GMap.NET;
using MissionPlanner.App.Core.Mission;
using MissionPlanner.App.Core.ViewModels.FlightPlan;
using static MissionPlanner.App.Tests.FlightPlan.PlanTestSupport;

namespace MissionPlanner.App.Tests.FlightPlan;

[TestClass]
public class PlanGeometryTests
{
    private static readonly PointLatLng Home = P(-35.363261, 149.165230);

    private static MissionItem At(PointLatLng p, MAVLink.MAV_CMD cmd = MAVLink.MAV_CMD.WAYPOINT) => Wp(p.Lat, p.Lng, 50, cmd);

    [TestMethod]
    public void RouteStartsAndEndsAtHomeWithDashedLegs()
    {
        var a = Offset(Home, 100, 0);
        var b = Offset(Home, 100, 100);
        var items = new List<MissionItem> { At(a), At(b) };

        var g = PlanGeometry.ForMission(Home, items, 2, 45);

        Assert.HasCount(3, g.Lines);
        Assert.IsTrue(g.Lines[0].Dashed, "home to first");
        CollectionAssert.AreEqual(new[] { Home, a }, g.Lines[0].Points.ToList());
        Assert.IsFalse(g.Lines[1].Dashed);
        CollectionAssert.AreEqual(new[] { a, b }, g.Lines[1].Points.ToList());
        Assert.IsTrue(g.Lines[2].Dashed, "last back to home");
        CollectionAssert.AreEqual(new[] { b, Home }, g.Lines[2].Points.ToList());

        Assert.AreEqual(100 + 100 + Math.Sqrt(2) * 100, g.TotalDistance, 1.5);
        Assert.AreEqual(b, g.LastPoint);
        CollectionAssert.AreEqual(new[] { 1, 2 }, g.Markers.Select(m => m.Number).ToArray());
        Assert.IsTrue(g.Markers.All(m => m.Kind == PlanMarkerKind.Waypoint && m.Radius == 2));
    }

    [TestMethod]
    public void LandingEndsTheRouteWithoutAReturnLeg()
    {
        var a = Offset(Home, 100, 0);
        var land = Offset(Home, 200, 0);
        var items = new List<MissionItem>
        {
            new() { Command = MAVLink.MAV_CMD.TAKEOFF, Alt = 20 }, // at the current position: not drawn
            At(a),
            At(land, MAVLink.MAV_CMD.LAND),
        };

        var g = PlanGeometry.ForMission(Home, items, 2, 45);

        Assert.HasCount(1, g.Lines);
        Assert.IsFalse(g.Lines[0].Dashed);
        CollectionAssert.AreEqual(new[] { Home, a, land }, g.Lines[0].Points.ToList());
        CollectionAssert.AreEqual(new[] { PlanMarkerKind.Waypoint, PlanMarkerKind.Land }, g.Markers.Select(m => m.Kind).ToArray());
        CollectionAssert.AreEqual(new[] { 2, 3 }, g.Markers.Select(m => m.Number).ToArray(), "numbers are list positions");
        Assert.AreEqual(200, g.TotalDistance, 1);
    }

    [TestMethod]
    public void RoiIsMarkedButNotFlown()
    {
        var a = Offset(Home, 100, 0);
        var roi = Offset(Home, 50, 300);
        var b = Offset(Home, 100, 100);
        var items = new List<MissionItem> { At(a), At(roi, MAVLink.MAV_CMD.DO_SET_ROI), At(b) };

        var g = PlanGeometry.ForMission(null, items, 2, 45);

        Assert.HasCount(1, g.Lines);
        CollectionAssert.AreEqual(new[] { a, b }, g.Lines[0].Points.ToList());
        Assert.AreEqual(PlanMarkerKind.Roi, g.Markers.Single(m => m.Number == 2).Kind);
    }

    [TestMethod]
    public void LoiterUsesItsOwnRadiusOrTheDefault()
    {
        var a = Offset(Home, 100, 0);
        var b = Offset(Home, 200, 0);
        var turns = At(a, MAVLink.MAV_CMD.LOITER_TURNS);
        turns.P3 = -60; // counter-clockwise, 60 m
        var items = new List<MissionItem> { turns, At(b, MAVLink.MAV_CMD.LOITER_UNLIM) };

        var g = PlanGeometry.ForMission(Home, items, 2, 45);

        Assert.AreEqual(60, g.Markers[0].Radius);
        Assert.AreEqual(45, g.Markers[1].Radius);
        Assert.IsTrue(g.Markers.All(m => m.Kind == PlanMarkerKind.Loiter));
    }

    [TestMethod]
    public void JumpDrawsTheRepeatedLegs()
    {
        var a = Offset(Home, 100, 0);
        var b = Offset(Home, 100, 100);
        var jump = new MissionItem { Command = MAVLink.MAV_CMD.DO_JUMP, P1 = 1, P2 = 1 };
        var items = new List<MissionItem> { At(a), At(b), jump };

        var g = PlanGeometry.ForMission(null, items, 2, 45);

        // a, b, then back to WP 1 and 2 once more
        CollectionAssert.AreEqual(new[] { a, b, a, b }, g.Lines.Single().Points.ToList());
    }

    [TestMethod]
    public void InsertHandlesSitHalfwayAlongEachLeg()
    {
        var a = Offset(Home, 100, 0);
        var b = Offset(Home, 100, 100);
        var items = new List<MissionItem> { At(a), At(b) };

        var g = PlanGeometry.ForMission(Home, items, 2, 45);

        Assert.HasCount(2, g.Midpoints);
        Assert.AreEqual(0, g.Midpoints[0].InsertIndex, "home leg inserts before WP 1");
        Assert.AreEqual(1, g.Midpoints[1].InsertIndex);
        Assert.AreEqual((a.Lat + b.Lat) / 2, g.Midpoints[1].Position.Lat, 1e-9);
        Assert.AreEqual(MidpointTarget.Mission, g.Midpoints[1].Target);
    }

    [TestMethod]
    public void FenceShapesFromTheList()
    {
        var v = new[] { Offset(Home, 0, 0), Offset(Home, 300, 0), Offset(Home, 300, 300) };
        var items = new List<MissionItem>();
        items.AddRange(MissionEditing.FencePolygon(v, inclusion: true));
        items.Add(new MissionItem { Command = MAVLink.MAV_CMD.FENCE_CIRCLE_EXCLUSION, P1 = 25, Lat = v[1].Lat, Lng = v[1].Lng });
        items.Add(new MissionItem { Command = MAVLink.MAV_CMD.FENCE_HOME_CIRCLE_INCLUSION, P1 = 500 });
        items.Add(new MissionItem { Command = MAVLink.MAV_CMD.FENCE_RETURN_POINT, Lat = v[2].Lat, Lng = v[2].Lng });

        var g = PlanGeometry.ForFence(Home, items);

        var poly = g.Polygons.Single();
        Assert.IsTrue(poly.Inclusion);
        CollectionAssert.AreEqual(v, poly.Points.ToList());
        Assert.HasCount(3, g.Midpoints, "one per edge, closing edge included");
        Assert.AreEqual(3, g.Midpoints[2].InsertIndex, "closing edge inserts after the last vertex");

        Assert.HasCount(2, g.Circles);
        var exclusion = g.Circles.Single(c => !c.AroundHome);
        Assert.IsFalse(exclusion.Inclusion);
        Assert.AreEqual(25, exclusion.Radius);
        var home = g.Circles.Single(c => c.AroundHome);
        Assert.AreEqual(Home, home.Center);
        Assert.AreEqual(500, home.Radius);

        // three vertices, the circle centre and the return point; the home circle has no marker
        Assert.HasCount(5, g.Markers);
        Assert.AreEqual(PlanMarkerKind.FenceReturn, g.Markers[^1].Kind);
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 5 }, g.Markers.Select(m => m.Index).ToArray());
    }

    [TestMethod]
    public void HomeCircleNeedsAHome()
    {
        var items = new List<MissionItem> { new() { Command = MAVLink.MAV_CMD.FENCE_HOME_CIRCLE_INCLUSION, P1 = 500 } };
        Assert.IsEmpty(PlanGeometry.ForFence(null, items).Circles);
    }

    [TestMethod]
    public void RallyPointsAreMarkersOnly()
    {
        var r = Offset(Home, 10, 10);
        var items = new List<MissionItem>
        {
            new() { Command = MAVLink.MAV_CMD.RALLY_POINT, Lat = r.Lat, Lng = r.Lng, Alt = 30 },
            new() { Command = MAVLink.MAV_CMD.RALLY_POINT }, // unset
        };

        var g = PlanGeometry.ForRally(items);

        Assert.HasCount(1, g.Markers);
        Assert.AreEqual(PlanMarkerKind.Rally, g.Markers[0].Kind);
        Assert.IsEmpty(g.Lines);
    }

    [TestMethod]
    public void NavigationCommandsMatchMissionPlannersMap()
    {
        Assert.IsTrue(PlanGeometry.IsNavigation(MAVLink.MAV_CMD.WAYPOINT));
        Assert.IsTrue(PlanGeometry.IsNavigation(MAVLink.MAV_CMD.SPLINE_WAYPOINT));
        Assert.IsTrue(PlanGeometry.IsNavigation(MAVLink.MAV_CMD.DO_LAND_START));
        Assert.IsTrue(PlanGeometry.IsNavigation(MAVLink.MAV_CMD.DO_SET_ROI));
        Assert.IsFalse(PlanGeometry.IsNavigation(MAVLink.MAV_CMD.RETURN_TO_LAUNCH));
        Assert.IsFalse(PlanGeometry.IsNavigation(MAVLink.MAV_CMD.DELAY));
        Assert.IsFalse(PlanGeometry.IsNavigation(MAVLink.MAV_CMD.DO_CHANGE_SPEED));
        Assert.IsFalse(PlanGeometry.IsNavigation(MAVLink.MAV_CMD.RALLY_POINT));
        Assert.IsTrue(PlanGeometry.HasLocation(MAVLink.MAV_CMD.WAYPOINT));
        Assert.IsFalse(PlanGeometry.HasLocation(MAVLink.MAV_CMD.DO_CHANGE_SPEED));
    }

    [TestMethod]
    public void PolygonHandlesCoverEveryEdge()
    {
        var pts = new[] { P(0, 0), P(0, 1), P(1, 1), P(1, 0) };
        var mids = PlanGeometry.PolygonMidpoints(pts);
        Assert.HasCount(4, mids);
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, mids.Select(m => m.InsertIndex).ToArray());
        Assert.AreEqual(P(0.5, 0), mids[3].Position);
        Assert.HasCount(1, PlanGeometry.PolygonMidpoints(pts.Take(2).ToList()), "two points: one edge");
    }
}
