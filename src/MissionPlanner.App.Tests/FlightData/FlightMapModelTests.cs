using MissionPlanner.App.Core.Mission;
using MissionPlanner.App.Core.ViewModels.FlightData;
using MissionPlanner.ArduPilot;
using Cmd = MAVLink.MAV_CMD;

namespace MissionPlanner.App.Tests.FlightData;

[TestClass]
public class FlightMapModelTests
{
    private static readonly TrackPoint Home = new(-35.36, 149.16);

    private static PlanItem Wp(Cmd cmd, double lat, double lng, float p1 = 0) => new(cmd, p1, lat, lng);

    [TestMethod]
    public void RouteRunsFromHomeThroughNavigationItems()
    {
        var mission = new[]
        {
            Wp(Cmd.TAKEOFF, 0, 0),                  // no position: not drawn
            Wp(Cmd.WAYPOINT, -35.361, 149.161),
            Wp(Cmd.DO_SET_ROI, -35.37, 149.17),     // a marker, not part of the route
            Wp(Cmd.DO_CHANGE_SPEED, 0, 0),
            Wp(Cmd.LOITER_TURNS, -35.362, 149.162),
            Wp(Cmd.RETURN_TO_LAUNCH, 0, 0),
        };
        var o = MissionOverlay.Build(Home, mission, [], []);

        CollectionAssert.AreEqual(new[] { Home, new TrackPoint(-35.361, 149.161), new TrackPoint(-35.362, 149.162) }, o.Route.ToArray());
        Assert.AreEqual(Home, o.Home);

        var wps = o.Markers.Where(m => m.Kind == MissionMarkerKind.Waypoint).Select(m => m.Label).ToArray();
        CollectionAssert.AreEqual(new[] { "2", "5" }, wps, "labels are the item numbers");
        Assert.AreEqual("3", o.Markers.Single(m => m.Kind == MissionMarkerKind.Other).Label);
    }

    [TestMethod]
    public void NoHomeNoHomeLeg()
    {
        var o = MissionOverlay.Build(null, [Wp(Cmd.WAYPOINT, 1, 1), Wp(Cmd.WAYPOINT, 2, 2)], [], []);
        Assert.IsNull(o.Home);
        Assert.HasCount(2, o.Route);
    }

    [TestMethod]
    public void HomeAloneIsNotARoute()
    {
        var o = MissionOverlay.Build(Home, [], [], []);
        Assert.IsEmpty(o.Route);
        Assert.IsTrue(o.IsEmpty);
    }

    [TestMethod]
    public void FencePolygonsCloseAtTheirVertexCount()
    {
        var fence = new[]
        {
            Wp(Cmd.FENCE_POLYGON_VERTEX_INCLUSION, 1, 1, 3),
            Wp(Cmd.FENCE_POLYGON_VERTEX_INCLUSION, 1, 2, 3),
            Wp(Cmd.FENCE_POLYGON_VERTEX_INCLUSION, 2, 2, 3),
            Wp(Cmd.FENCE_POLYGON_VERTEX_EXCLUSION, 5, 5, 4),
            Wp(Cmd.FENCE_POLYGON_VERTEX_EXCLUSION, 5, 6, 4),
            Wp(Cmd.FENCE_POLYGON_VERTEX_EXCLUSION, 6, 6, 4),
            Wp(Cmd.FENCE_POLYGON_VERTEX_EXCLUSION, 6, 5, 4),
            Wp(Cmd.FENCE_CIRCLE_EXCLUSION, 7, 7, 50),
            Wp(Cmd.FENCE_CIRCLE_INCLUSION, 8, 8, 0),  // no radius: skipped
            Wp(Cmd.FENCE_RETURN_POINT, 3, 3),
        };
        var o = MissionOverlay.Build(null, [], fence, []);

        Assert.HasCount(2, o.FencePolygons);
        Assert.IsTrue(o.FencePolygons[0].Inclusion);
        Assert.HasCount(3, o.FencePolygons[0].Points);
        Assert.IsFalse(o.FencePolygons[1].Inclusion);
        Assert.HasCount(4, o.FencePolygons[1].Points);

        var circle = o.FenceCircles.Single();
        Assert.AreEqual(50, circle.RadiusMetres);
        Assert.IsFalse(circle.Inclusion);

        Assert.AreEqual(1, o.Markers.Count(m => m.Kind == MissionMarkerKind.FenceReturn));
    }

    [TestMethod]
    public void RallyPointsAreLabelled()
    {
        var o = MissionOverlay.Build(null, [], [], [Wp(Cmd.RALLY_POINT, 1, 1), Wp(Cmd.RALLY_POINT, 2, 2)]);
        CollectionAssert.AreEqual(new[] { "R1", "R2" }, o.Markers.Select(m => m.Label).ToArray());
        Assert.IsTrue(o.Markers.All(m => m.Kind == MissionMarkerKind.Rally));
    }

    [TestMethod]
    public void BuildsFromTheMissionStore()
    {
        var store = new MissionStore();
        store.SetHome(-35.36, 149.16, 584);
        store.Mission.Add(store.NewWaypoint(-35.361, 149.161));
        store.Mission.Add(store.NewWaypoint(-35.362, 149.162));

        var planned = MissionOverlay.Build(store, vehicleHome: null);
        Assert.AreEqual(new TrackPoint(-35.36, 149.16), planned.Home, "the planned home when the vehicle has none");
        Assert.HasCount(3, planned.Route);

        var live = MissionOverlay.Build(store, new TrackPoint(-35.3, 149.1));
        Assert.AreEqual(new TrackPoint(-35.3, 149.1), live.Route[0], "the vehicle's home wins");
    }

    [TestMethod]
    public void VehicleIconsFollowTheHeartbeatType()
    {
        Assert.AreEqual(VehicleIconKind.Plane, VehicleIcons.For(MAVLink.MAV_TYPE.FIXED_WING, Firmwares.ArduCopter2));
        Assert.AreEqual(VehicleIconKind.Plane, VehicleIcons.For(MAVLink.MAV_TYPE.VTOL_TILTROTOR, Firmwares.ArduPlane));
        Assert.AreEqual(VehicleIconKind.Multirotor, VehicleIcons.For(MAVLink.MAV_TYPE.HEXAROTOR, Firmwares.ArduCopter2));
        Assert.AreEqual(VehicleIconKind.Rover, VehicleIcons.For(MAVLink.MAV_TYPE.GROUND_ROVER, Firmwares.ArduRover));
        Assert.AreEqual(VehicleIconKind.Boat, VehicleIcons.For(MAVLink.MAV_TYPE.SURFACE_BOAT, Firmwares.ArduRover));
        Assert.AreEqual(VehicleIconKind.Boat, VehicleIcons.For(MAVLink.MAV_TYPE.SUBMARINE, Firmwares.ArduSub));
    }

    [TestMethod]
    public void VehicleIconsFallBackToTheFirmware()
    {
        Assert.AreEqual(VehicleIconKind.Plane, VehicleIcons.For(MAVLink.MAV_TYPE.GENERIC, Firmwares.ArduPlane));
        Assert.AreEqual(VehicleIconKind.Rover, VehicleIcons.For(MAVLink.MAV_TYPE.GENERIC, Firmwares.ArduRover));
        Assert.AreEqual(VehicleIconKind.Multirotor, VehicleIcons.For(MAVLink.MAV_TYPE.GENERIC, Firmwares.ArduCopter2));
    }
}
