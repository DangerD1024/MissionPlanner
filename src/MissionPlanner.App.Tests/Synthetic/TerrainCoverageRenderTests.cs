using System.Numerics;
using MissionPlanner.App.Core.Synthetic;

namespace MissionPlanner.App.Tests.Synthetic;

[TestClass]
public class TerrainCoverageRenderTests
{
    private const int W = 320, H = 240;

    [TestMethod]
    [DataRow(-40f, false)]
    [DataRow(40f, false)]
    [DataRow(-40f, true)]
    [DataRow(40f, true)]
    [Timeout(60000, CooperativeCancellation = true)]
    public void TerrainDetailsKeepExistingImageryWhenHeightsDifferOrDownloadsFail(float nearHeight, bool missingImagery)
    {
        GlContext gl;
        try
        {
            gl = GlContext.Create();
        }
        catch (InvalidOperationException ex)
        {
            Assert.Inconclusive(ex.Message);
            return;
        }

        using var context = gl;
        using var renderer = new SceneRenderer(gl.Gl);
        using var target = new OffscreenTarget(gl.Gl, W, H);
        var far = renderer.Upload(Patch(2000, 0, 220, 20));
        var near = renderer.Upload(Patch(100, nearHeight, 20, 220, missingImagery));
        try
        {
            var eye = new Vector3(0, 140, 180);
            var setup = new SceneRenderer.FrameSetup(
                Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY),
                SceneCamera.Projection(MathF.PI / 3, (float)W / H, 0.3f, 24000),
                eye, 9000, null, Matrix4x4.Identity);

            // Imagery already visible in the distance must survive the arrival of a near patch.
            Render([(far, Vector3.Zero)]);
            AssertCovered();
            // Recentring the near patch must not leave its old coverage behind in the next frame.
            foreach (var offset in new[] { Vector3.Zero, new Vector3(40, 0, -30), new Vector3(-40, 0, 30) })
            {
                Render([(far, Vector3.Zero), (near, offset)]);
                AssertCovered();
                var point = SceneCamera.Project(new Vector3(0, nearHeight, 0) + offset, setup.View, setup.Projection)!.Value;
                var centre = Pixel((int)(point.X * W), (int)(point.Y * H));
                Assert.IsTrue(missingImagery ? centre.R > centre.G * 2 : centre.G > centre.R * 2,
                    $"expected {(missingImagery ? "coarse fallback" : "detailed imagery")}: {centre}");
            }

            // Either patch can finish loading first. A frame with only the far patch must also
            // clear the previous near patch's mask, even when another view shares the renderer.
            Render([(far, Vector3.Zero)]);
            AssertCovered();
            Assert.IsTrue(Pixel(W / 2, H / 2).R > Pixel(W / 2, H / 2).G * 2);
            using (var otherView = new OffscreenTarget(gl.Gl, W, H))
            {
                otherView.Bind();
                renderer.Draw(setup, [(near, Vector3.Zero)], W, H);
            }

            Render([(far, Vector3.Zero), (near, Vector3.Zero)]);
            AssertCovered();

            void Render((SceneRenderer.GpuPatch, Vector3)[] patches)
            {
                // Readback is one frame behind, so draw the same view twice.
                for (var i = 0; i < 2; i++)
                {
                    target.Bind();
                    renderer.Draw(setup, patches, W, H);
                    target.Read();
                }
            }

            (int R, int G, int B) Pixel(int x, int y)
            {
                var i = (y * W + x) * 4;
                return (target.Pixels[i + 2], target.Pixels[i + 1], target.Pixels[i]);
            }

            void AssertCovered()
            {
                // The camera points down and the far mesh covers the entire view. Every pixel
                // must come from red coarse terrain or green detailed terrain, never the sky.
                var gaps = 0;
                for (var y = 0; y < H; y++)
                {
                    for (var x = 0; x < W; x++)
                    {
                        var p = Pixel(x, y);
                        if (p.R <= p.G * 1.5 && p.G <= p.R * 1.5)
                            gaps++;
                    }
                }

                Assert.AreEqual(0, gaps, $"{gaps} pixels exposed the background between terrain patches");
            }
        }
        finally
        {
            renderer.Free(near);
            renderer.Free(far);
        }
    }

    private static TerrainPatch Patch(double half, float height, byte red, byte green, bool missing = false)
    {
        var spec = new PatchSpec(half, 11, 11, 14, 10);
        var drape = new DrapeImage([red, green, 20, missing ? (byte)0 : (byte)255], 1, 1, 1, 0, 0, 1, 1) { Complete = !missing };
        return TerrainPatch.Build(new LocalFrame(0, 0), spec,
            Enumerable.Repeat(height, spec.GridSize * spec.GridSize).ToArray(), drape, 0);
    }
}
