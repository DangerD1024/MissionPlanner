using MissionPlanner.App.Core.Session;
using MissionPlanner.App.Core.Synthetic;
using SkiaSharp;

namespace MissionPlanner.App.Tests.Synthetic;

/// <summary>
/// A board without GPS dead-reckons from 0,0: the map and 3D view must not jump to "null island",
/// and the elevation data's fault along the prime meridian there must not cut a trench into the 3D
/// ground (seen as a bright vertical stripe through the view).
/// </summary>
[TestClass]
public class NullIslandTests
{
    [TestMethod]
    public void APositionNearZeroWithoutGpsIsNotAPlace()
    {
        Assert.IsFalse(VehicleLocation.IsKnown(0, 0, gpsFix: false));
        Assert.IsFalse(VehicleLocation.IsKnown(0, 0, gpsFix: true), "0,0 exactly is 'no position' in MAVLink");
        Assert.IsFalse(VehicleLocation.IsKnown(0.010643, 0.004292, gpsFix: false), "the drift seen on a GPS-less ArduPlane");
        Assert.IsFalse(VehicleLocation.IsKnown(-0.6, 0.9, gpsFix: false));
        Assert.IsFalse(VehicleLocation.IsKnown(double.NaN, 30, gpsFix: true));

        Assert.IsTrue(VehicleLocation.IsKnown(0.010643, 0.004292, gpsFix: true), "with a fix, null island is a real place");
        Assert.IsTrue(VehicleLocation.IsKnown(50.45, 30.52, gpsFix: false), "dead reckoning after losing GPS elsewhere is kept");
        Assert.IsTrue(VehicleLocation.IsKnown(-35.363261, 149.16523, gpsFix: true));
    }

    [TestMethod]
    public void TheFaultAlongThePrimeMeridianIsDroppedButLowLandIsKept()
    {
        // what AWS Terrarium serves over the sea at the west edge of the meridian: 0 m, then -498 … -12749 m
        var heights = Decode(WestOfMeridian);
        Assert.IsNotNull(heights);
        Assert.AreEqual(0f, heights[128 * 256 + 100]);
        for (var c = 252; c < 256; c++)
            Assert.IsTrue(float.IsNaN(heights[128 * 256 + c]), $"column {c} should be unknown, is {heights[128 * 256 + c]}");

        // the Dead Sea shore is real land below sea level
        var deadSea = Decode((_, _) => -430);
        Assert.AreEqual(-430f, deadSea![0], 0.01f);
    }

    [TestMethod]
    public async Task TheGroundAcrossThePrimeMeridianHasNoTrench()
    {
        var terrain = new TerrainSource(Path.Combine(TestEnvironment.DataRoot, "terrain-" + Guid.NewGuid().ToString("N")))
        {
            Download = (z, x, y, _) => Task.FromResult<byte[]?>(Png(
                x == (1 << (z - 1)) - 1 ? WestOfMeridian : x == 1 << (z - 1) ? EastOfMeridian : (_, _) => 0)),
        };
        var frame = new LocalFrame(0.0001, 0.0002);
        var spec = PatchSpec.Near;
        var grid = await terrain.GridAsync(frame, spec.Half, spec.GridSize, spec.DemZoom, CancellationToken.None);
        var patch = TerrainPatch.Build(frame, spec, grid, null, fallbackHeight: 0);

        for (var e = -spec.Half; e <= spec.Half; e += 5)
        {
            var h = patch.HeightAt(e, 0);
            Assert.IsNotNull(h);
            Assert.AreEqual(0f, h.Value, 1f, $"ground {e:0} m east of the frame: {h.Value}");
        }

        Assert.IsNull(terrain.HeightAt(0.0001, 0, spec.DemZoom), "a point on the fault has no height");
    }

    private static readonly Func<int, int, float> WestOfMeridian = (col, _) => col switch
    {
        252 => -498, 253 => -4582, 254 => -8665, 255 => -12749, _ => 0,
    };

    private static readonly Func<int, int, float> EastOfMeridian = (col, row) => WestOfMeridian(255 - col, row);

    private static float[]? Decode(Func<int, int, float> height) => TerrainSource.Decode(Png(height));

    /// <summary>A Terrarium PNG: metres + 32768 = R·256 + G + B/256.</summary>
    private static byte[] Png(Func<int, int, float> height)
    {
        using var bmp = new SKBitmap(new SKImageInfo(256, 256, SKColorType.Rgba8888, SKAlphaType.Opaque));
        for (var y = 0; y < 256; y++)
        {
            for (var x = 0; x < 256; x++)
            {
                var v = height(x, y) + 32768;
                var r = (int)(v / 256);
                var g = (int)(v - r * 256);
                var b = (int)Math.Round((v - r * 256 - g) * 256);
                bmp.SetPixel(x, y, new SKColor((byte)r, (byte)g, (byte)b));
            }
        }

        using var image = SKImage.FromBitmap(bmp);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }
}
