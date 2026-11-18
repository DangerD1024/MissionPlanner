using System.Collections.ObjectModel;
using GMap.NET;
using MissionPlanner.App.Core.Mission;
using MissionPlanner.App.Core.ViewModels.FlightPlan;
using static MissionPlanner.App.Tests.FlightPlan.PlanTestSupport;

namespace MissionPlanner.App.Tests.FlightPlan;

[TestClass]
public class MissionEditingTests
{
    private static readonly PointLatLng Base = P(-35.363261, 149.165230);

    private static MissionItem Vertex(MAVLink.MAV_CMD cmd, int count, double lat = -35.36, double lng = 149.16) =>
        new() { Command = cmd, P1 = count, Lat = lat, Lng = lng, Frame = AltFrame.Absolute };

    [TestMethod]
    public void MoveUpAndDownStopAtTheEnds()
    {
        var a = Wp(1, 1);
        var b = Wp(2, 2);
        var c = Wp(3, 3);
        var list = new ObservableCollection<MissionItem> { a, b, c };

        Assert.IsTrue(MissionEditing.Move(list, c, -1));
        CollectionAssert.AreEqual(new[] { a, c, b }, list.ToList());
        Assert.IsTrue(MissionEditing.Move(list, a, +1));
        CollectionAssert.AreEqual(new[] { c, a, b }, list.ToList());
        Assert.IsFalse(MissionEditing.Move(list, c, -1), "first row cannot go up");
        Assert.IsFalse(MissionEditing.Move(list, b, +1), "last row cannot go down");
        Assert.IsFalse(MissionEditing.Move(list, Wp(9, 9), +1), "not in the list");
    }

    [TestMethod]
    public void FencePolygonsAreSplitByVertexCount()
    {
        const MAVLink.MAV_CMD inc = MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION;
        const MAVLink.MAV_CMD exc = MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_EXCLUSION;
        var list = new List<MissionItem>
        {
            Vertex(inc, 3), Vertex(inc, 3), Vertex(inc, 3),
            // a second inclusion polygon right after the first
            Vertex(inc, 4), Vertex(inc, 4), Vertex(inc, 4), Vertex(inc, 4),
            new() { Command = MAVLink.MAV_CMD.FENCE_CIRCLE_EXCLUSION, P1 = 20 },
            Vertex(exc, 3), Vertex(exc, 3), Vertex(exc, 3),
        };

        var groups = MissionEditing.FencePolygonGroups(list);

        CollectionAssert.AreEqual(new[]
        {
            new FencePolygonGroup(0, 3, inc),
            new FencePolygonGroup(3, 4, inc),
            new FencePolygonGroup(8, 3, exc),
        }, groups);
        Assert.IsTrue(groups[0].Inclusion);
        Assert.IsFalse(groups[2].Inclusion);
    }

    [TestMethod]
    public void ShortPolygonRunEndsAtTheNextCommand()
    {
        const MAVLink.MAV_CMD inc = MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION;
        var list = new List<MissionItem> { Vertex(inc, 5), Vertex(inc, 5), new() { Command = MAVLink.MAV_CMD.FENCE_RETURN_POINT } };
        CollectionAssert.AreEqual(new[] { new FencePolygonGroup(0, 2, inc) }, MissionEditing.FencePolygonGroups(list));
    }

    [TestMethod]
    public void DeletingAFenceVertexShrinksItsPolygon()
    {
        const MAVLink.MAV_CMD inc = MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION;
        var list = new ObservableCollection<MissionItem>
        {
            Vertex(inc, 4), Vertex(inc, 4), Vertex(inc, 4), Vertex(inc, 4),
            Vertex(inc, 3), Vertex(inc, 3), Vertex(inc, 3),
        };

        MissionEditing.Delete(list, list[1]);

        Assert.HasCount(6, list);
        CollectionAssert.AreEqual(new[] { 3f, 3f, 3f, 3f, 3f, 3f }, list.Select(i => i.P1).ToArray());
        CollectionAssert.AreEqual(new[] { new FencePolygonGroup(0, 3, inc), new FencePolygonGroup(3, 3, inc) },
            MissionEditing.FencePolygonGroups(list));
    }

    [TestMethod]
    public void InsertingAFenceVertexGrowsItsPolygon()
    {
        const MAVLink.MAV_CMD exc = MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_EXCLUSION;
        var list = new ObservableCollection<MissionItem>
        {
            new() { Command = MAVLink.MAV_CMD.FENCE_RETURN_POINT },
            Vertex(exc, 3), Vertex(exc, 3), Vertex(exc, 3),
        };
        var group = MissionEditing.FencePolygonGroups(list).Single();

        var added = MissionEditing.InsertFenceVertex(list, group, 3, -35.1, 149.1);

        Assert.AreSame(added, list[3]);
        Assert.AreEqual(exc, added.Command);
        CollectionAssert.AreEqual(new[] { 0f, 4f, 4f, 4f, 4f }, list.Select(i => i.P1).ToArray());
        CollectionAssert.AreEqual(new[] { new FencePolygonGroup(1, 4, exc) }, MissionEditing.FencePolygonGroups(list));
    }

