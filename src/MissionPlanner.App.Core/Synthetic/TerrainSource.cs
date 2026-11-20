using System.Collections.Concurrent;
using GMap.NET.MapProviders;
using log4net;
using MissionPlanner.App.Core.Maps;
using StbImageSharp;

namespace MissionPlanner.App.Core.Synthetic;

/// <summary>
/// Ground heights from AWS Terrarium tiles (free, worldwide, PNG-encoded metres; the source ArduDeck
/// uses), downloaded once into the data folder and decoded in managed code.
/// </summary>
public sealed class TerrainSource
{
    public const string UrlTemplate = "https://s3.amazonaws.com/elevation-tiles-prod/terrarium/{0}/{1}/{2}.png";
    private static readonly ILog Log = LogManager.GetLogger(typeof(TerrainSource));
    private static readonly HttpClient Http = CreateHttp();

    private readonly string _cacheDir;
    private readonly ConcurrentDictionary<(int Z, int X, int Y), float[]?> _tiles = new();
    private readonly SemaphoreSlim _downloads = new(4);

    public TerrainSource(string cacheDir) => _cacheDir = cacheDir;

    /// <summary>Tests replace the network (bytes of a Terrarium PNG, or null when there is none).</summary>
    public Func<int, int, int, CancellationToken, Task<byte[]?>>? Download { get; set; }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MissionPlanner/3");
        return http;
    }

    /// <summary>A tile's 256×256 heights (row 0 north), from memory, disk or the network; null if unavailable.</summary>
    public async Task<float[]?> GetTileAsync(int z, int x, int y, CancellationToken cancel)
    {
        var n = 1 << z;
        x = ((x % n) + n) % n;
        if (y < 0 || y >= n)
            return null;
        if (_tiles.TryGetValue((z, x, y), out var cached))
            return cached;

        var file = Path.Combine(_cacheDir, z.ToString(), x.ToString(), y + ".png");
        byte[]? png = null;
        if (File.Exists(file))
        {
            png = await File.ReadAllBytesAsync(file, cancel).ConfigureAwait(false);
        }
        else
        {
            await _downloads.WaitAsync(cancel).ConfigureAwait(false);
            try
            {
                png = Download != null ? await Download(z, x, y, cancel).ConfigureAwait(false) : await FetchAsync(z, x, y, cancel).ConfigureAwait(false);
                if (png != null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    await File.WriteAllBytesAsync(file, png, cancel).ConfigureAwait(false);
                }
            }
            finally
            {
                _downloads.Release();
            }
        }

        var heights = png == null ? null : Decode(png);
        // a failed download is not remembered: the next rebuild tries again
        if (heights != null)
            _tiles[(z, x, y)] = heights;
        return heights;
    }

    private static async Task<byte[]?> FetchAsync(int z, int x, int y, CancellationToken cancel)
    {
        try
        {
            using var response = await Http.GetAsync(string.Format(UrlTemplate, z, x, y), cancel).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(cancel).ConfigureAwait(false) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancel.IsCancellationRequested)
        {
            Log.Warn($"terrain tile {z}/{x}/{y}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Decodes a Terrarium PNG into 256×256 heights.</summary>
    public static float[]? Decode(byte[] png)
    {
        try
        {
            var img = ImageResult.FromMemory(png, ColorComponents.RedGreenBlue);
            if (img.Width != Tiles.Size || img.Height != Tiles.Size)
                return null;
            var h = new float[Tiles.Size * Tiles.Size];
            var d = img.Data;
            for (var i = 0; i < h.Length; i++)
                h[i] = Tiles.Terrarium(d[i * 3], d[i * 3 + 1], d[i * 3 + 2]);
            return h;
        }
        catch (Exception ex)
        {
            Log.Warn("terrain tile decode: " + ex.Message);
            return null;
        }
    }

    /// <summary>The height at a point from tiles already in memory (bilinear); null if not loaded yet.</summary>
    public float? HeightAt(double lat, double lng, int zoom)
    {
        var fx = Tiles.X(lng, zoom);
        var fy = Tiles.Y(lat, zoom);
        var tx = (int)Math.Floor(fx);
        var ty = (int)Math.Floor(fy);
        if (!_tiles.TryGetValue((zoom, tx, ty), out var tile) || tile == null)
            return null;
        return Sample(tile, (fx - tx) * Tiles.Size, (fy - ty) * Tiles.Size);
    }

    private static float Sample(float[] tile, double px, double py)
    {
        px = Math.Clamp(px - 0.5, 0, Tiles.Size - 1.001);
        py = Math.Clamp(py - 0.5, 0, Tiles.Size - 1.001);
        var x0 = (int)px;
        var y0 = (int)py;
        var x1 = Math.Min(x0 + 1, Tiles.Size - 1);
        var y1 = Math.Min(y0 + 1, Tiles.Size - 1);
        var fx = (float)(px - x0);
        var fy = (float)(py - y0);
        var a = tile[y0 * Tiles.Size + x0] * (1 - fx) + tile[y0 * Tiles.Size + x1] * fx;
        var b = tile[y1 * Tiles.Size + x0] * (1 - fx) + tile[y1 * Tiles.Size + x1] * fx;
        return a * (1 - fy) + b * fy;
    }

    /// <summary>
    /// An n×n grid of heights over ±<paramref name="half"/> metres around the frame's origin (row 0
    /// north, column 0 west). Missing tiles leave NaN.
    /// </summary>
    public async Task<float[]> GridAsync(LocalFrame frame, double half, int n, int zoom, CancellationToken cancel)
    {
        var grid = new float[n * n];
        var step = 2 * half / (n - 1);

        // the tiles the grid covers, fetched in parallel
        var (northLat, westLng) = frame.ToGeo(-half, half);
        var (southLat, eastLng) = frame.ToGeo(half, -half);
        var tx0 = (int)Math.Floor(Tiles.X(westLng, zoom));
        var tx1 = (int)Math.Floor(Tiles.X(eastLng, zoom));
        var ty0 = (int)Math.Floor(Tiles.Y(northLat, zoom));
        var ty1 = (int)Math.Floor(Tiles.Y(southLat, zoom));
        var jobs = new List<Task<float[]?>>();
        var keys = new List<(int X, int Y)>();
        for (var ty = ty0; ty <= ty1; ty++)
        {
            for (var tx = tx0; tx <= tx1; tx++)
            {
                keys.Add((tx, ty));
                jobs.Add(GetTileAsync(zoom, tx, ty, cancel));
            }
        }

        var tiles = await Task.WhenAll(jobs).ConfigureAwait(false);
        var map = new Dictionary<(int, int), float[]?>();
        for (var i = 0; i < keys.Count; i++)
            map[keys[i]] = tiles[i];

        for (var row = 0; row < n; row++)
        {
            for (var col = 0; col < n; col++)
            {
                var (lat, lng) = frame.ToGeo(-half + col * step, half - row * step);
                var fx = Tiles.X(lng, zoom);
                var fy = Tiles.Y(lat, zoom);
                var tx = (int)Math.Floor(fx);
                var ty = (int)Math.Floor(fy);
                grid[row * n + col] = map.TryGetValue((tx, ty), out var tile) && tile != null
                    ? Sample(tile, (fx - tx) * Tiles.Size, (fy - ty) * Tiles.Size)
                    : float.NaN;
            }
        }

        return grid;
    }
}

/// <summary>An RGBA picture covering a Mercator tile rectangle (the satellite drape).</summary>
public sealed record DrapeImage(byte[] Rgba, int Width, int Height, int Zoom, int TileX0, int TileY0, int TilesX, int TilesY);

/// <summary>Stitches map tiles (the map's provider, through its cache) into one texture.</summary>
public static class Drape
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(Drape));

    /// <summary>The tiles over ±<paramref name="half"/> m, at most <paramref name="maxTiles"/> per side (the zoom drops to fit).</summary>
    public static async Task<DrapeImage?> BuildAsync(GMapProvider provider, LocalFrame frame, double half, int zoom, int maxTiles,
        CancellationToken cancel, Func<GMapProvider, long, long, int, CancellationToken, Task<byte[]?>>? fetch = null)
    {
        fetch ??= MapTiles.GetTileAsync;
        var (northLat, westLng) = frame.ToGeo(-half, half);
        var (southLat, eastLng) = frame.ToGeo(half, -half);
        int tx0, ty0, nx, ny;
        while (true)
        {
            tx0 = (int)Math.Floor(Tiles.X(westLng, zoom));
            ty0 = (int)Math.Floor(Tiles.Y(northLat, zoom));
            nx = (int)Math.Floor(Tiles.X(eastLng, zoom)) - tx0 + 1;
            ny = (int)Math.Floor(Tiles.Y(southLat, zoom)) - ty0 + 1;
            if ((nx <= maxTiles && ny <= maxTiles) || zoom <= 1)
                break;
            zoom--;
        }

        var w = nx * Tiles.Size;
        var h = ny * Tiles.Size;
        var rgba = new byte[w * h * 4];
        // unloaded tiles stay a neutral ground colour rather than black
        for (var i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = 96;
            rgba[i + 1] = 104;
            rgba[i + 2] = 80;
            rgba[i + 3] = 255;
        }

        var any = false;
        var jobs = new List<Task>();
        var gate = new object();
        for (var j = 0; j < ny; j++)
        {
            for (var i = 0; i < nx; i++)
            {
                var (ci, cj) = (i, j);
                jobs.Add(Task.Run(async () =>
                {
                    byte[]? bytes;
                    try
                    {
                        bytes = await fetch(provider, tx0 + ci, ty0 + cj, zoom, cancel).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Log.Warn($"drape tile {zoom}/{tx0 + ci}/{ty0 + cj}: {ex.Message}");
                        return;
                    }

                    if (bytes == null)
                        return;
                    ImageResult img;
                    try
                    {
                        img = ImageResult.FromMemory(bytes, ColorComponents.RedGreenBlueAlpha);
                    }
                    catch (Exception)
                    {
                        return;
                    }

                    if (img.Width != Tiles.Size || img.Height != Tiles.Size)
                        return;
                    lock (gate)
                    {
                        any = true;
                        for (var y = 0; y < Tiles.Size; y++)
                            Buffer.BlockCopy(img.Data, y * Tiles.Size * 4, rgba, ((cj * Tiles.Size + y) * w + ci * Tiles.Size) * 4, Tiles.Size * 4);
                    }
                }, cancel));
            }
        }

        await Task.WhenAll(jobs).ConfigureAwait(false);
        return any ? new DrapeImage(rgba, w, h, zoom, tx0, ty0, nx, ny) : null;
    }

    /// <summary>Texture coordinate of a point on the drape.</summary>
    public static (float U, float V) Uv(DrapeImage d, double lat, double lng) =>
        ((float)((Tiles.X(lng, d.Zoom) - d.TileX0) / d.TilesX), (float)((Tiles.Y(lat, d.Zoom) - d.TileY0) / d.TilesY));
}
