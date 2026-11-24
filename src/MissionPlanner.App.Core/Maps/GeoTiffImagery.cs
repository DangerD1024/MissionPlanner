using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using BitMiracle.LibTiff.Classic;
using DotSpatial.Projections;
using GMap.NET;
using GMap.NET.MapProviders;
using GMap.NET.Projections;
using log4net;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.Maps;

/// <summary>
/// One georeferenced image (GeoTIFF, or a TIFF with a .tfw world file): where it is and how to read
/// its pixels. Replaces Mission Planner's GDAL imagery overlay, whose native GDAL 2.3 build only
/// exists for the .NET Framework on Windows; this reads the files with the managed LibTiff and
/// reprojects with DotSpatial, so it works on every platform.
/// </summary>
public sealed class GeoTiffImage
{
    /// <summary>Pixels kept per side at most; larger images are read at a reduced size.</summary>
    internal static int MaxSide { get; set; } = 8192;

    private static readonly ProjectionInfo Wgs84 = KnownCoordinateSystems.Geographic.World.WGS1984;

    // model coordinate of a pixel corner: X = _a0 + _a1 i + _a2 j, Y = _b0 + _b1 i + _b2 j
    private readonly double _a0, _a1, _a2, _b0, _b1, _b2, _det;
    private readonly ProjectionInfo? _projection;
    private readonly object _projectionLock = new();

    private GeoTiffImage(string file, int width, int height, double[] affine, ProjectionInfo? projection, string crs)
    {
        FileName = file;
        Width = width;
        Height = height;
        (_a0, _a1, _a2, _b0, _b1, _b2) = (affine[0], affine[1], affine[2], affine[3], affine[4], affine[5]);
        _det = _a1 * _b2 - _a2 * _b1;
        if (_det == 0)
            throw new InvalidDataException("The georeference has no extent.");
        _projection = projection;
        Crs = crs;

        // the outline in WGS84: corners and edge midpoints
        var pts = new List<(double I, double J)>();
        for (var k = 0; k <= 4; k++)
        {
            var f = k / 4.0;
            pts.Add((f * width, 0));
            pts.Add((f * width, height));
            pts.Add((0, f * height));
            pts.Add((width, f * height));
        }

        var xy = new double[pts.Count * 2];
        for (var n = 0; n < pts.Count; n++)
        {
            xy[n * 2] = _a0 + _a1 * pts[n].I + _a2 * pts[n].J;
            xy[n * 2 + 1] = _b0 + _b1 * pts[n].I + _b2 * pts[n].J;
        }

        if (_projection != null)
            Reproject.ReprojectPoints(xy, null, _projection, Wgs84, 0, pts.Count);

        double minLat = double.MaxValue, maxLat = double.MinValue, minLng = double.MaxValue, maxLng = double.MinValue;
        for (var n = 0; n < pts.Count; n++)
        {
            var lng = xy[n * 2];
            var lat = xy[n * 2 + 1];
            if (double.IsNaN(lat) || double.IsNaN(lng) || double.IsInfinity(lat) || double.IsInfinity(lng))
                continue;
            minLat = Math.Min(minLat, lat);
            maxLat = Math.Max(maxLat, lat);
            minLng = Math.Min(minLng, lng);
            maxLng = Math.Max(maxLng, lng);
        }

        if (minLat > maxLat || minLat < -90.5 || maxLat > 90.5 || minLng < -360 || maxLng > 360)
            throw new InvalidDataException("The georeference is not on the earth (wrong coordinate system?).");

        Bounds = new RectLatLng(maxLat, minLng, maxLng - minLng, maxLat - minLat);
        Resolution = (maxLng - minLng) / width;
    }

