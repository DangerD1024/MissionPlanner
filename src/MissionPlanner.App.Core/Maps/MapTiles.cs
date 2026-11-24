using System.Collections.Concurrent;
using GMap.NET;
using GMap.NET.MapProviders;
using MissionPlanner.Maps;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.Maps;

/// <summary>
/// Tile access through the existing GMap.NET.Core providers and Mission Planner's file tile cache
/// (shared with the WinForms app: &lt;data dir&gt;/gmapcache/TileDBv3). Returns encoded image bytes;
/// decoding and drawing are the UI's job.
/// </summary>
public static class MapTiles
{
    private static readonly object InitLock = new();
    private static bool _initialized;

    /// <summary>Maximum tile downloads in flight; most tile servers throttle above this.</summary>
    private static readonly SemaphoreSlim Downloads = new(6);

    /// <summary>
    /// Must run before anything touches <see cref="GMaps.Instance"/>: without an image proxy the
    /// GMaps static constructor goes looking for the WinForms assembly.
    /// </summary>
    public static void Initialize(string? cacheDirectory = null)
    {
        lock (InitLock)
        {
            if (_initialized)
                return;

            GMapProvider.TileImageProxy = TileBytesProxy.Instance;
            GMapProvider.UserAgent = Settings.Instance.UserAgent;

            // OpenStreetMap's servers answer "Access blocked" (HTTP 418) to applications that send
            // openstreetmap.org as the Referer, which GMap.NET's provider does: send none.
            GMapProviders.OpenStreetMap.RefererUrl = string.Empty;

            var cache = new MyImageCache();
            if (cacheDirectory != null)
                cache.CacheLocation = cacheDirectory;

            GMaps.Instance.PrimaryCache = cache;
            PurgeBlockedOsmTilesOnce(cache.CacheLocation);
            GMaps.Instance.Mode = AccessMode.ServerAndCache;
            _initialized = true;
        }
    }

    /// <summary>
    /// Tiles fetched with the blocked Referer were cached as the "Access blocked" picture. Clear the
    /// OpenStreetMap folder of the shared tile cache once so they are downloaded again.
    /// </summary>
    private static void PurgeBlockedOsmTilesOnce(string cacheLocation)
    {
        const string key = "next_osm_cache_purged";
        try
        {
            if (Settings.Instance[key] == "1")
                return;
            var name = GMapProviders.OpenStreetMap.Name;
            foreach (var dir in new[]
                     {
                         Path.Combine(cacheLocation, "TileDBv3", GMapProvider.LanguageStr, name),
                         Path.Combine(cacheLocation, name),
                     })
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            Settings.Instance[key] = "1";
            Settings.Instance.Save();
        }
        catch (Exception)
        {
            // a cache that can't be cleared only means some tiles stay stale
        }
    }

    /// <summary>Built-in providers worth offering first; the full list is GMapProviders.List.</summary>
    public static IReadOnlyList<GMapProvider> CommonProviders { get; } =
    [
        GMapProviders.GoogleSatelliteMap,
        GMapProviders.GoogleHybridMap,
        GMapProviders.GoogleMap,
        GMapProviders.BingSatelliteMap,
        GMapProviders.BingHybridMap,
        GMapProviders.OpenStreetMap,
        EsriWorldImageryProvider.Instance,
        GMapProviders.ArcGIS_World_Topo_Map,
        .. LocalProviders,
    ];

    /// <summary>
    /// Maps made from the user's own files (TOOLS): "Custom" shows the tiles injected into the cache,
    /// "GeoTIFF Custom" draws loaded GeoTIFFs over satellite imagery. Last in <see cref="CommonProviders"/>.
    /// </summary>
    public static IReadOnlyList<GMapProvider> LocalProviders =>
    [
        Custom.Instance,
        GeoTiffImageryProvider.Instance,
    ];

    /// <summary>Raised on a worker thread when a provider request fails (HTTP error, throttling, non-image reply).</summary>
    public static event Action<GMapProvider, int, Exception>? TileFailed;

