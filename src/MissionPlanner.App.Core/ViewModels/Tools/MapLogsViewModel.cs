using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GMap.NET;
using GMap.NET.MapProviders;
using MissionPlanner.App.Core.Maps;
using MissionPlanner.Comms;
using MissionPlanner.Utilities;
using SkiaSharp;

namespace MissionPlanner.App.Core.ViewModels.Tools;

/// <summary>
/// Mission Planner's LogMap: a JPG of each log's flight path over map imagery, written next to the
/// log as &lt;log&gt;.jpg with the log's modification time (log lists show it as a thumbnail).
/// </summary>
public sealed class LogMapRenderer
{
    private static readonly SKColor[] Colours =
    [
        SKColors.Red, SKColors.Orange, SKColors.Yellow, SKColors.Green, SKColors.Blue, SKColors.Indigo,
        SKColors.Violet, SKColors.Pink,
    ];

    /// <summary>Encoded tile layers (bottom first) for a provider tile; the map's tile access by default.</summary>
    private readonly Func<GMapProvider, long, long, int, IReadOnlyList<byte[]>> _tiles;

    public LogMapRenderer(GMapProvider provider, Func<GMapProvider, long, long, int, IReadOnlyList<byte[]>>? tiles = null)
    {
        Provider = provider;
        _tiles = tiles ?? ((p, x, y, z) => MapTiles.GetTileLayersAsync(p, x, y, z).GetAwaiter().GetResult().Layers);
    }

    public GMapProvider Provider { get; }

    public enum Outcome
    {
        Exists,
        Mapped,
        NoGps,
        Failed,
    }

