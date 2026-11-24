using System.Security.Cryptography;
using MissionPlanner.App.Core.Maps;

namespace MissionPlanner.App.Tests;

/// <summary>
/// Every map offered in the app returns real imagery from its server. Talks to the internet, so it
/// runs only with MP_TEST_NETWORK=1.
/// </summary>
[TestClass]
[TestCategory("Network")]
public class MapProviderNetworkTests
{
    /// <summary>OpenStreetMap's "Access blocked" picture, sent to apps breaking its tile policy.</summary>
    private const string OsmBlockedSha256 = "ed65ab236c17a3d866bd5e097c7a79b433b8bba2d590c8e5d3ff1649437c7ff1";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task EveryOfferedMapReturnsImagery()
    {
        if (Environment.GetEnvironmentVariable("MP_TEST_NETWORK") != "1")
            Assert.Inconclusive("set MP_TEST_NETWORK=1 to fetch tiles from the map servers");

        MapTiles.Initialize();
        var errors = new List<string>();
        MapTiles.TileFailed += (p, z, e) =>
        {
            lock (errors)
                errors.Add($"{p.Name} z{z}: {e.Message}");
        };

        var failed = new List<string>();
        // the local maps (injected tiles, GeoTIFF overlay) have no server
        foreach (var provider in MapTiles.CommonProviders.Except(MapTiles.LocalProviders))
        {
            // CMAC, Canberra, zoom 15
            var px = provider.Projection.FromLatLngToPixel(-35.363261, 149.165230, 15);
            var tile = provider.Projection.FromPixelToTileXY(px);
            // as the map does: Google looks up its imagery version in the background on first use,
            // and failed tiles are retried
            MapTiles.PrepareProvider(provider);
            var result = await MapTiles.GetTileLayersAsync(provider, tile.X, tile.Y, 15);
            for (var attempt = 0; attempt < 5 && !result.Complete; attempt++)
            {
                await Task.Delay(2000);
                result = await MapTiles.GetTileLayersAsync(provider, tile.X, tile.Y, 15);
            }

            var first = result.Layers.FirstOrDefault();
            var hash = first == null ? "" : Convert.ToHexStringLower(SHA256.HashData(first));
            TestContext.WriteLine($"{provider.Name}: {result.Layers.Count} layer(s), {first?.Length ?? 0} bytes, complete={result.Complete}");

            if (!result.Complete || first == null || first.Length < 500)
                failed.Add(provider.Name + " returned no imagery");
            else if (hash == OsmBlockedSha256)
                failed.Add(provider.Name + " answered 'Access blocked'");
        }

        lock (errors)
            TestContext.WriteLine(string.Join("\n", errors));
        Assert.IsEmpty(failed, string.Join("; ", failed));
    }
}