    public string FileName { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>The covered area in WGS84.</summary>
    public RectLatLng Bounds { get; }

    /// <summary>Degrees of longitude per pixel (smaller is sharper).</summary>
    public double Resolution { get; }

    /// <summary>The coordinate system, as found in the file.</summary>
    public string Crs { get; }

    /// <summary>The pixels as read (RGBA, row-major), loaded on first use; null when unreadable.</summary>
    internal PixelBuffer? Pixels { get; set; }

    internal bool LoadFailed { get; set; }

    internal long LastUsed { get; set; }

    public string Describe() => string.Format(CultureInfo.InvariantCulture,
        "{0}: {1}×{2} px, {3:0.000000},{4:0.000000} to {5:0.000000},{6:0.000000}, {7}",
        Path.GetFileName(FileName), Width, Height, Bounds.Top, Bounds.Left, Bounds.Bottom, Bounds.Right, Crs);

    /// <summary>
    /// The pixel position (column, row, pixel corners) of each lat/lng pair, in place: the array
    /// holds lng, lat pairs on entry and column, row pairs on return.
    /// </summary>
    internal void ToPixels(double[] lngLat)
    {
        var count = lngLat.Length / 2;
        if (_projection != null)
        {
            lock (_projectionLock)
                Reproject.ReprojectPoints(lngLat, null, Wgs84, _projection, 0, count);
        }

        for (var n = 0; n < count; n++)
        {
            var dx = lngLat[n * 2] - _a0;
            var dy = lngLat[n * 2 + 1] - _b0;
            lngLat[n * 2] = (_b2 * dx - _a2 * dy) / _det;
            lngLat[n * 2 + 1] = (-_b1 * dx + _a1 * dy) / _det;
        }
    }

    /// <summary>Reads where a file is. Throws with the reason when it is not a usable georeferenced image.</summary>
    public static GeoTiffImage Open(string file)
    {
        using var tiff = Tiff.Open(file, "r") ?? throw new InvalidDataException("Not a TIFF file.");
        var width = tiff.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
        var height = tiff.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
        if (width <= 0 || height <= 0)
            throw new InvalidDataException("The image is empty.");

        var keys = GeoKeys.Read(tiff);
        var affine = AffineFromTags(tiff, keys) ?? AffineFromWorldFile(file)
            ?? throw new InvalidDataException("No georeference (GeoTIFF tags or .tfw world file).");

        var (projection, crs) = Projection(file, keys, affine);
        return new GeoTiffImage(file, width, height, affine, projection, crs);
    }

    private static double[] Doubles(Tiff tiff, int tag)
    {
        var field = tiff.GetField((TiffTag)tag);
        if (field == null || field.Length < 2)
            return [];
        var bytes = field[1].ToByteArray();
        var values = new double[bytes.Length / 8];
        for (var k = 0; k < values.Length; k++)
            values[k] = BitConverter.ToDouble(bytes, k * 8);
        return values;
    }

    private static double[]? AffineFromTags(Tiff tiff, GeoKeys keys)
    {
        var tie = Doubles(tiff, 33922);
        var scale = Doubles(tiff, 33550);
        double[]? affine = null;
        if (tie.Length >= 6 && scale.Length >= 2 && scale[0] != 0 && scale[1] != 0)
        {
            // raster (I, J) at model (X, Y); rows go down while Y goes up
            affine = [tie[3] - tie[0] * scale[0], scale[0], 0, tie[4] + tie[1] * scale[1], 0, -scale[1]];
        }
        else
        {
            var m = Doubles(tiff, 34264);
            if (m.Length >= 16)
                affine = [m[3], m[0], m[1], m[7], m[4], m[5]];
        }

        if (affine == null)
            return null;

        // PixelIsPoint: the tie point is the centre of the pixel, move it to the corner
        if (keys.Short(1025) == 2)
        {
            affine[0] -= (affine[1] + affine[2]) / 2;
            affine[3] -= (affine[4] + affine[5]) / 2;
        }

        return affine;
    }

    /// <summary>A world file (.tfw, .tifw, .wld): six lines A, D, B, E, C, F; C, F is the centre of the top-left pixel.</summary>
    private static double[]? AffineFromWorldFile(string file)
    {
        foreach (var ext in new[] { ".tfw", ".tifw", ".tiffw", ".wld" })
        {
            var wf = Path.ChangeExtension(file, ext);
            if (!File.Exists(wf))
                continue;
            var v = File.ReadAllLines(wf)
                .Where(l => l.Trim().Length > 0)
                .Select(l => double.Parse(l.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture))
                .ToArray();
            if (v.Length < 6)
                continue;
            double a = v[0], d = v[1], b = v[2], e = v[3], c = v[4], f = v[5];
            return [c - (a + b) / 2, a, b, f - (d + e) / 2, d, e];
        }

        return null;
    }

    private static (ProjectionInfo? Projection, string Crs) Projection(string file, GeoKeys keys, double[] affine)
    {
        var projected = keys.Short(3072);
        var model = keys.Short(1024);
        if (projected is > 0 and < 32767)
        {
            ProjectionInfo p;
            try
            {
                p = ProjectionInfo.FromEpsgCode(projected.Value);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"Unknown projected coordinate system EPSG:{projected} ({ex.Message}).");
            }

            return (p.IsLatLon ? null : p, "EPSG:" + projected);
        }

        var prj = Path.ChangeExtension(file, ".prj");
        if (File.Exists(prj))
        {
            var p = ProjectionInfo.FromEsriString(File.ReadAllText(prj).Trim());
            return (p.IsLatLon ? null : p, Path.GetFileName(prj));
        }

        if (projected == 32767 && keys.Ascii(3073) is { Length: > 0 } citation)
        {
            try
            {
                var p = ProjectionInfo.FromEsriString(citation);
                return (p.IsLatLon ? null : p, citation);
            }
            catch (Exception)
            {
                throw new InvalidDataException("User-defined projection not supported: " + citation);
            }
        }

        if (model == 1 || projected == 32767)
            throw new InvalidDataException("Projected coordinate system without an EPSG code is not supported.");

        if (model == 2 || keys.Short(2048) != null)
            return (null, keys.Short(2048) is { } g and > 0 and < 32767 ? "EPSG:" + g : "geographic");

        // no keys at all (world file without .prj): accept it when it looks like degrees
        var x = affine[0];
        var y = affine[3];
        if (Math.Abs(x) <= 360 && Math.Abs(y) <= 90 && Math.Abs(affine[1]) < 1)
            return (null, "geographic (assumed)");
        throw new InvalidDataException("No coordinate system (add a .prj file next to the image).");
    }

