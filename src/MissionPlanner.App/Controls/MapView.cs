using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using GMap.NET;
using GMap.NET.MapProviders;
using MissionPlanner.App.Core.Maps;

namespace MissionPlanner.App.Controls;

/// <summary>Screen geometry of the current map view, handed to layers while they draw.</summary>
public sealed class MapViewport
{
    internal MapViewport(PureProjection projection, int zoom, double originX, double originY, Size size)
    {
        Projection = projection;
        Zoom = zoom;
        OriginX = originX;
        OriginY = originY;
        Size = size;
    }

    public PureProjection Projection { get; }

    public int Zoom { get; }

    public double OriginX { get; }

    public double OriginY { get; }

    public Size Size { get; }

    public Point ToScreen(double lat, double lng)
    {
        var p = Projection.FromLatLngToPixel(lat, lng, Zoom);
        return new Point(p.X - OriginX, p.Y - OriginY);
    }

    public Point ToScreen(PointLatLng p) => ToScreen(p.Lat, p.Lng);

    public PointLatLng ToLatLng(Point screen) =>
        Projection.FromPixelToLatLng((long)Math.Round(screen.X + OriginX), (long)Math.Round(screen.Y + OriginY), Zoom);

    /// <summary>Metres per screen pixel at a latitude.</summary>
    public double MetersPerPixel(double lat) => Projection.GetGroundResolution(Zoom, lat);
}

/// <summary>Something drawn over the tiles. Layers run on the UI thread only.</summary>
public abstract class MapLayer
{
    public bool IsVisible { get; set; } = true;

    public abstract void Render(DrawingContext ctx, MapViewport vp);

    /// <summary>The draggable item under a point, if any.</summary>
    public virtual object? HitTest(Point screen, MapViewport vp) => null;
}

public sealed class MapPointerEventArgs(PointLatLng position, Point screen, object? hit, KeyModifiers modifiers) : EventArgs
{
    public PointLatLng Position { get; } = position;

    public Point Screen { get; } = screen;

    /// <summary>The layer item under the pointer, if any.</summary>
    public object? Hit { get; } = hit;

    public KeyModifiers Modifiers { get; } = modifiers;
}

public sealed class MapDragEventArgs(object item, PointLatLng position) : EventArgs
{
    public object Item { get; } = item;

    public PointLatLng Position { get; } = position;
}

/// <summary>
/// Slippy map over GMap.NET.Core providers and Mission Planner's tile cache, with drawable layers.
/// Tiles download and decode off the UI thread; everything else (layers included) is drawn and
/// changed on the UI thread only, so overlays can never race the renderer.
/// </summary>
public sealed class MapView : Control
{
    public static readonly StyledProperty<GMapProvider?> ProviderProperty =
        AvaloniaProperty.Register<MapView, GMapProvider?>(nameof(Provider));

    public static readonly StyledProperty<double> CenterLatProperty =
        AvaloniaProperty.Register<MapView, double>(nameof(CenterLat), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> CenterLngProperty =
        AvaloniaProperty.Register<MapView, double>(nameof(CenterLng), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<int> ZoomProperty =
        AvaloniaProperty.Register<MapView, int>(nameof(Zoom), 15, defaultBindingMode: BindingMode.TwoWay);

    private const int MaxCachedTiles = 500;
    private const double ClickSlop = 4;

    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#1b1f24"));

    /// <summary>Waits before re-requesting an incomplete tile; after the last one it stays as it is.</summary>
    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60)];

    private readonly Dictionary<TileKey, TileEntry> _tiles = new();
    private CancellationTokenSource _loads = new();
    private long _frame;

    private Point? _pressAt;
    private Point _lastPress = new(double.NaN, double.NaN);
    private bool _panning;
    private object? _dragItem;
    private GPoint _dragCenterPx;
    private MapViewport? _lastViewport;

    // two-finger pinch on a touch screen: the fingers down, and the spread and zoom when it began
    private readonly Dictionary<long, Point> _touches = new();
    private bool _pinching;
    private double _pinchStartSpread;
    private int _pinchStartZoom;