    private static readonly ConcurrentDictionary<Guid, bool> ProvidersInitialized = new();

    /// <summary>
    /// Runs the provider's one-time setup (the WinForms map control does this when a provider is
    /// selected). Google looks up the current imagery version here; its built-in one is stale and
    /// the server answers 404 without it. Bing fetches its tile URL metadata.
    /// </summary>
    public static void PrepareProvider(GMapProvider provider)
    {
        foreach (var layer in provider.Overlays ?? [provider])
        {
            if (ProvidersInitialized.TryAdd(layer.Id, true))
            {
                layer.OnInitialized();
                if (layer is GoogleMapProviderBase)
                    _ = GoogleVersions.RefreshOnceAsync();
            }
        }
    }

    /// <summary>
    /// One map tile as its stacked layers (a hybrid map is imagery plus a labels layer).
    /// <see cref="Complete"/> is false when any layer failed, so the caller can retry later.
    /// </summary>
    public sealed record TileLayers(IReadOnlyList<byte[]> Layers, bool Complete);

    public static async Task<TileLayers> GetTileLayersAsync(GMapProvider provider, long x, long y, int zoom, CancellationToken cancel = default)
    {
        var overlays = provider.Overlays ?? [provider];
        var layers = new List<byte[]>(overlays.Length);
        var complete = true;
        foreach (var layer in overlays)
        {
            var bytes = await GetTileAsync(layer, x, y, zoom, cancel).ConfigureAwait(false);
            if (bytes == null)
                complete = false;
            else
                layers.Add(bytes);
        }

        return new TileLayers(layers, complete);
    }

    /// <summary>
    /// Fetches one tile of a single provider (memory cache, then disk cache, then server). Returns
    /// null when the server has no tile or the request failed.
    /// </summary>
    public static async Task<byte[]?> GetTileAsync(GMapProvider provider, long x, long y, int zoom, CancellationToken cancel = default)
    {
        Initialize();

        await Downloads.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            cancel.ThrowIfCancellationRequested();
            return await Task.Run(() =>
            {
                // some providers do blocking HTTP in their setup, so it runs here, off the UI thread
                PrepareProvider(provider);
                using var image = GMaps.Instance.GetImageFrom(provider, new GPoint(x, y), zoom, out var error);
                if (error != null)
                    TileFailed?.Invoke(provider, zoom, error);
                return image?.Data?.ToArray();
            }, cancel).ConfigureAwait(false);
        }
        finally
        {
            Downloads.Release();
        }
    }

    /// <summary>
    /// Holds the encoded bytes as they are. GMap sets <see cref="PureImage.Data"/> after
    /// <see cref="FromStream"/> returns; returning null rejects non-image responses (error pages).
    /// </summary>
    internal sealed class TileBytesProxy : PureImageProxy
    {
        public static readonly TileBytesProxy Instance = new();

        public override PureImage? FromStream(Stream stream)
        {
            Span<byte> head = stackalloc byte[12];
            var start = stream.CanSeek ? stream.Position : 0;
            var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            if (stream.CanSeek)
                stream.Position = start;

            return LooksLikeImage(head[..read]) ? new TileImage() : null;
        }

        public override bool Save(Stream stream, PureImage image)
        {
            if (image?.Data == null)
                return false;
            image.Data.Position = 0;
            image.Data.CopyTo(stream);
            return true;
        }

        internal static bool LooksLikeImage(ReadOnlySpan<byte> b) =>
            (b.Length >= 8 && b[0] == 0x89 && b[1] == (byte)'P' && b[2] == (byte)'N' && b[3] == (byte)'G') ||
            (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) ||
            (b.Length >= 6 && b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F') ||
            (b.Length >= 12 && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F' &&
             b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P');
    }

    private sealed class TileImage : PureImage
    {
        public override void Dispose()
        {
            Data?.Dispose();
            Data = null;
        }
    }
}