    [TestMethod]
    public void DeletingAWaypointLeavesOthersAlone()
    {
        var list = new ObservableCollection<MissionItem> { Wp(1, 1), Wp(2, 2), Wp(3, 3) };
        var keep = list[2];
        MissionEditing.Delete(list, list[1]);
        CollectionAssert.AreEqual(new[] { 1.0, 3.0 }, list.Select(i => i.Lat).ToArray());
        Assert.AreSame(keep, list[1]);
    }

    [TestMethod]
    public void FencePolygonFromDrawnPointsCarriesTheCount()
    {
        var pts = new[] { P(1, 1), P(1, 2), P(2, 2), P(2, 1) };
        var items = MissionEditing.FencePolygon(pts, inclusion: false);
        Assert.HasCount(4, items);
        Assert.IsTrue(items.All(i => i.Command == MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_EXCLUSION && i.P1 == 4));
        CollectionAssert.AreEqual(pts.Select(p => p.Lat).ToArray(), items.Select(i => i.Lat).ToArray());
        CollectionAssert.AreEqual(pts.Select(p => p.Lng).ToArray(), items.Select(i => i.Lng).ToArray());
    }

    [TestMethod]
    public void InsertGoesIntoTheNearestLeg()
    {
        // home, then 1 north 200 m, 2 north-east, 3 east
        var w1 = Offset(Base, 200, 0);
        var w2 = Offset(Base, 200, 200);
        var w3 = Offset(Base, 0, 200);
        var items = new List<MissionItem>
        {
            Wp(w1.Lat, w1.Lng),
            new() { Command = MAVLink.MAV_CMD.DO_CHANGE_SPEED, P2 = 5 }, // not on the route
            Wp(w2.Lat, w2.Lng),
            Wp(w3.Lat, w3.Lng),
        };

        // beside the home -> 1 leg
        Assert.AreEqual(0, MissionEditing.InsertIndexForPoint(Base, items, Offset(Base, 100, -10)));
        // beside the 1 -> 2 leg: goes before item 2 (index 2), after the DO command
        Assert.AreEqual(2, MissionEditing.InsertIndexForPoint(Base, items, Offset(Base, 210, 100)));
        // beside the 2 -> 3 leg
        Assert.AreEqual(3, MissionEditing.InsertIndexForPoint(Base, items, Offset(Base, 100, 215)));
        // past the last waypoint: appended
        Assert.AreEqual(4, MissionEditing.InsertIndexForPoint(Base, items, Offset(Base, -150, 200)));
        // without home the first leg is 1 -> 2
        Assert.AreEqual(2, MissionEditing.InsertIndexForPoint(null, items, Offset(Base, 100, -10)));
        // nothing to split
        Assert.AreEqual(0, MissionEditing.InsertIndexForPoint(null, new List<MissionItem>(), Base));
    }

    [TestMethod]
    public void SegmentDistanceIsMetricAndClamped()
    {
        var a = Base;
        var b = Offset(Base, 0, 100);
        var (mid, t) = MissionEditing.DistanceToSegment(Offset(Base, 30, 50), a, b);
        Assert.AreEqual(30, mid, 0.5);
        Assert.AreEqual(0.5, t, 0.01);

        var (beyond, t2) = MissionEditing.DistanceToSegment(Offset(Base, 0, 140), a, b);
        Assert.AreEqual(40, beyond, 0.5);
        Assert.IsGreaterThan(1.0, t2);
    }

    [TestMethod]
    public void PolygonAreaOfAHectare()
    {
        var square = new[] { Base, Offset(Base, 100, 0), Offset(Base, 100, 100), Offset(Base, 0, 100) };
        Assert.AreEqual(10_000, MissionEditing.PolygonArea(square), 50);
        Assert.AreEqual(5_000, MissionEditing.PolygonArea(square.Take(3).ToList()), 25);
        Assert.AreEqual(0, MissionEditing.PolygonArea(square.Take(2).ToList()));
        Assert.AreEqual("1.00 ha", MissionEditing.FormatArea(10_000));
        Assert.AreEqual("2.50 km²", MissionEditing.FormatArea(2_500_000));
        Assert.AreEqual("950 m²", MissionEditing.FormatArea(950));
        Assert.AreEqual("1.25 km", MissionEditing.FormatDistance(1250));
        Assert.AreEqual("80 m", MissionEditing.FormatDistance(80.2));
    }

    [TestMethod]
    public void PolygonFileRoundTrips()
    {
        var path = Path.Combine(TestEnvironment.DataRoot, "survey.poly");
        var pts = new List<PointLatLng> { P(-35.1234567, 149.7654321), P(-35.2, 149.8), P(-35.3, 149.7) };

        MissionEditing.WritePolygonFile(path, pts);
        var lines = File.ReadAllLines(path);
        Assert.StartsWith("#", lines[0]);
        Assert.AreEqual(lines[1], lines[^1], "the loop is closed in the file, as Mission Planner writes it");

        var back = MissionEditing.ReadPolygonFile(path);
        CollectionAssert.AreEqual(pts, back, "the closing duplicate is dropped");
    }
}