    static MapView()
    {
        AffectsRender<MapView>(ProviderProperty, CenterLatProperty, CenterLngProperty, ZoomProperty);
        ClipToBoundsProperty.OverrideDefaultValue<MapView>(true);
        FocusableProperty.OverrideDefaultValue<MapView>(true);
    }

    public MapView()
    {
        Layers.CollectionChanged += (_, _) => InvalidateVisual();
    }

    public GMapProvider? Provider
    {
        get => GetValue(ProviderProperty);
        set => SetValue(ProviderProperty, value);
    }

    public double CenterLat
    {
        get => GetValue(CenterLatProperty);
        set => SetValue(CenterLatProperty, value);
    }

    public double CenterLng
    {
        get => GetValue(CenterLngProperty);
        set => SetValue(CenterLngProperty, value);
    }

    public int Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    public AvaloniaList<MapLayer> Layers { get; } = new();

    /// <summary>Left click without dragging.</summary>
    public event EventHandler<MapPointerEventArgs>? MapClick;

    public event EventHandler<MapPointerEventArgs>? MapDoubleClick;

    /// <summary>Right click; the view decides what menu to show.</summary>
    public event EventHandler<MapPointerEventArgs>? MapContextRequested;

    /// <summary>Pointer position over the map, for coordinate readouts.</summary>
    public event EventHandler<PointLatLng>? PointerPositionChanged;

    public event EventHandler<MapDragEventArgs>? ItemDragged;

    public event EventHandler<MapDragEventArgs>? ItemDragCompleted;

    /// <summary>Raised when the user pans by hand, so auto-follow can step aside.</summary>
    public event EventHandler? UserPanned;

    public MapViewport? Viewport => _lastViewport;

    public void CenterOn(double lat, double lng)
    {
        SetCurrentValue(CenterLatProperty, Math.Clamp(lat, -85.05, 85.05));
        SetCurrentValue(CenterLngProperty, lng);
    }

    /// <summary>Centres and zooms so all points fit.</summary>
    public void ZoomToFit(IReadOnlyList<PointLatLng> points, int maxZoom = 18)
    {
        var provider = Provider;
        if (provider == null || points.Count == 0 || Bounds.Width < 10)
            return;
        if (points.Count == 1)
        {
            CenterOn(points[0].Lat, points[0].Lng);
            return;
        }

        var minLat = points.Min(p => p.Lat);
        var maxLat = points.Max(p => p.Lat);
        var minLng = points.Min(p => p.Lng);
        var maxLng = points.Max(p => p.Lng);
        var zoom = Math.Min(maxZoom, provider.MaxZoom ?? 20);
        for (; zoom > 2; zoom--)
        {
            var a = provider.Projection.FromLatLngToPixel(maxLat, minLng, zoom);
            var b = provider.Projection.FromLatLngToPixel(minLat, maxLng, zoom);
            if (b.X - a.X < Bounds.Width * 0.85 && b.Y - a.Y < Bounds.Height * 0.85)
                break;
        }

        SetCurrentValue(ZoomProperty, zoom);
        CenterOn((minLat + maxLat) / 2, (minLng + maxLng) / 2);
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        context.FillRectangle(Background, new Rect(size));

        var provider = Provider;
        if (provider == null || size.Width <= 0 || size.Height <= 0)
            return;

        var proj = provider.Projection;
        var zoom = Zoom;
        var tw = (double)proj.TileSize.Width;
        var th = (double)proj.TileSize.Height;
        var maxTile = proj.GetTileMatrixMaxXY(zoom);

        // whole-pixel origin: fractional tile positions leave hairline seams between tiles
        var center = proj.FromLatLngToPixel(CenterLat, CenterLng, zoom);
        var originX = Math.Floor(center.X - size.Width / 2);
        var originY = Math.Floor(center.Y - size.Height / 2);
        var vp = new MapViewport(proj, zoom, originX, originY, size);
        _lastViewport = vp;

        var minTx = (long)Math.Floor(originX / tw);
        var maxTx = (long)Math.Floor((originX + size.Width) / tw);
        var minTy = Math.Max(0, (long)Math.Floor(originY / th));
        var maxTy = Math.Min(maxTile.Height, (long)Math.Floor((originY + size.Height) / th));

        _frame++;
        for (var ty = minTy; ty <= maxTy; ty++)
        {
            for (var tx = minTx; tx <= maxTx; tx++)
            {
                // wrap around the antimeridian
                var wx = Mod(tx, maxTile.Width + 1);
                var entry = GetOrRequest(provider, new TileKey(provider.DbId, zoom, wx, ty));
                // a half-pixel overlap hides seams left by display scaling
                var dest = new Rect(tx * tw - originX, ty * th - originY, tw + 0.5, th + 0.5);
                foreach (var layer in entry.Layers)
                    context.DrawImage(layer, dest);
            }
        }

        foreach (var layer in Layers)
        {
            if (layer.IsVisible)
                layer.Render(context, vp);
        }

        Evict();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ProviderProperty && Provider is { } selected)
        {
            // start the provider's version/metadata lookup before the first tiles are requested
            Task.Run(() => MapTiles.PrepareProvider(selected));
        }