    /// <summary>The GeoKey directory (tag 34735) with its double (34736) and ASCII (34737) parameters.</summary>
    private sealed class GeoKeys
    {
        private readonly Dictionary<int, object> _values = new();

        public static GeoKeys Read(Tiff tiff)
        {
            var keys = new GeoKeys();
            var dirField = tiff.GetField((TiffTag)34735);
            if (dirField == null || dirField.Length < 2)
                return keys;
            var dir = dirField[1].ToByteArray();
            var ascii = tiff.GetField((TiffTag)34737) is { Length: >= 2 } a ? a[1].ToByteArray() : [];
            var doubles = Doubles(tiff, 34736);
            ushort U(int index) => index * 2 + 2 <= dir.Length ? BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(index * 2)) : (ushort)0;
            var count = U(3);
            for (var k = 0; k < count; k++)
            {
                var id = U(4 + k * 4);
                var location = U(5 + k * 4);
                var n = U(6 + k * 4);
                var value = U(7 + k * 4);
                if (location == 0)
                    keys._values[id] = (int)value;
                else if (location == 34737 && value + n <= ascii.Length)
                    keys._values[id] = Encoding.ASCII.GetString(ascii, value, n).TrimEnd('|', '\0');
                else if (location == 34736 && value < doubles.Length)
                    keys._values[id] = doubles[value];
            }

            return keys;
        }

        public int? Short(int id) => _values.TryGetValue(id, out var v) && v is int i ? i : null;

        public string? Ascii(int id) => _values.TryGetValue(id, out var v) ? v as string : null;
    }
}

/// <summary>RGBA pixels (R in the low byte) read at 1/<see cref="Factor"/> of the image size.</summary>
internal sealed class PixelBuffer(uint[] data, int width, int height, int factor)
{
    public uint[] Data { get; } = data;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int Factor { get; } = factor;
    public long Bytes => Data.LongLength * 4;

