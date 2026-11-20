using System.Numerics;
using MissionPlanner.App.Core.Synthetic;

namespace MissionPlanner.App.Tests.Synthetic;

[TestClass]
public class SyntheticMathTests
{
    private static void Near(Vector3 expected, Vector3 actual, float tol = 1e-4f) =>
        Assert.IsLessThan(tol, Vector3.Distance(expected, actual), $"expected {expected}, got {actual}");

    [TestMethod]
    public void BodyAxesFollowYawPitchAndRoll()
    {
        // level, facing north: forward is -z (north), right is +x (east), down is -y
        var b = SceneCamera.BodyToWorld(0, 0, 0);
        Near(new Vector3(0, 0, -1), new Vector3(b.M11, b.M12, b.M13));
        Near(new Vector3(1, 0, 0), new Vector3(b.M21, b.M22, b.M23));
        Near(new Vector3(0, -1, 0), new Vector3(b.M31, b.M32, b.M33));

        // facing east
        b = SceneCamera.BodyToWorld(0, 0, 90);
        Near(new Vector3(1, 0, 0), new Vector3(b.M11, b.M12, b.M13));

        // nose 30° up: forward climbs
        b = SceneCamera.BodyToWorld(0, 30, 0);
        Near(new Vector3(0, 0.5f, -MathF.Sqrt(3) / 2), new Vector3(b.M11, b.M12, b.M13));

        // right wing down 90°: the right axis points at the ground
        b = SceneCamera.BodyToWorld(90, 0, 0);
        Near(new Vector3(0, -1, 0), new Vector3(b.M21, b.M22, b.M23));
    }

    [TestMethod]
    public void FirstPersonFieldOfViewMatchesTheHudPitchLadder()
    {
        // the HUD draws h/50 pixels per degree; a point 1° above the nose must land there
        const float h = 600, w = 800;
        var view = SceneCamera.FirstPerson(Vector3.Zero, 0, 0, 0);
        var proj = SceneCamera.Projection(SceneCamera.HudVerticalFov, w / h, 1, 10000);
        var ahead = SceneCamera.Project(new Vector3(0, 0, -1000), view, proj)!.Value;
        Assert.AreEqual(0.5f, ahead.Y, 1e-4f);
        Assert.AreEqual(0.5f, ahead.X, 1e-4f);

        var up = new Vector3(0, MathF.Tan(MathF.PI / 180) * 1000, -1000);
        var p = SceneCamera.Project(up, view, proj)!.Value;
        Assert.AreEqual(h / 50, (ahead.Y - p.Y) * h, 0.05, "pixels per degree at the centre");
    }

    [TestMethod]
    public void TheHorizonCanSitBelowTheMiddleLikeTheHud()
    {
        var view = SceneCamera.FirstPerson(Vector3.Zero, 0, 0, 0);
        var proj = SceneCamera.Projection(SceneCamera.HudVerticalFov, 4f / 3, 1, 10000, centreDownFraction: 0.03f);
        var ahead = SceneCamera.Project(new Vector3(0, 0, -1000), view, proj)!.Value;
        Assert.AreEqual(0.53f, ahead.Y, 1e-4f);
        Assert.IsNull(SceneCamera.Project(new Vector3(0, 0, 1000), view, proj), "behind the camera");
    }

    [TestMethod]
    public void TheChaseCameraSitsBehindAndAbove()
    {
        var target = new Vector3(0, 100, 0);
        var cam = SceneCamera.ChasePosition(target, headingDeg: 0, elevationDeg: 30, distance: 10);
        // looking north means sitting to the south (+z), 5 m up
        Near(new Vector3(0, 105, 8.660254f), cam);
        cam = SceneCamera.ChasePosition(target, 90, 0, 10);
        Near(new Vector3(-10, 100, 0), cam);
    }

    [TestMethod]
    public void TileMathRoundTripsAndDecodesTerrarium()
    {
        foreach (var (lat, lng) in new[] { (-35.363261, 149.16523), (51.5, -0.12), (0.0, 0.0) })
        {
            Assert.AreEqual(lng, Tiles.Lng(Tiles.X(lng, 13), 13), 1e-9);
            Assert.AreEqual(lat, Tiles.Lat(Tiles.Y(lat, 13), 13), 1e-9);
        }

        Assert.AreEqual(0f, Tiles.Terrarium(128, 0, 0));
        Assert.AreEqual(584.5f, Tiles.Terrarium(130, 72, 128));
        Assert.AreEqual(-100f, Tiles.Terrarium(127, 156, 0));
        Assert.AreEqual(4891.97, Tiles.MetresPerTile(0, 13), 0.01);
    }