    /// <summary>The logs (.tlog, .bin, .log) under a folder and its subfolders; unreadable folders are skipped.</summary>
    public static List<string> FindLogs(string folder)
    {
        var files = new List<string>();
        foreach (var pattern in new[] { "*.tlog", "*.bin", "*.log" })
            files.AddRange(GetFiles(folder, pattern));
        return files;

        static IEnumerable<string> GetFiles(string path, string pattern)
        {
            var list = new List<string>();
            string[] dirs = [];
            try
            {
                list.AddRange(Directory.GetFiles(path, pattern, SearchOption.TopDirectoryOnly)
                    // *.log also matches *.logx on Windows
                    .Where(f => f.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)));
                dirs = Directory.GetDirectories(path);
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }

            foreach (var d in dirs)
                list.AddRange(GetFiles(d, pattern));
            return list;
        }
    }

    /// <summary>The positions of each vehicle in a log, and whether it came from SITL.</summary>
    public static (List<List<PointLatLng>> Tracks, bool Sitl) ReadTracks(string logfile)
    {
        var tracks = new Dictionary<ulong, List<PointLatLng>>();
        var sitl = false;
        if (logfile.EndsWith(".tlog", StringComparison.OrdinalIgnoreCase))
        {
            var cf = new CommsFile();
            cf.Open(logfile);
            try
            {
                using var cs = new CommsStream(cf, cf.BytesToRead);
                var parse = new MAVLink.MavlinkParse(true);
                while (cs.Position < cs.Length)
                {
                    try
                    {
                        var packet = parse.ReadPacket(cs);
                        if (packet == null || packet.Length < 5)
                            continue;
                        if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.SIM_STATE || packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.SIMSTATE)
                            sitl = true;
                        if (packet.msgid != (uint)MAVLink.MAVLINK_MSG_ID.GLOBAL_POSITION_INT)
                            continue;
                        var loc = packet.ToStructure<MAVLink.mavlink_global_position_int_t>();
                        if (loc.lat == 0 || loc.lon == 0)
                            continue;
                        var id = (ulong)packet.sysid * 256 + packet.compid;
                        if (!tracks.TryGetValue(id, out var list))
                            tracks[id] = list = new List<PointLatLng>();
                        list.Add(new PointLatLng(loc.lat / 1e7, loc.lon / 1e7));
                    }
                    catch (Exception) when (cs.Position < cs.Length)
                    {
                        // a damaged record: carry on with the next
                    }
                }
            }
            finally
            {
                cf.Close();
            }
        }
        else
        {
            using var buffer = new DFLogBuffer(logfile);
            var list = tracks[0] = new List<PointLatLng>();
            if (buffer.dflog.logformat.ContainsKey("GPS"))
            {
                foreach (var item in buffer.GetEnumeratorType("GPS"))
                {
                    if (!item.msgtype.StartsWith("GPS", StringComparison.Ordinal))
                        continue;
                    var status = double.Parse(item.items[buffer.dflog.FindMessageOffset(item.msgtype, "Status")], CultureInfo.InvariantCulture);
                    var lat = double.Parse(item.items[buffer.dflog.FindMessageOffset(item.msgtype, "Lat")], CultureInfo.InvariantCulture);
                    var lng = double.Parse(item.items[buffer.dflog.FindMessageOffset(item.msgtype, "Lng")], CultureInfo.InvariantCulture);
                    if (lat == 0 || lng == 0 || status < 3)
                        continue;
                    list.Add(new PointLatLng(lat, lng));
                }
            }
        }

        return (tracks.Values.ToList(), sitl);
    }

    /// <summary>Writes &lt;log&gt;.jpg unless it is already there.</summary>
    public Outcome ProcessFile(string logfile)
    {
        var jpg = logfile + ".jpg";
        if (File.Exists(jpg))
            return Outcome.Exists;
        try
        {
            var (tracks, sitl) = ReadTracks(logfile);
            var all = tracks.SelectMany(t => t).ToList();
            Outcome outcome;
            if (tracks.Count > 0 && tracks[0].Count > 10)
            {
                // a bit of margin around the flight
                var minx = all.Min(p => p.Lng);
                var maxx = all.Max(p => p.Lng);
                var miny = all.Min(p => p.Lat);
                var maxy = all.Max(p => p.Lat);
                var area = RectLatLng.FromLTRB(minx - 0.001, maxy + 0.001, maxx + 0.001, miny - 0.001);
                using var map = GetMap(area);
                using (var canvas = new SKCanvas(map))
                {
                    if (sitl)
                        DrawText(canvas, "SITL");

                    var a = 0;
                    foreach (var track in tracks)
                    {
                        using var pen = new SKPaint
                        {
                            Color = Colours[a % (Colours.Length - 1)],
                            StrokeWidth = 1,
                            IsAntialias = true,
                            Style = SKPaintStyle.Stroke,
                        };
                        SKPoint? last = null;
                        foreach (var p in track)
                        {
                            var pt = GetPixel(area, p, map.Width, map.Height);
                            if (last is { } l)
                                canvas.DrawLine(l.X, l.Y, pt.X, pt.Y, pen);
                            last = pt;
                        }

                        a++;
                    }
                }

                SaveJpeg(map, jpg);
                outcome = Outcome.Mapped;
            }
            else
            {
                DoTextMap(jpg, "No gps data");
                outcome = Outcome.NoGps;
            }

            File.SetLastWriteTime(jpg, new FileInfo(logfile).LastWriteTime);
            return outcome;
        }
        catch (Exception ex)
        {
            if (ex.ToString().Contains("Mavlink 0.9"))
            {
                DoTextMap(jpg, "Old log\nMavlink 0.9");
                return Outcome.NoGps;
            }

            return Outcome.Failed;
        }
    }

    private static void DoTextMap(string jpgname, string text)
    {
        using var map = new SKBitmap(100, 100);
        using (var canvas = new SKCanvas(map))
        {
            canvas.Clear(SKColors.Black);
            DrawText(canvas, text);
        }

        SaveJpeg(map, jpgname);
    }

    /// <summary>Red text at the top left (skipped if this SkiaSharp has no simple text API).</summary>
    private static void DrawText(SKCanvas canvas, string text)
    {
        try
        {
            DrawTextLines(canvas, text);
        }
        catch (MissingMethodException)
        {
        }
    }

    private static void DrawTextLines(SKCanvas canvas, string text)
    {
        using var paint = new SKPaint { Color = SKColors.Red, TextSize = 12, IsAntialias = true };
        var y = 12f;
        foreach (var line in text.Split('\n'))
        {
            canvas.DrawText(line, 0, y, paint);
            y += 14;
        }
    }

    private static void SaveJpeg(SKBitmap bitmap, string file)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        using var fs = File.Create(file);
        data.SaveTo(fs);
    }

    private static SKPoint GetPixel(RectLatLng area, PointLatLng loc, int width, int height)
    {
        var x = (loc.Lng - area.Left) * width / (area.Right - area.Left);
        var y = (loc.Lat - area.Top) * height / (area.Bottom - area.Top);
        return new SKPoint((float)x, (float)y);
    }

    /// <summary>The imagery under <paramref name="area"/>, at the highest zoom up to 16 that keeps it within 2000 px.</summary>
    private SKBitmap GetMap(RectLatLng area)
    {
        var prj = Provider.Projection;
        var zoom = 16;
        GPoint topLeftPx, rightBottomPx;
        while (true)
        {
            topLeftPx = prj.FromLatLngToPixel(area.LocationTopLeft, zoom);
            rightBottomPx = prj.FromLatLngToPixel(area.Bottom, area.Right, zoom);
            if (rightBottomPx.X - topLeftPx.X <= 2000 || zoom <= 1)
                break;
            zoom--;
        }

        var pxDelta = new GPoint(rightBottomPx.X - topLeftPx.X, rightBottomPx.Y - topLeftPx.Y);
        const int padding = 10;
        var bmp = new SKBitmap((int)pxDelta.X + padding * 2, (int)pxDelta.Y + padding * 2);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Black);
        var tileSize = prj.TileSize.Width;
        foreach (var p in prj.GetAreaTileList(area, zoom, 0))
        {
            IReadOnlyList<byte[]> layers;
            try
            {
                layers = _tiles(Provider, p.X, p.Y, zoom);
            }
            catch (Exception)
            {
                continue;
            }

            var x = p.X * tileSize - topLeftPx.X + padding;
            var y = p.Y * tileSize - topLeftPx.Y + padding;
            foreach (var layer in layers)
            {
                using var tile = SKBitmap.Decode(layer);
                if (tile != null)
                    canvas.DrawBitmap(tile, new SKRect(x, y, x + tileSize, y + tileSize));
            }
        }

        return bmp;
    }
}