    /// <summary>Reads the image's pixels through LibTiff's RGBA interface (any photometric, strips or tiles).</summary>
    public static PixelBuffer Load(string file, int maxSide)
    {
        using var tiff = Tiff.Open(file, "r") ?? throw new InvalidDataException("Not a TIFF file.");
        var w = tiff.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
        var h = tiff.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
        var f = Math.Max(1, (Math.Max(w, h) + maxSide - 1) / maxSide);
        var ow = (w + f - 1) / f;
        var oh = (h + f - 1) / f;
        var dst = new uint[(long)ow * oh];

        if (tiff.IsTiled())
        {
            var tw = tiff.GetField(TiffTag.TILEWIDTH)[0].ToInt();
            var th = tiff.GetField(TiffTag.TILELENGTH)[0].ToInt();
            var buf = new int[tw * th];
            for (var ty = 0; ty < h; ty += th)
            for (var tx = 0; tx < w; tx += tw)
            {
                if (!tiff.ReadRGBATile(tx, ty, buf))
                    throw new InvalidDataException("The image data cannot be read as colour.");
                // the tile comes bottom-up: buffer row th-1-i is image row ty+i
                for (var i = 0; i < th && ty + i < h; i++)
                {
                    var row = ty + i;
                    if (row % f != 0)
                        continue;
                    var src = (th - 1 - i) * tw;
                    var outRow = (long)(row / f) * ow;
                    for (var k = 0; k < tw && tx + k < w; k++)
                    {
                        var col = tx + k;
                        if (col % f == 0)
                            dst[outRow + col / f] = (uint)buf[src + k];
                    }
                }
            }
        }
        else
        {
            var rps = tiff.GetFieldDefaulted(TiffTag.ROWSPERSTRIP)[0].ToInt();
            if (rps <= 0 || rps > h)
                rps = h;
            var buf = new int[(long)w * rps];
            for (var row0 = 0; row0 < h; row0 += rps)
            {
                var n = Math.Min(rps, h - row0);
                if (!tiff.ReadRGBAStrip(row0, buf))
                    throw new InvalidDataException("The image data cannot be read as colour.");
                // the strip comes bottom-up: buffer row n-1-i is image row row0+i
                for (var i = 0; i < n; i++)
                {
                    var row = row0 + i;
                    if (row % f != 0)
                        continue;
                    var src = (long)(n - 1 - i) * w;
                    var outRow = (long)(row / f) * ow;
                    for (var col = 0; col < w; col += f)
                        dst[outRow + col / f] = (uint)buf[src + col];
                }
            }
        }

        return new PixelBuffer(dst, ow, oh, f);
    }
}