        if (change.Property == ZoomProperty || change.Property == ProviderProperty)
        {
            // downloads queued for the old view are no longer wanted
            CancelPendingLoads();
            var clamped = ClampZoom(Zoom);
            if (clamped != Zoom)
                SetCurrentValue(ZoomProperty, clamped);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var provider = Provider;
        var vp = _lastViewport;
        if (provider == null || vp == null)
            return;

        var point = e.GetCurrentPoint(this);
        var pos = point.Position;

        if (e.Pointer.Type == PointerType.Touch)
        {
            _touches[e.Pointer.Id] = pos;
            if (_touches.Count >= 2)
            {
                // a second finger: stop panning or dragging and zoom with the spread instead
                StartPinch();
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
        }

        if (point.Properties.IsRightButtonPressed)
        {
            MapContextRequested?.Invoke(this, new MapPointerEventArgs(vp.ToLatLng(pos), pos, HitTestLayers(pos, vp), e.KeyModifiers));
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
            return;

        // a quick second click elsewhere is a new click, not a double-click
        var nearLast = Math.Abs(pos.X - _lastPress.X) + Math.Abs(pos.Y - _lastPress.Y) <= ClickSlop * 2;
        _lastPress = pos;
        if (e.ClickCount == 2 && nearLast)
        {
            MapDoubleClick?.Invoke(this, new MapPointerEventArgs(vp.ToLatLng(pos), pos, HitTestLayers(pos, vp), e.KeyModifiers));
            e.Handled = true;
            return;
        }

        _pressAt = pos;
        _panning = false;
        _dragItem = HitTestLayers(pos, vp);
        _dragCenterPx = provider.Projection.FromLatLngToPixel(CenterLat, CenterLng, Zoom);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var provider = Provider;
        var vp = _lastViewport;
        if (provider == null || vp == null)
            return;

        var pos = e.GetPosition(this);
        if (e.Pointer.Type == PointerType.Touch && _touches.ContainsKey(e.Pointer.Id))
        {
            _touches[e.Pointer.Id] = pos;
            if (_pinching)
            {
                Pinch();
                e.Handled = true;
                return;
            }
        }

        PointerPositionChanged?.Invoke(this, vp.ToLatLng(pos));

        if (_pressAt is not { } start)
            return;

        var delta = pos - start;
        if (!_panning && Math.Abs(delta.X) + Math.Abs(delta.Y) < ClickSlop)
            return;
        _panning = true;

        if (_dragItem != null)
        {
            ItemDragged?.Invoke(this, new MapDragEventArgs(_dragItem, vp.ToLatLng(pos)));
            InvalidateVisual();
        }
        else
        {
            var px = new GPoint(_dragCenterPx.X - (long)Math.Round(delta.X), _dragCenterPx.Y - (long)Math.Round(delta.Y));
            SetCenter(provider.Projection.FromPixelToLatLng(px, Zoom));
            UserPanned?.Invoke(this, EventArgs.Empty);
        }

        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.Pointer.Type == PointerType.Touch && _touches.Remove(e.Pointer.Id) && _pinching)
        {
            // the pinch ends with its fingers; a finger left down neither pans nor clicks
            if (_touches.Count < 2)
                _pinching = false;
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        if (_pressAt == null)
            return;

        var vp = _lastViewport;
        var pos = e.GetPosition(this);
        if (vp != null)
        {
            if (!_panning)
                MapClick?.Invoke(this, new MapPointerEventArgs(vp.ToLatLng(pos), pos, _dragItem, e.KeyModifiers));
            else if (_dragItem != null)
                ItemDragCompleted?.Invoke(this, new MapDragEventArgs(_dragItem, vp.ToLatLng(pos)));
        }

        _pressAt = null;
        _dragItem = null;
        _panning = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        // a touch cancelled by the system (a gesture, the app going to the background)
        if (_touches.Remove(e.Pointer.Id) && _touches.Count < 2)
            _pinching = false;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var provider = Provider;
        if (provider == null || e.Delta.Y == 0)
            return;

        if (ZoomAbout(e.GetPosition(this), Zoom + Math.Sign(e.Delta.Y)))
            e.Handled = true;
    }

    /// <summary>Changes the zoom keeping the map point under <paramref name="at"/> where it is.</summary>
    private bool ZoomAbout(Point at, int zoom)
    {
        var provider = Provider;
        if (provider == null)
            return false;
        var oldZoom = Zoom;
        var newZoom = ClampZoom(zoom);
        if (newZoom == oldZoom)
            return false;

        // keep the point under the cursor fixed while zooming
        var proj = provider.Projection;
        var offset = at - new Point(Bounds.Width / 2, Bounds.Height / 2);
        var centerPx = proj.FromLatLngToPixel(CenterLat, CenterLng, oldZoom);
        var cursor = proj.FromPixelToLatLng(new GPoint(centerPx.X + (long)offset.X, centerPx.Y + (long)offset.Y), oldZoom);
        var cursorPx = proj.FromLatLngToPixel(cursor, newZoom);
        var newCenter = proj.FromPixelToLatLng(new GPoint(cursorPx.X - (long)offset.X, cursorPx.Y - (long)offset.Y), newZoom);

        SetCurrentValue(ZoomProperty, newZoom);
        SetCenter(newCenter);
        return true;
    }

    private (Point Mid, double Spread) TouchSpread()
    {
        var pts = _touches.Values.Take(2).ToArray();
        var mid = new Point((pts[0].X + pts[1].X) / 2, (pts[0].Y + pts[1].Y) / 2);
        var d = pts[0] - pts[1];
        return (mid, Math.Sqrt(d.X * d.X + d.Y * d.Y));
    }

    private void StartPinch()
    {
        _pinching = true;
        _pressAt = null;
        _dragItem = null;
        _panning = false;
        _pinchStartSpread = Math.Max(1, TouchSpread().Spread);
        _pinchStartZoom = Zoom;
    }

    /// <summary>One zoom level for each doubling (or halving) of the distance between the fingers.</summary>
    private void Pinch()
    {
        if (_touches.Count < 2)
            return;
        var (mid, spread) = TouchSpread();
        var target = _pinchStartZoom + (int)Math.Round(Math.Log2(Math.Max(1, spread) / _pinchStartSpread));
        if (target != Zoom)
        {
            ZoomAbout(mid, target);
            UserPanned?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        CancelPendingLoads();
        foreach (var entry in _tiles.Values)
            entry.DisposeLayers();
        _tiles.Clear();
    }

    private object? HitTestLayers(Point pos, MapViewport vp)
    {
        // topmost layer first
        for (var i = Layers.Count - 1; i >= 0; i--)
        {
            if (Layers[i].IsVisible && Layers[i].HitTest(pos, vp) is { } hit)
                return hit;
        }

        return null;
    }

    private void SetCenter(PointLatLng p)
    {
        SetCurrentValue(CenterLatProperty, Math.Clamp(p.Lat, -85.05, 85.05));
        SetCurrentValue(CenterLngProperty, p.Lng);
    }

    private int ClampZoom(int zoom)
    {
        var provider = Provider;
        var min = Math.Max(2, provider?.MinZoom ?? 2);
        var max = provider?.MaxZoom ?? 20;
        return Math.Clamp(zoom, min, Math.Max(min, max));
    }

    private TileEntry GetOrRequest(GMapProvider provider, TileKey key)
    {
        if (!_tiles.TryGetValue(key, out var entry))
        {
            entry = new TileEntry();
            _tiles[key] = entry;
            StartLoad(provider, key, entry);
        }
        else if (!entry.Loading && !entry.Complete && entry.Attempts <= RetryDelays.Length &&
                 DateTime.UtcNow >= entry.RetryAt)
        {
            StartLoad(provider, key, entry);
        }

        entry.LastUsed = _frame;
        return entry;
    }

    private void StartLoad(GMapProvider provider, TileKey key, TileEntry entry)
    {
        entry.Loading = true;
        entry.Attempts++;
        _ = LoadAsync(provider, key, entry, _loads.Token);
    }

    private async Task LoadAsync(GMapProvider provider, TileKey key, TileEntry entry, CancellationToken cancel)
    {
        var bitmaps = new List<Bitmap>();
        var complete = false;
        var cancelled = false;
        try
        {
            var tile = await MapTiles.GetTileLayersAsync(provider, key.X, key.Y, key.Zoom, cancel).ConfigureAwait(false);
            foreach (var bytes in tile.Layers)
            {
                using var stream = new MemoryStream(bytes, writable: false);
                bitmaps.Add(new Bitmap(stream));
            }

            complete = tile.Complete;
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            // a bad tile must not take the map down; it is retried later
            System.Diagnostics.Trace.WriteLine($"tile {key} failed: {ex.Message}");
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!_tiles.TryGetValue(key, out var current) || !ReferenceEquals(current, entry))
            {
                bitmaps.ForEach(b => b.Dispose());
                return;
            }

            if (cancelled)
            {
                // forget it so it is requested again if it comes back into view
                bitmaps.ForEach(b => b.Dispose());
                entry.DisposeLayers();
                _tiles.Remove(key);
                return;
            }

            // keep what an earlier attempt drew if this one got fewer layers
            if (bitmaps.Count >= entry.Layers.Length)
            {
                entry.DisposeLayers();
                entry.Layers = bitmaps.ToArray();
            }
            else
            {
                bitmaps.ForEach(b => b.Dispose());
            }

            entry.Loading = false;
            entry.Complete = complete;
            if (!complete && entry.Attempts <= RetryDelays.Length)
            {
                var delay = RetryDelays[entry.Attempts - 1];
                entry.RetryAt = DateTime.UtcNow + delay;
                // a render is what re-requests visible tiles
                DispatcherTimer.RunOnce(InvalidateVisual, delay);
            }

            InvalidateVisual();
        });
    }

    private void CancelPendingLoads()
    {
        _loads.Cancel();
        _loads.Dispose();
        _loads = new CancellationTokenSource();
    }

    private void Evict()
    {
        if (_tiles.Count <= MaxCachedTiles)
            return;

        var victims = _tiles
            .Where(kv => kv.Value.LastUsed != _frame && !kv.Value.Loading)
            .OrderBy(kv => kv.Value.LastUsed)
            .Take(_tiles.Count - MaxCachedTiles * 3 / 4)
            .ToList();

        foreach (var (key, entry) in victims)
        {
            entry.DisposeLayers();
            _tiles.Remove(key);
        }
    }

    private static long Mod(long a, long n) => ((a % n) + n) % n;

    private readonly record struct TileKey(int ProviderId, int Zoom, long X, long Y);

    private sealed class TileEntry
    {
        /// <summary>Bottom to top; a hybrid map has imagery then labels.</summary>
        public Bitmap[] Layers = [];
        public bool Loading;
        public bool Complete;
        public int Attempts;
        public DateTime RetryAt;
        public long LastUsed;

        public void DisposeLayers()
        {
            foreach (var layer in Layers)
                layer.Dispose();
            Layers = [];
        }
    }
}