/// <summary>TOOLS > Map logs (Ctrl+F "map logs"): a map JPG for every log in a folder.</summary>
public sealed partial class MapLogsViewModel : ToolPageViewModel
{
    public MapLogsViewModel(AppServices services)
        : base("Map logs",
            "Draws the flight path of every log (.tlog, .bin, .log) in a folder and its subfolders over map imagery, saved as <log>.jpg next to each log. Logs that already have a picture are skipped.",
            services)
    {
        Providers = MapTiles.CommonProviders.Except(MapTiles.LocalProviders).ToList();
        _provider = Providers.FirstOrDefault(p => p == GMapProviders.GoogleSatelliteMap) ?? Providers[0];
    }

    public override bool NeedsVehicle => false;

    public IReadOnlyList<GMapProvider> Providers { get; }

    [ObservableProperty]
    private GMapProvider _provider;

    [ObservableProperty]
    private string _log = "";

    [ObservableProperty]
    private double _progress;

    [RelayCommand]
    private async Task MapFolderAsync()
    {
        var folder = await Services.Dialogs.PickFolderAsync("Folder of logs to map");
        if (folder == null)
            return;
        await MapFolderNowAsync(folder, null);
    }

    internal async Task MapFolderNowAsync(string folder, LogMapRenderer? renderer)
    {
        if (!Directory.Exists(folder) || IsBusy)
            return;
        renderer ??= new LogMapRenderer(Provider);
        IsBusy = true;
        Progress = 0;
        Log = "";
        Status = "Looking for logs…";
        StatusIsError = false;
        try
        {
            var logs = await Task.Run(() => LogMapRenderer.FindLogs(folder));
            var done = 0;
            var counts = new int[4];
            await Task.Run(() => Parallel.ForEach(logs, new ParallelOptions { MaxDegreeOfParallelism = 4 }, logfile =>
            {
                var outcome = renderer.ProcessFile(logfile);
                Interlocked.Increment(ref counts[(int)outcome]);
                var n = Interlocked.Increment(ref done);
                Services.Dispatcher.Post(() =>
                {
                    Progress = logs.Count == 0 ? 1 : n / (double)logs.Count;
                    Status = $"{n} of {logs.Count}: {Path.GetFileName(logfile)}";
                    if (outcome != LogMapRenderer.Outcome.Exists)
                        Log = $"{outcome}: {logfile}{Environment.NewLine}{Log}";
                });
            }));
            Progress = 1;
            Report($"{logs.Count} logs: {counts[(int)LogMapRenderer.Outcome.Mapped]} mapped, {counts[(int)LogMapRenderer.Outcome.NoGps]} without GPS, " +
                   $"{counts[(int)LogMapRenderer.Outcome.Exists]} already had a picture, {counts[(int)LogMapRenderer.Outcome.Failed]} could not be read.");
        }
        catch (Exception ex)
        {
            Report("Could not map the logs: " + ex.Message, true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task MapLogFolderAsync() => await MapFolderNowAsync(Settings.Instance.LogDir, null);

    public string LogFolder => Settings.Instance.LogDir;
}