/// <summary>
/// The georeferenced images of a folder, drawn as map tiles (Ctrl+F "Custom GDAL"). Coarse images
/// are drawn first so sharper ones cover them, as Mission Planner's GDAL overlay does.
/// </summary>
public static class GeoTiffImagery
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(GeoTiffImagery));

    public const string FolderKey = "next_gdal_image_dir";
    public const string OpacityKey = "next_gdal_opacity";

    /// <summary>Pixel memory kept for decoded images before the least recently used is dropped.</summary>
    internal static long MaxLoadedBytes { get; set; } = 768L * 1024 * 1024;

    private static readonly object Lock = new();
    private static List<GeoTiffImage> _images = new();
    private static long _clock;
    private static bool _autoLoaded;
    private static double? _opacity;

    public static IReadOnlyList<GeoTiffImage> Images
    {
        get
        {
            lock (Lock)
                return _images.ToList();
        }
    }

    /// <summary>How opaque the overlay is over the base imagery, 0.05 to 1.</summary>
    public static double Opacity
    {
        get
        {
            if (_opacity == null)
            {
                _opacity = double.TryParse(Settings.Instance[OpacityKey], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 1.0;
            }

            return _opacity.Value;
        }
        set
        {
            _opacity = Math.Clamp(value, 0.05, 1.0);
            Settings.Instance[OpacityKey] = _opacity.Value.ToString(CultureInfo.InvariantCulture);
        }
    }

    public sealed record ScanResult(IReadOnlyList<GeoTiffImage> Added, IReadOnlyList<string> Skipped);

    /// <summary>
    /// Adds the georeferenced TIFFs under <paramref name="folder"/> (subfolders too) to the overlay.
    /// Files already loaded are kept once. <paramref name="progress"/> gets (fraction, file).
    /// </summary>
    public static ScanResult ScanDirectory(string folder, Action<double, string>? progress = null)
    {
        lock (Lock)
            _autoLoaded = true;
        if (!Directory.Exists(folder))
            return new ScanResult([], []);

        var files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        var added = new List<GeoTiffImage>();
        var skipped = new List<string>();
        for (var k = 0; k < files.Count; k++)
        {
            var file = files[k];
            progress?.Invoke(k / (double)files.Count, file);
            try
            {
                // 1 kB check, as Mission Planner's scan
                if (new FileInfo(file).Length < 1024)
                    continue;
                lock (Lock)
                {
                    if (_images.Any(i => string.Equals(i.FileName, file, StringComparison.OrdinalIgnoreCase)))
                        continue;
                }

                added.Add(GeoTiffImage.Open(file));
            }
            catch (Exception ex)
            {
                Log.Info("GeoTIFF " + file, ex);
                skipped.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        lock (Lock)
        {
            _images = _images.Concat(added)
                .OrderByDescending(i => i.Resolution)
                .ToList();
        }

        GMaps.Instance.MemoryCache.Clear();
        progress?.Invoke(1, "");
        return new ScanResult(added, skipped);
    }

    /// <summary>Removes every image from the overlay.</summary>
    public static void Clear()
    {
        lock (Lock)
        {
            _images = new List<GeoTiffImage>();
            _autoLoaded = true;
        }

        GMaps.Instance.MemoryCache.Clear();
    }

    /// <summary>The first time the overlay is drawn or listed, loads the folder chosen last time.</summary>
    public static void EnsureLoaded()
    {
        string folder;
        lock (Lock)
        {
            if (_autoLoaded)
                return;
            _autoLoaded = true;
            folder = Settings.Instance[FolderKey] ?? "";
        }

        if (folder.Length > 0 && Directory.Exists(folder))
            ScanDirectory(folder);
    }

    /// <summary>
    /// The overlay for one Web Mercator tile as a PNG with transparency, or null when no image
    /// covers it.
    /// </summary>
    public static byte[]? RenderTile(long tileX, long tileY, int zoom, int size = 256)
    {
        EnsureLoaded();
        var prj = MercatorProjection.Instance;
        var px = tileX * size;
        var py = tileY * size;
        var tl = prj.FromPixelToLatLng(px, py, zoom);
        var br = prj.FromPixelToLatLng(px + size, py + size, zoom);
        var tileRect = new RectLatLng(tl.Lat, tl.Lng, br.Lng - tl.Lng, tl.Lat - br.Lat);

        List<GeoTiffImage> images;
        lock (Lock)
            images = _images.Where(i => i.Bounds.IntersectsWith(tileRect)).ToList();
        if (images.Count == 0)
            return null;

        const int grid = 16;
        var output = new uint[size * size];
        var drawn = false;
        var opacity = Opacity;
        foreach (var image in images)
        {
            var pixels = PixelsOf(image);
            if (pixels == null)
                continue;

            // pixel position of a 17×17 grid over the tile, interpolated in between
            var pts = new double[(grid + 1) * (grid + 1) * 2];
            for (var gy = 0; gy <= grid; gy++)
            for (var gx = 0; gx <= grid; gx++)
            {
                var ll = prj.FromPixelToLatLng(px + gx * size / grid, py + gy * size / grid, zoom);
                var n = (gy * (grid + 1) + gx) * 2;
                pts[n] = ll.Lng;
                pts[n + 1] = ll.Lat;
            }

            image.ToPixels(pts);
            var f = (double)pixels.Factor;
            var cell = size / (double)grid;
            for (var y = 0; y < size; y++)
            {
                var gy = Math.Min(grid - 1, (int)((y + 0.5) / cell));
                var fy = (y + 0.5) / cell - gy;
                for (var x = 0; x < size; x++)
                {
                    var gx = Math.Min(grid - 1, (int)((x + 0.5) / cell));
                    var fx = (x + 0.5) / cell - gx;
                    var i00 = (gy * (grid + 1) + gx) * 2;
                    var i10 = i00 + 2;
                    var i01 = i00 + (grid + 1) * 2;
                    var i11 = i01 + 2;
                    var u = Lerp(Lerp(pts[i00], pts[i10], fx), Lerp(pts[i01], pts[i11], fx), fy);
                    var v = Lerp(Lerp(pts[i00 + 1], pts[i10 + 1], fx), Lerp(pts[i01 + 1], pts[i11 + 1], fx), fy);
                    if (u < 0 || v < 0 || u >= image.Width || v >= image.Height)
                        continue;
                    var sx = Math.Min(pixels.Width - 1, (int)(u / f));
                    var sy = Math.Min(pixels.Height - 1, (int)(v / f));
                    var c = pixels.Data[(long)sy * pixels.Width + sx];
                    var a = (c >> 24) * opacity;
                    if (a < 1)
                        continue;
                    output[y * size + x] = Over(c & 0xFFFFFF, a / 255.0, output[y * size + x]);
                    drawn = true;
                }
            }
        }

        return drawn ? Png.Encode(output, size, size) : null;
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>Source colour with alpha <paramref name="alpha"/> over a destination pixel (straight alpha).</summary>
    private static uint Over(uint rgb, double alpha, uint dst)
    {
        var da = (dst >> 24) / 255.0;
        var oa = alpha + da * (1 - alpha);
        if (oa <= 0)
            return 0;
        uint Channel(int shift)
        {
            var s = (rgb >> shift) & 0xFF;
            var d = (dst >> shift) & 0xFF;
            return (uint)Math.Round((s * alpha + d * da * (1 - alpha)) / oa) & 0xFF;
        }

        return Channel(0) | (Channel(8) << 8) | (Channel(16) << 16) | ((uint)Math.Round(oa * 255) << 24);
    }

    private static PixelBuffer? PixelsOf(GeoTiffImage image)
    {
        lock (image)
        {
            image.LastUsed = Interlocked.Increment(ref _clock);
            if (image.Pixels != null || image.LoadFailed)
                return image.Pixels;
            try
            {
                image.Pixels = PixelBuffer.Load(image.FileName, GeoTiffImage.MaxSide);
            }
            catch (Exception ex)
            {
                Log.Warn("GeoTIFF pixels " + image.FileName, ex);
                image.LoadFailed = true;
                return null;
            }
        }

        TrimMemory(image);
        return image.Pixels;
    }

    /// <summary>Drops the least recently drawn images' pixels while over the memory budget.</summary>
    private static void TrimMemory(GeoTiffImage keep)
    {
        List<GeoTiffImage> loaded;
        lock (Lock)
            loaded = _images.Where(i => i.Pixels != null).OrderBy(i => i.LastUsed).ToList();
        var total = loaded.Sum(i => i.Pixels?.Bytes ?? 0);
        foreach (var image in loaded)
        {
            if (total <= MaxLoadedBytes)
                break;
            if (ReferenceEquals(image, keep))
                continue;
            lock (image)
            {
                total -= image.Pixels?.Bytes ?? 0;
                image.Pixels = null;
            }
        }
    }
}

/// <summary>
/// The GeoTIFF overlay as a map: satellite imagery with the loaded images drawn over it
/// (Mission Planner's "GDAL Custom" map type).
/// </summary>
public sealed class GeoTiffImageryProvider : GMapProvider
{
    public static readonly GeoTiffImageryProvider Instance;

    private static readonly Lazy<byte[]> Transparent = new(() => Png.Encode(new uint[256 * 256], 256, 256));

    private GeoTiffImageryProvider()
    {
        MaxZoom = 24;
        BypassCache = true;
    }

    static GeoTiffImageryProvider()
    {
        Instance = new GeoTiffImageryProvider();
        try
        {
            // as Mission Planner's Custom/GDAL providers: findable by DbId (the tile cache looks providers up so)
            var field = typeof(GMapProviders).GetField("DbHash", BindingFlags.Static | BindingFlags.NonPublic);
            if (field?.GetValue(null) is Dictionary<int, GMapProvider> list)
                list.TryAdd(Instance.DbId, Instance);
        }
        catch (Exception)
        {
            // only the lookup by id is missing then
        }
    }

    public override Guid Id { get; } = new("8C1E5E1A-58F4-4C34-9E7D-0B4C2A6F3D11");

    public override string Name => "GeoTIFF Custom";

    private GMapProvider[]? _overlays;

    public override GMapProvider[] Overlays => _overlays ??= [GoogleSatelliteMapProvider.Instance, this];

    public override PureProjection Projection => MercatorProjection.Instance;

    public override PureImage? GetTileImage(GPoint pos, int zoom)
    {
        var bytes = GeoTiffImagery.RenderTile(pos.X, pos.Y, zoom) ?? Transparent.Value;
        var ms = new MemoryStream(bytes, 0, bytes.Length, false, true);
        var image = (TileImageProxy ?? MapTiles.TileBytesProxy.Instance).FromStream(ms);
        if (image != null)
            image.Data = ms;
        return image;
    }
}

/// <summary>A minimal PNG writer (8-bit RGBA, no filtering) for generated tiles.</summary>
internal static class Png
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Encode(uint[] rgba, int width, int height)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 6; // RGBA
        Chunk(ms, "IHDR", ihdr);

        using (var raw = new MemoryStream())
        {
            using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
            {
                var row = new byte[1 + width * 4];
                for (var y = 0; y < height; y++)
                {
                    row[0] = 0;
                    for (var x = 0; x < width; x++)
                        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(1 + x * 4), rgba[y * width + x]);
                    z.Write(row);
                }
            }

            Chunk(ms, "IDAT", raw.ToArray());
        }

        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> b4 = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b4, data.Length);
        s.Write(b4);
        var t = Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        var crc = 0xFFFFFFFFu;
        foreach (var b in t)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        BinaryPrimitives.WriteUInt32BigEndian(b4, crc ^ 0xFFFFFFFFu);
        s.Write(b4);
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        return table;
    }
}
