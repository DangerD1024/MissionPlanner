using MissionPlanner.App.Core.Mission;

namespace MissionPlanner.App.Tests.FlightPlan;

/// <summary>QGC WPL 110 files through MissionStore, as the Flight Plan's Load File / Save File use them.</summary>
[TestClass]
public class WaypointFileTests
{
    private static string TempFile(string name) => Path.Combine(TestEnvironment.DataRoot, Guid.NewGuid().ToString("N") + name);

    private static void AssertSame(MissionItem expected, MissionItem actual, string what)
    {
        Assert.AreEqual(expected.Command, actual.Command, what);
        Assert.AreEqual(expected.P1, actual.P1, 1e-6, what);
        Assert.AreEqual(expected.P2, actual.P2, 1e-6, what);
        Assert.AreEqual(expected.P3, actual.P3, 1e-6, what);
        Assert.AreEqual(expected.P4, actual.P4, 1e-6, what);
        Assert.AreEqual(expected.Lat, actual.Lat, 1e-7, what);
        Assert.AreEqual(expected.Lng, actual.Lng, 1e-7, what);
        Assert.AreEqual(expected.Alt, actual.Alt, 1e-4, what);
        Assert.AreEqual(expected.Frame, actual.Frame, what);
    }

    [TestMethod]
    public void MissionRoundTripsWithHome()
    {
        var store = new MissionStore();
        store.SetHome(-35.3632621, 149.1652374, 584.25);
        store.Mission.Add(new MissionItem { Command = MAVLink.MAV_CMD.TAKEOFF, P1 = 15, Alt = 20, Frame = AltFrame.Relative });
        store.Mission.Add(new MissionItem { Command = MAVLink.MAV_CMD.WAYPOINT, P1 = 2, Lat = -35.3611111, Lng = 149.1622222, Alt = 100 });
        store.Mission.Add(new MissionItem
        {
            Command = MAVLink.MAV_CMD.LOITER_TURNS, P1 = 3, P3 = -60, Lat = -35.36, Lng = 149.16, Alt = 120, Frame = AltFrame.Terrain,
        });
        store.Mission.Add(new MissionItem { Command = MAVLink.MAV_CMD.DO_CHANGE_SPEED, P1 = 0, P2 = 12.5f, P3 = -1 });
        store.Mission.Add(new MissionItem { Command = MAVLink.MAV_CMD.SPLINE_WAYPOINT, Lat = -35.35, Lng = 149.15, Alt = 80, Frame = AltFrame.Absolute });
        store.Mission.Add(new MissionItem { Command = MAVLink.MAV_CMD.RETURN_TO_LAUNCH });
        var path = TempFile("mission.waypoints");

        store.SaveWaypointFile(path);
        var lines = File.ReadAllLines(path);
        Assert.AreEqual("QGC WPL 110", lines[0]);
        Assert.HasCount(1 + 1 + store.Mission.Count, lines, "header, home, items");
        Assert.StartsWith("0\t1\t0\t16\t", lines[1]);

        var loaded = new MissionStore();
        loaded.LoadWaypointFile(path);

        Assert.IsTrue(loaded.HomeSet);
        Assert.AreEqual(store.HomeLat, loaded.HomeLat, 1e-7);
        Assert.AreEqual(store.HomeLng, loaded.HomeLng, 1e-7);
        Assert.AreEqual(store.HomeAlt, loaded.HomeAlt, 1e-4);
        Assert.HasCount(store.Mission.Count, loaded.Mission);
        for (var i = 0; i < store.Mission.Count; i++)
            AssertSame(store.Mission[i], loaded.Mission[i], "item " + (i + 1));
        CollectionAssert.AreEqual(Enumerable.Range(1, store.Mission.Count).ToArray(), loaded.Mission.Select(m => m.Seq).ToArray());
    }

    [TestMethod]
    public void FenceAndRallyUseTheSameFormatWithADummyHome()
    {
        var store = new MissionStore();
        store.SetHome(-35, 149, 600);
        store.Fence.Add(new MissionItem
        {
            Command = MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION, P1 = 3, Lat = -35.1, Lng = 149.1, Frame = AltFrame.Absolute,
        });
        store.Fence.Add(new MissionItem
        {
            Command = MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION, P1 = 3, Lat = -35.2, Lng = 149.1, Frame = AltFrame.Absolute,
        });
        store.Fence.Add(new MissionItem
        {
            Command = MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION, P1 = 3, Lat = -35.2, Lng = 149.2, Frame = AltFrame.Absolute,
        });
        store.Fence.Add(new MissionItem
        {
            Command = MAVLink.MAV_CMD.FENCE_CIRCLE_EXCLUSION, P1 = 25, Lat = -35.15, Lng = 149.15, Frame = AltFrame.Absolute,
        });
        store.Rally.Add(new MissionItem { Command = MAVLink.MAV_CMD.RALLY_POINT, Lat = -35.05, Lng = 149.05, Alt = 40 });

        var fencePath = TempFile("fence.waypoints");
        var rallyPath = TempFile("rally.waypoints");
        store.SaveWaypointFile(fencePath, MAVLink.MAV_MISSION_TYPE.FENCE);
        store.SaveWaypointFile(rallyPath, MAVLink.MAV_MISSION_TYPE.RALLY);
        Assert.StartsWith("0\t1\t0\t16\t0\t0\t0\t0\t0.0000000\t0.0000000\t", File.ReadAllLines(fencePath)[1], "no home in a fence file");

        var loaded = new MissionStore();
        loaded.LoadWaypointFile(fencePath, MAVLink.MAV_MISSION_TYPE.FENCE);
        loaded.LoadWaypointFile(rallyPath, MAVLink.MAV_MISSION_TYPE.RALLY);

        Assert.IsFalse(loaded.HomeSet, "a fence or rally file does not move home");
        Assert.IsEmpty(loaded.Mission);
        Assert.HasCount(4, loaded.Fence);
        for (var i = 0; i < 4; i++)
            AssertSame(store.Fence[i], loaded.Fence[i], "fence " + (i + 1));
        Assert.HasCount(1, loaded.Rally);
        AssertSame(store.Rally[0], loaded.Rally[0], "rally");
    }
}
