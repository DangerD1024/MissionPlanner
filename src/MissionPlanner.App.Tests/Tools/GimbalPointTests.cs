using MissionPlanner.App.Core.ViewModels.Tools;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Tests.Tools;

[TestClass]
public class GimbalPointTests
{
    private const double Lat = -35.363261, Lng = 149.165230;

    [TestMethod]
    public void AngleInputMapsPwmOntoTheLimits()
    {
        Assert.AreEqual(-45, GimbalPointing.AngleInput(false, 1500, 1100, 1900, -90, 0), 1e-9);
        Assert.AreEqual(-90, GimbalPointing.AngleInput(false, 1100, 1100, 1900, -90, 0), 1e-9);
        Assert.AreEqual(0, GimbalPointing.AngleInput(true, 1100, 1100, 1900, -90, 0), 1e-9);
        Assert.AreEqual(-90, GimbalPointing.AngleInput(true, 1900, 1100, 1900, -90, 0), 1e-9);
    }

    [TestMethod]
    public void SegmentsCrossOrNot()
    {
        var hit = GimbalPointing.Intersection((0, 10), (10, 0), (0, 0), (10, 10));
        Assert.IsNotNull(hit);
        Assert.AreEqual(5, hit.Value.X, 1e-9);
        Assert.AreEqual(5, hit.Value.Y, 1e-9);
        Assert.IsNull(GimbalPointing.Intersection((0, 0), (1, 0), (0, 1), (1, 1)), "parallel");
        Assert.IsNull(GimbalPointing.Intersection((0, 10), (10, 0), (20, 0), (30, 10)), "beyond the end");
    }

    [TestMethod]
    public void FortyFiveDegreesDownFromAHundredMetresOverFlatGround()
    {
        var t = GimbalPointing.Project(Lat, Lng, 100, -45, 90, (_, _) => 0)!;
        Assert.AreEqual(100, t.Distance, 0.01);
        Assert.AreEqual(0, t.GroundAlt, 0.01);
        Assert.AreEqual(90, t.Bearing, 1e-9);
        var here = new PointLatLngAlt(Lat, Lng);
        Assert.AreEqual(100, here.GetDistance(new PointLatLngAlt(t.Lat, t.Lng)), 0.5);
        Assert.AreEqual(90, here.GetBearing(new PointLatLngAlt(t.Lat, t.Lng)), 0.5);
    }

    [TestMethod]
    public void RisingGroundIsHitSooner()
    {
        // ground 50 m above sea level everywhere, vehicle at 150 m: 100 m above it, 45° down → 100 m
        var t = GimbalPointing.Project(Lat, Lng, 150, -45, 0, (_, _) => 50)!;
        Assert.AreEqual(100, t.Distance, 0.01);
        Assert.AreEqual(50, t.GroundAlt, 0.01);

        // a slope rising 1 m per 10 m: tan(45°) line meets it at d = 100 / 1.1
        var origin = new PointLatLngAlt(Lat, Lng);
        var slope = GimbalPointing.Project(Lat, Lng, 100, -45, 0,
            (la, ln) => origin.GetDistance(new PointLatLngAlt(la, ln)) / 10)!;
        Assert.AreEqual(100 / 1.1, slope.Distance, 0.5);
    }

    [TestMethod]
    public void StraightDownAndAboveTheHorizon()
    {
        var down = GimbalPointing.Project(Lat, Lng, 80, -90, 0, (_, _) => 20)!;
        Assert.AreEqual(0, down.Distance, 0.01);
        Assert.AreEqual(20, down.GroundAlt, 0.01);
        Assert.IsNull(GimbalPointing.Project(Lat, Lng, 80, 0, 0, (_, _) => 0), "level: never reaches the ground");
        Assert.IsNull(GimbalPointing.Project(Lat, Lng, 100, -1, 0, (_, _) => 0), "a shallow angle lands beyond 1 km");
    }

    [TestMethod]
    public void AnglesFromTheMountServoWhenTheMountReportsNothing()
    {
        var cs = new CurrentState { ch7out = 1500, ch6out = 1900, pitch = 5 };
        var p = new Dictionary<string, double>
        {
            ["SERVO7_FUNCTION"] = 7, ["SERVO7_MIN"] = 1100, ["SERVO7_MAX"] = 1900,
            ["MNT1_PITCH_MIN"] = -90, ["MNT1_PITCH_MAX"] = 0,
            ["SERVO6_FUNCTION"] = 6, ["SERVO6_MIN"] = 1100, ["SERVO6_MAX"] = 1900,
            ["MNT1_YAW_MIN"] = -180, ["MNT1_YAW_MAX"] = 180,
        };
        var a = GimbalPointing.Angles(cs, n => p.TryGetValue(n, out var v) ? v : null, null);
        Assert.AreEqual("mount servo outputs", a.Source);
        Assert.AreEqual(180, a.Yaw, 1e-6);
        // servo −45° plus the vehicle's 5° pitch
        Assert.AreEqual(-40, a.Pitch, 1e-6);

        // the older centidegree limits
        var old = new Dictionary<string, double>
        {
            ["SERVO7_FUNCTION"] = 7, ["SERVO7_MIN"] = 1000, ["SERVO7_MAX"] = 2000,
            ["MNT_ANGMIN_TIL"] = -9000, ["MNT_ANGMAX_TIL"] = 0,
        };
        var cs2 = new CurrentState { ch7out = 1000 };
        Assert.AreEqual(-90, GimbalPointing.Angles(cs2, n => old.TryGetValue(n, out var v) ? v : null, null).Pitch, 1e-6);
    }

    [TestMethod]
    public void AnglesFromMountStatusWin()
    {
        var cs = new CurrentState { campointa = -30, campointb = 0, campointc = 15, ch7out = 1500 };
        var a = GimbalPointing.Angles(cs, n => n == "SERVO7_FUNCTION" ? 7 : null, null);
        Assert.AreEqual("mount status", a.Source);
        Assert.AreEqual(-30, a.Pitch, 1e-6);
        Assert.AreEqual(15, a.Yaw, 1e-6);
    }
}
