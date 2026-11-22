using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GMap.NET;
using GMap.NET.MapProviders;
using MissionPlanner.App.Core.Maps;
using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.FlightPlan;

/// <summary>One map tile: x, y at a zoom level.</summary>
public readonly record struct TileId(int Zoom, long X, long Y);

/// <summary>The tiles Mission Planner's Prefetch and Prefetch WP Path download into the cache.</summary>
public static class TilePrefetch
{
    /// <summary>More than this is refused: it would take hours and the tile servers block such clients.</summary>
    public const int MaxTiles = 200_000;

    /// <summary>Every tile covering the box between two corners, at each zoom from min to max.</summary>
    public static List<TileId> ForArea(PureProjection proj, PointLatLng a, PointLatLng b, int minZoom, int maxZoom)
    {
        var set = new HashSet<TileId>();
        var top = Math.Max(a.Lat, b.Lat);
        var bottom = Math.Min(a.Lat, b.Lat);
        var left = Math.Min(a.Lng, b.Lng);
        var right = Math.Max(a.Lng, b.Lng);
        for (var z = minZoom; z <= maxZoom; z++)
        {
            AddBox(proj, z, top, left, bottom, right, set);
            if (set.Count > MaxTiles)
                break;
        }

        return Sorted(set);
    }

    /// <summary>
    /// Tiles within <paramref name="bufferMetres"/> of a path (the mission route), at each zoom up to
    /// max: each leg is walked in steps shorter than the buffer and the box round each step taken.
    /// </summary>
    public static List<TileId> ForPath(PureProjection proj, IReadOnlyList<PointLatLng> path, double bufferMetres, int minZoom, int maxZoom)
    {
        var set = new HashSet<TileId>();
        if (path.Count == 0)
            return [];
        var samples = new List<PointLatLng> { path[0] };
        for (var i = 1; i < path.Count; i++)
        {
            var (p, q) = (path[i - 1], path[i]);
            var metres = Distance(p, q);
            var steps = Math.Max(1, (int)Math.Ceiling(metres / Math.Max(1, bufferMetres)));
            for (var s = 1; s <= steps; s++)
            {
                var f = (double)s / steps;
                samples.Add(new PointLatLng(p.Lat + (q.Lat - p.Lat) * f, p.Lng + (q.Lng - p.Lng) * f));
            }
        }

        for (var z = minZoom; z <= maxZoom; z++)
        {
            foreach (var s in samples)
            {
                var dLat = bufferMetres / 111_320.0;
                var dLng = bufferMetres / (111_320.0 * Math.Max(0.01, Math.Cos(s.Lat * Math.PI / 180)));
                AddBox(proj, z, s.Lat + dLat, s.Lng - dLng, s.Lat - dLat, s.Lng + dLng, set);
            }

            if (set.Count > MaxTiles)
                break;
        }

        return Sorted(set);
    }

    private static void AddBox(PureProjection proj, int z, double top, double left, double bottom, double right, HashSet<TileId> set)
    {
        top = Math.Clamp(top, -85.05, 85.05);
        bottom = Math.Clamp(bottom, -85.05, 85.05);
        var tl = proj.FromPixelToTileXY(proj.FromLatLngToPixel(top, left, z));
        var br = proj.FromPixelToTileXY(proj.FromLatLngToPixel(bottom, right, z));
        var max = proj.GetTileMatrixMaxXY(z);
        for (var x = Math.Max(0, tl.X); x <= Math.Min(max.Width, br.X); x++)
        {
            for (var y = Math.Max(0, tl.Y); y <= Math.Min(max.Height, br.Y); y++)
            {
                set.Add(new TileId(z, x, y));
                if (set.Count > MaxTiles)
                    return;
            }
        }
    }

    private static List<TileId> Sorted(HashSet<TileId> set) =>
        set.OrderBy(t => t.Zoom).ThenBy(t => t.Y).ThenBy(t => t.X).ToList();

