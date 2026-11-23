using GMap.NET;
using GMap.NET.MapProviders;
using GMap.NET.Projections;
using MissionPlanner.App.Core.Maps;
using MissionPlanner.App.Core.Synthetic;
using StbImageSharp;
using System.Collections.Concurrent;

namespace MissionPlanner.App.Tests.Synthetic;

[TestClass]
public class DrapeTests
{
    // A 256 px tile with red, green, blue and yellow quadrants.
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAQAAAAEACAYAAABccqhmAAACcElEQVR42u3WQQ0AIAwEwUKQgJj6f1UEXsAGSWck3GNz4+x9g7ay" +
        "bNDZNAEIACAAgAAAAgAIACAAgAAAAgAIACAAgAAAAgAIACAAgAAAAgAIACAAgAAAAgAIACAAgAAAAgAIACAAgAAAAgAIACAAgAAA" +
        "AgAIACAAgAAAAgACAAgAIACAAAACAAgAIACAAAACAAgAIACAAAACAAgAIACAAAACAAgAIACAAAACAAgAIACAAAACAAgAIACAAAAC" +
        "AAgAIACAAAACAAgAIACAAAACAAIACAAgAIAAAAIACAAgAIAAAAIACAAgAIAAAAIACAAgAIAAAAIACAAgAIAAAAIACAAgAIAAAAIA" +
        "CAAgAIAAAAIACAAgAIAAAAIACAAgAIAAAAIAAgAIACAAgAAAAgAIACAAgAAAAgAIACAAgAAAAgAIACAAgAAAf1kZZYXGKtIIHgAg" +
        "AIAAAAIACAAgAIAAAAIACAAgAIAAAAIACAAgAIAAAAIACAAgAIAAAAIACAAgAIAAAAIACAAgAIAAAAIACAAgAIAAAAIACAAgAIAA" +
        "AAIAAmACEABAAAABAAQAEABAAAABAAQAEABAAAABAAQAEABAAAABAAQAEABAAAABAAQAEABAAAABAAQAEABAAAABAAQAEABAAAAB" +
        "AAQAEABAAAABAAQABAAQAEAAAAEABAAQAEAAAAEABAAQAEAAAAEABAAQAEAAAAEABAAQAEAAAAEABAAQAEAAAAEABAAQAEAAAAEA" +
        "BAAQAEAAAAEABAAQAEAAAAEABAAEABAAQAAAAQAEABAAQAAAAQAEABAAQAAAAQAEABAAQAAAAQAEAPjFAydGBwWrXMhGAAAAAElF" +
        "TkSuQmCC");

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task FailedTilesStayTransparentAndAreMarkedForRetry(int failure)
    {
        // Straddles four tiles, with two good tiles and two missing/corrupt/throwing tiles.
        var frame = new LocalFrame(0, 0);
        var drape = await Drape.BuildAsync(GMapProviders.OpenStreetMap, frame, 100, 4, 4, default,
            (_, x, _, _, _) => x == 7 ? Task.FromResult<byte[]?>(Png) : failure switch
            {
                1 => Task.FromResult<byte[]?>([1, 2, 3]),
                2 => throw new IOException("temporary tile failure"),
                _ => Task.FromResult<byte[]?>(null),
            });
        Assert.IsNotNull(drape);
        Assert.IsFalse(drape.Complete);
        Assert.AreEqual((byte)255, drape.Rgba[3], "downloaded imagery stays opaque");
        Assert.AreEqual((byte)0, drape.Rgba[Tiles.Size * 4 + 3], "missing imagery must leave the coarse tile visible");

        var recovered = await Drape.BuildAsync(GMapProviders.OpenStreetMap, frame, 100, 4, 4, default,
            (_, _, _, _, _) => Task.FromResult<byte[]?>(Png));
        Assert.IsNotNull(recovered);
        Assert.IsTrue(recovered.Complete);
        Assert.AreEqual((byte)255, recovered.Rgba[Tiles.Size * 4 + 3]);
    }

    [TestMethod]
    public async Task ApproachingAnExistingTileKeepsItsImageryUntilTheDetailedDownloadSucceeds()
    {
        var image = ImageResult.FromMemory(Png, ColorComponents.RedGreenBlueAlpha);
        var coarse = new DrapeImage(image.Data, 256, 256, 3, 4, 4, 1, 1);
        // The centre of this parent tile straddles four child tiles at the next detail level.
        var frame = new LocalFrame(Tiles.Lat(4.5, 3), Tiles.Lng(4.5, 3));
        var near = await Drape.BuildAsync(GMapProviders.OpenStreetMap, frame, 100, 4, 4, default,
            (_, _, _, _, _) => Task.FromResult<byte[]?>(null), coarse);
        Assert.IsNotNull(near);
        Assert.IsFalse(near.Complete, "fallback imagery still needs a detailed retry");
        Assert.AreEqual((byte)220, near.Rgba[0], "northwest child keeps the red parent quadrant");
        Assert.AreEqual((byte)20, near.Rgba[1]);
        Assert.AreEqual((byte)220, near.Rgba[Tiles.Size * 4 + 1], "northeast child keeps the green parent quadrant");
        Assert.AreEqual((byte)220, near.Rgba[Tiles.Size * near.Width * 4 + 2], "southwest child keeps the blue parent quadrant");
        Assert.AreEqual((byte)255, near.Rgba[(near.Height * near.Width - 1) * 4 + 3]);

        var detailed = await Drape.BuildAsync(GMapProviders.OpenStreetMap, frame, 100, 4, 4, default,
            (_, _, _, _, _) => Task.FromResult<byte[]?>(Png), near);
        Assert.IsNotNull(detailed);
        Assert.IsTrue(detailed.Complete);
        Assert.AreEqual((byte)220, detailed.Rgba[200 * 4 + 1], "new detail replaces the old red fallback with its own green pixels");

        // A drawing-distance increase lowers the drape zoom; reuse must work in both directions.
        var wider = await Drape.BuildAsync(GMapProviders.OpenStreetMap, frame, 100, 3, 4, default,
            (_, _, _, _, _) => Task.FromResult<byte[]?>(null), near);
        Assert.IsNotNull(wider);
        CollectionAssert.AreEqual(coarse.Rgba, wider.Rgba);
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public void StationaryAircraftRetriesMissingTilesAndAppliesDrawingDistanceChanges()
    {
        MapTiles.Initialize();
        var provider = new RecoveringProvider();
        var terrain = new TerrainSource(Path.Combine(TestEnvironment.DataRoot, "retry-terrain"))
        {
            Download = (_, _, _, _) => Task.FromResult<byte[]?>(null),
        };
        var scene = new SyntheticScene(terrain) { Imagery = () => provider };
        using var view = scene.Acquire(SyntheticViewKind.Chase);
        view.Width = 160;
        view.Height = 120;
        var pose = new VehiclePose(0, 0, 100, 0, 0, 0, 0, 0, 0, true, true, VehicleShape.Plane);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < 2)
            Tick();
        Assert.IsGreaterThan(0, provider.DetailRequests);
        var attempts = provider.DetailRequests;
        provider.Available = true;
        while (provider.SuccessfulDetails == 0 && clock.Elapsed.TotalSeconds < 12)
            Tick();
        Assert.IsGreaterThan(attempts, provider.DetailRequests, "the aircraft never moved, but incomplete imagery must be retried");
        Assert.IsGreaterThan(0, provider.SuccessfulDetails);

        scene.DrawDistance = SyntheticScene.MaxDrawDistance;
        while (provider.LowestZoom >= PatchSpec.Far.ImageryZoom && clock.Elapsed.TotalSeconds < 16)
            Tick();
        Assert.IsLessThan(PatchSpec.Far.ImageryZoom, provider.LowestZoom,
            "increasing drawing distance must rebuild the horizon at a wider zoom without moving the aircraft");

        void Tick()
        {
            scene.UpdatePose(pose);
            Thread.Sleep(40);
            if (scene.Error != null)
                Assert.Inconclusive(scene.Error);
        }
    }

    private sealed class RecoveringProvider : GMapProvider
    {
        private readonly Guid _id = Guid.NewGuid();
        private readonly ConcurrentDictionary<int, byte> _zooms = new();
        private int _detailRequests, _successfulDetails;
        public volatile bool Available;
        public int DetailRequests => Volatile.Read(ref _detailRequests);
        public int SuccessfulDetails => Volatile.Read(ref _successfulDetails);
        public int LowestZoom => _zooms.Keys.DefaultIfEmpty(int.MaxValue).Min();
        public override Guid Id => _id;
        public override string Name => "Recovering imagery test";
        public override PureProjection Projection => MercatorProjection.Instance;
        public override GMapProvider[] Overlays => [this];

        public RecoveringProvider() => BypassCache = true;

        public override PureImage GetTileImage(GPoint pos, int zoom)
        {
            _zooms.TryAdd(zoom, 0);
            if (zoom > PatchSpec.Far.ImageryZoom)
            {
                Interlocked.Increment(ref _detailRequests);
                if (!Available)
                    return null!;
                Interlocked.Increment(ref _successfulDetails);
            }

            return TileImageProxy.FromArray(Png);
        }
    }
}
