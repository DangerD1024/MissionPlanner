using GMap.NET;
using MissionPlanner.App.Core.Mission;
using MissionPlanner.App.Core.ViewModels.FlightPlan;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Tests.FlightPlan;

[TestClass]
public class AutoWaypointsTests
{
    private static readonly PointLatLng Centre = new(-35.36, 149.16);

    private static double Dist(PointLatLng a, PointLatLng b) =>
        new PointLatLngAlt(a.Lat, a.Lng).GetDistance(new PointLatLngAlt(b.Lat, b.Lng));

    [TestMethod]
    public void CircleGoesRoundAtTheRadius_BackToTheStart()
    {
        var pts = AutoWaypoints.Circle(Centre, 50, 8, 1, 0);
        Assert.HasCount(9, pts, "8 points and the closing one");
        foreach (var p in pts)
            Assert.AreEqual(50, Dist(Centre, p), 0.5);
        Assert.IsGreaterThan(Centre.Lat, pts[0].Lat, "first point north at angle 0");
        Assert.IsGreaterThan(Centre.Lng, pts[2].Lng, "clockwise: a quarter round is east");
        Assert.IsLessThan(Centre.Lng, AutoWaypoints.Circle(Centre, 50, 8, -1, 0)[2].Lng, "anticlockwise: west");
        Assert.AreEqual(pts[0].Lat, pts[^1].Lat, 1e-9);
    }

    [TestMethod]
    public void SplineCircleStacksRingsLookingAtTheCentre()
    {
        var items = AutoWaypoints.SplineCircle(Centre, 30, 5, 20, 5, 0, AltFrame.Relative);
        Assert.AreEqual(MAVLink.MAV_CMD.DO_SET_ROI, items[0].Command);
        var splines = items.Skip(1).ToList();
        Assert.HasCount(4 * 5, splines, "4 rings (5, 10, 15, 20 m) of 5 points");
        Assert.IsTrue(splines.All(i => i.Command == MAVLink.MAV_CMD.SPLINE_WAYPOINT));
        CollectionAssert.AreEqual(new[] { 5f, 10f, 15f, 20f }, splines.Select(i => i.Alt).Distinct().ToArray());
    }

    [TestMethod]
    public void CircleSurveyTakesAPhotoAtEveryStop()
    {
        var items = AutoWaypoints.CircleSurvey(Centre, 10, 12, 2, 5, 4, 0, AltFrame.Relative);
        Assert.AreEqual(MAVLink.MAV_CMD.DO_SET_ROI, items[0].Command);
        var stops = items.Skip(1).ToList();
        Assert.HasCount(2 * 5 * 2, stops, "2 rings x 5 headings (0..360) x waypoint + photo");
        Assert.AreEqual((MAVLink.MAV_CMD.WAYPOINT, 2f), (stops[0].Command, stops[0].P1));
        Assert.AreEqual((MAVLink.MAV_CMD.DO_DIGICAM_CONTROL, 1.0), (stops[1].Command, stops[1].Lat));
        Assert.AreEqual(5, Dist(Centre, new PointLatLng(stops[0].Lat, stops[0].Lng)), 0.1);
    }
}