    private static double Distance(PointLatLng a, PointLatLng b)
    {
        var dLat = (b.Lat - a.Lat) * 111_320.0;
        var dLng = (b.Lng - a.Lng) * 111_320.0 * Math.Cos((a.Lat + b.Lat) / 2 * Math.PI / 180);
        return Math.Sqrt(dLat * dLat + dLng * dLng);
    }
}

/// <summary>The prefetch progress window: downloads the tiles into the map cache, a few at a time, until done or cancelled.</summary>
public sealed partial class TilePrefetchViewModel : PageViewModel, IDialogPage
{
    private const int Workers = 4;

    private readonly IReadOnlyList<TileId> _tiles;
    private readonly Func<TileId, CancellationToken, Task<bool>> _fetch;
    private readonly IUiDispatcher _ui;
    private readonly CancellationTokenSource _cancel = new();
    private int _done;
    private int _failed;

    public TilePrefetchViewModel(GMapProvider provider, IReadOnlyList<TileId> tiles, IUiDispatcher ui)
        : this(tiles, ui, async (t, c) => await MapTiles.GetTileAsync(provider, t.X, t.Y, t.Zoom, c).ConfigureAwait(false) != null)
    {
        ProviderName = provider.Name;
    }

    /// <summary>For tests: any fetch function.</summary>
    public TilePrefetchViewModel(IReadOnlyList<TileId> tiles, IUiDispatcher ui, Func<TileId, CancellationToken, Task<bool>> fetch)
        : base("Prefetch map tiles")
    {
        _tiles = tiles;
        _ui = ui;
        _fetch = fetch;
        Total = tiles.Count;
        ZoomText = tiles.Count == 0 ? "" : $"zoom {tiles.Min(t => t.Zoom)} to {tiles.Max(t => t.Zoom)}";
    }

    public event EventHandler<bool>? CloseRequested;

    public string ProviderName { get; } = "";

    public int Total { get; }

    public string ZoomText { get; }

    [ObservableProperty]
    private int _completed;

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonText))]
    private bool _isRunning;

    public string ButtonText => IsRunning ? "Cancel" : "Close";

    /// <summary>Downloads everything; finishes when done or cancelled.</summary>
    public async Task RunAsync()
    {
        IsRunning = true;
        var next = -1;
        var token = _cancel.Token;
        var lastReport = DateTime.MinValue;

        async Task Worker()
        {
            while (!token.IsCancellationRequested)
            {
                var i = Interlocked.Increment(ref next);
                if (i >= _tiles.Count)
                    return;
                bool ok;
                try
                {
                    ok = await _fetch(_tiles[i], token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    ok = false;
                }

                Interlocked.Increment(ref _done);
                if (!ok)
                    Interlocked.Increment(ref _failed);
                if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(200))
                {
                    lastReport = DateTime.UtcNow;
                    _ui.Post(Report);
                }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, Workers).Select(_ => Task.Run(Worker))).ConfigureAwait(false);
        await _ui.InvokeAsync(() =>
        {
            Report();
            IsRunning = false;
            ProgressText = (token.IsCancellationRequested ? "Cancelled: " : "Done: ") + ProgressText;
        }).ConfigureAwait(false);
    }

    private void Report()
    {
        Completed = _done;
        ProgressText = $"{_done} of {Total} tiles" + (_failed > 0 ? $", {_failed} failed" : "");
    }

    [RelayCommand]
    private void Button()
    {
        if (IsRunning)
            _cancel.Cancel();
        else
            CloseRequested?.Invoke(this, true);
    }

    /// <summary>Closing the window stops the downloads.</summary>
    public void Cancel() => _cancel.Cancel();

    public override void OnShown()
    {
        base.OnShown();
        if (!IsRunning && Completed == 0)
            _ = RunAsync();
    }

    public override void OnHidden()
    {
        base.OnHidden();
        Cancel();
    }
}