    [TestMethod]
    public void LocalFrameIsMetresEastAndNorth()
    {
        var f = new LocalFrame(-35.363261, 149.16523);
        var (e, n) = f.ToLocal(-35.363261 + 1.0 / 111319.49, 149.16523);
        Assert.AreEqual(0, e, 1e-6);
        Assert.AreEqual(1, n, 1e-3);
        var (lat, lng) = f.ToGeo(1500, -700);
        var back = f.ToLocal(lat, lng);
        Assert.AreEqual(1500, back.East, 1e-6);
        Assert.AreEqual(-700, back.North, 1e-6);
    }

    [TestMethod]
    public void APatchMeshesItsHeightsAndFillsTheGaps()
    {
        var spec = new PatchSpec(100, 3, 13, 17, 4);
        // a slope rising to the east, one height missing
        var grid = new[] { 10f, 20f, 30f, 10f, float.NaN, 30f, 10f, 20f, 30f };
        var p = TerrainPatch.Build(new LocalFrame(0, 0), spec, grid, null, fallbackHeight: 0);
        Assert.IsTrue(p.HasHeights);
        Assert.AreEqual(9 * TerrainPatch.FloatsPerVertex, p.Vertices.Length);
        Assert.HasCount(4 * 6, p.Indices);
        Assert.AreEqual(20f, p.HeightAt(0, 0)!.Value, 1e-4f, "the gap takes the average");
        Assert.AreEqual(15f, p.HeightAt(-50, 0)!.Value, 1e-4f, "interpolated");
        Assert.IsNull(p.HeightAt(150, 0), "outside");

        // the slope leans the normals west (up-slope is east)
        Assert.IsLessThan(0f, p.Vertices[4 * TerrainPatch.FloatsPerVertex + 3]);

        var flat = TerrainPatch.Build(new LocalFrame(0, 0), spec, Enumerable.Repeat(float.NaN, 9).ToArray(), null, fallbackHeight: 584);
        Assert.IsFalse(flat.HasHeights);
        Assert.AreEqual(584f, flat.HeightAt(10, 10)!.Value);
    }

    [TestMethod]
    public void PosesArePredictedFromVelocityAndResetOnJumps()
    {
        var t = new PoseTracker();
        var pose = new VehiclePose(-35, 149, 600, 0, 0, 0, VelNorth: 10, VelEast: 0, VelDown: -2, false, true, VehicleShape.Plane);
        t.Add(pose, 0);
        var a = t.At(0)!.Value;
        Assert.AreEqual(-35, a.Lat, 1e-9);

        // 0.2 s later, with no new sample, it has moved on ~2 m north and climbed ~0.4 m (blended)
        var b = t.At(0.2)!.Value;
        var (_, north) = new LocalFrame(-35, 149).ToLocal(b.Lat, b.Lng);
        Assert.IsTrue(north is > 1.0 and < 2.01, $"north {north}");
        Assert.IsTrue(b.AltMsl > 600 && b.AltMsl <= 600.41, $"alt {b.AltMsl}");

        // a jump of kilometres is not glided across
        t.Add(pose with { Lat = -34.9 }, 0.3);
        var c = t.At(0.3)!.Value;
        Assert.AreEqual(-34.9, c.Lat, 1e-6);
    }

    [TestMethod]
    public void YawIsBlendedTheShortWayRound()
    {
        var t = new PoseTracker();
        var pose = new VehiclePose(0, 0, 0, 0, 0, 350, 0, 0, 0, false, true, VehicleShape.Multirotor);
        t.Add(pose, 0);
        t.At(0);
        t.Add(pose with { Yaw = 10 }, 0.05);
        var y = t.At(0.06)!.Value.Yaw;
        Assert.IsTrue(y is > 350 or < 10.001, $"yaw {y} went the long way");
    }

    [TestMethod]
    public void TheAltitudeDatumIsLearntOnTheGround()
    {
        var d = new AltitudeDatum();
        var onGround = new VehiclePose(0, 0, 580, 0, 0, 0, 0, 0, 0, Armed: false, true, VehicleShape.Multirotor);
        d.Update(onGround, ground: 584, eyeHeight: 0.3);
        Assert.IsTrue(d.Learnt);
        Assert.AreEqual(4.3, d.Offset, 1e-9);
        Assert.AreEqual(684.3, d.EyeAltitude(680, 584), 1e-9, "the offset is kept in flight");

        // armed or moving: not relearnt
        d.Update(onGround with { Armed = true, AltMsl = 500 }, 584, 0.3);
        Assert.AreEqual(4.3, d.Offset, 1e-9);
        Assert.AreEqual(586, d.EyeAltitude(400, 584), 1e-9, "never below the ground plus the floor");
    }
}
