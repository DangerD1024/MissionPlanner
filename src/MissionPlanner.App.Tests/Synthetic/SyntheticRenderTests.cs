using MissionPlanner.App.Core.Synthetic;

namespace MissionPlanner.App.Tests.Synthetic;

/// <summary>
/// The 3D view rendered for real (OpenGL 3.3 off screen) over flat stand-in terrain (no network):
/// pixels are checked for sky above and ground below, roll, pitch and the chase model. Inconclusive
/// without OpenGL.
/// </summary>
[TestClass]
public class SyntheticRenderTests
{
    private const int W = 320, H = 240;

    private static SyntheticScene Scene()
    {
        var terrain = new TerrainSource(Path.Combine(TestEnvironment.DataRoot, "terrain-" + Guid.NewGuid().ToString("N")))
        {
            Download = (_, _, _, _) => Task.FromResult<byte[]?>(null),
        };
        return new SyntheticScene(terrain);
    }

    private static VehiclePose Level(double roll = 0, double pitch = 0, double yaw = 0, VehicleShape shape = VehicleShape.Multirotor) =>
        new(-35.363261, 149.16523, 700, roll, pitch, yaw, 0, 0, 0, Armed: true, HasPosition: true, shape, GroundGuessMsl: 584);

    /// <summary>Renders one settled frame; BGRA rows top-down.</summary>
    private static byte[] Render(SyntheticScene scene, SyntheticViewKind kind, VehiclePose pose, Action<SyntheticView>? setup = null)
    {
        using var view = scene.Acquire(kind);
        view.Width = W;
        view.Height = H;
        setup?.Invoke(view);
        byte[]? last = null;
        var frames = 0;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        long seq = 0;
        while (DateTime.UtcNow < deadline && frames < 12)
        {
            scene.UpdatePose(pose);
            Thread.Sleep(40);
            if (scene.Error != null)
                Assert.Inconclusive(scene.Error);
            if (view.Frames.TryRead(ref seq, (px, w, h, stride) => last = px[..(h * stride)]))
                frames++;
        }

        Assert.IsNotNull(last, "no frame rendered");
        return last;
    }

    private static (int B, int G, int R) Px(byte[] p, int x, int y)
    {
        var i = (y * W + x) * 4;
        return (p[i], p[i + 1], p[i + 2]);
    }

    private static bool IsSky((int B, int G, int R) c) => c.B > c.G && c.B > c.R + 30;

    private static bool IsGround((int B, int G, int R) c) => !IsSky(c);

    /// <summary>The first row (top-down) in a column that is no longer sky.</summary>
    private static int Horizon(byte[] p, int x)
    {
        for (var y = 0; y < H; y++)
        {
            if (IsGround(Px(p, x, y)))
                return y;
        }

        return H;
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public void LevelFirstPersonShowsSkyAboveAndGroundBelow()
    {
        var p = Render(Scene(), SyntheticViewKind.FirstPerson, Level());
        Assert.IsTrue(IsSky(Px(p, W / 2, 5)), $"top {Px(p, W / 2, 5)}");
        Assert.IsTrue(IsGround(Px(p, W / 2, H - 5)), $"bottom {Px(p, W / 2, H - 5)}");
        // 116 m above flat ground the horizon is ~0.6° below the nose: just under the middle
        var horizon = Horizon(p, W / 2);
        Assert.IsTrue(horizon is > H / 2 - 3 and < H / 2 + 8, $"horizon at row {horizon}");
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public void PitchMovesTheHorizonAsTheHudLadderDoes()
    {
        var scene = Scene();
        var level = Horizon(Render(scene, SyntheticViewKind.FirstPerson, Level()), W / 2);
        var up = Horizon(Render(scene, SyntheticViewKind.FirstPerson, Level(pitch: 10)), W / 2);
        // nose up 10°: the horizon drops by about 10 · h/50 = 48 rows
        Assert.AreEqual(48, up - level, 4, $"level {level}, pitched {up}");
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public void AWideTiltedFpvCameraPutsTheHorizonWhereTheHudLadderExpectsIt()
    {
        // 120° lens tilted up 20°: the horizon sits f·tan(20°) below the middle, f = (h/2)/tan(vfov/2),
        // which is exactly where the HUD's perspective ladder draws it
        var p = Render(Scene(), SyntheticViewKind.FirstPerson, Level(), v =>
        {
            v.HorizontalFov = 120;
            v.CameraTilt = 20;
        });
        var vfov = SceneCamera.VerticalFromHorizontal(120, (double)W / H);
        var f = H / 2.0 / Math.Tan(vfov / 2);
        var expected = H / 2.0 + f * Math.Tan(20 * Math.PI / 180);
        var horizon = Horizon(p, W / 2);
        // plus the dip of the horizon seen from 116 m (under a degree, about a row)
        Assert.AreEqual(expected, horizon, 4, $"horizon at row {horizon}, expected {expected:0.0}");
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public void RollTiltsTheHorizon()
    {
        var p = Render(Scene(), SyntheticViewKind.FirstPerson, Level(roll: 30));
        var left = Horizon(p, 40);
        var right = Horizon(p, W - 40);
        // right wing down: the world turns the other way, the horizon rises on the right
        Assert.IsTrue(left - right > 60, $"left {left}, right {right}");
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public void TheChaseViewShowsTheModelLevelHorizonAndOrbits()
    {
        var scene = Scene();
        var p = Render(scene, SyntheticViewKind.Chase, Level(roll: 40, shape: VehicleShape.Plane));
        // the horizon stays level even though the vehicle banks
        Assert.AreEqual(Horizon(p, 20), Horizon(p, W - 20), 2);
        // something that is neither sky nor plain ground at the centre: the model
        var centre = Px(p, W / 2, H / 2);
        var groundColour = Px(p, 10, H - 10);
        Assert.IsTrue(Math.Abs(centre.R - groundColour.R) + Math.Abs(centre.G - groundColour.G) + Math.Abs(centre.B - groundColour.B) > 60,
            $"centre {centre} vs ground {groundColour}");

        // looking straight down from above, there is no sky
        var top = Render(scene, SyntheticViewKind.Chase, Level(), v => v.OrbitElevation = 85);
        Assert.IsFalse(IsSky(Px(top, W / 2, 5)), $"top {Px(top, W / 2, 5)}");
    }
}
