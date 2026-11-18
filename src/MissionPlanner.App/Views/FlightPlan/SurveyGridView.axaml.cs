using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GMap.NET;
using MissionPlanner.App.Controls;
using MissionPlanner.App.Controls.FlightPlan;
using MissionPlanner.App.Core.ViewModels.FlightPlan;

namespace MissionPlanner.App.Views.FlightPlan;

public sealed partial class SurveyGridView : UserControl
{
    private SurveyGridViewModel? _vm;
    private bool _attached;
    private bool _zoomedToRoute;

    public SurveyGridView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Bind(DataContext as SurveyGridViewModel);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Bind(DataContext as SurveyGridViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Bind(null);
    }

    private void Bind(SurveyGridViewModel? vm)
    {
        if (!_attached)
            vm = null;
        if (ReferenceEquals(vm, _vm))
            return;

        if (_vm != null)
            _vm.PreviewChanged -= OnPreviewChanged;

        _vm = vm;
        Map.Layers.Clear();
        if (vm == null)
            return;

        Map.Layers.Add(new SurveyPolygonLayer(vm));
        Map.Layers.Add(new SurveyRouteLayer(vm));
        Map.Layers.Add(new SurveyMarkerLayer(vm));
        vm.PreviewChanged += OnPreviewChanged;

        // the polygon is known before the first grid: show it straight away
        var polygon = vm.Polygon.Select(p => new PointLatLng(p.Lat, p.Lng)).ToList();
        if (polygon.Count > 0)
        {
            Map.CenterOn(polygon.Average(p => p.Lat), polygon.Average(p => p.Lng));
            ZoomWhenLaidOut(polygon);
        }
    }

    private void OnPreviewChanged(object? sender, EventArgs e)
    {
        // lead-ins and overshoots can reach outside the polygon: fit the first route once
        if (!_zoomedToRoute && _vm is { Preview.Extent.Count: > 0 } vm)
        {
            _zoomedToRoute = true;
            ZoomWhenLaidOut(vm.Preview.Extent);
        }

        Map.InvalidateVisual();
    }

    private void OnZoomToSurvey(object? sender, RoutedEventArgs e)
    {
        if (_vm is { } vm)
            ZoomWhenLaidOut(vm.Preview.Extent.Count > 0 ? vm.Preview.Extent : vm.PolygonPoints);
    }

    /// <summary>The map can only fit points once it has a size.</summary>
    private void ZoomWhenLaidOut(IReadOnlyList<PointLatLng> points)
    {
        if (points.Count == 0)
            return;
        if (Map.Bounds.Width >= 10 && StatsCard.Bounds.Width > 0)
        {
            Fit(points);
            return;
        }

        void OnLayout(object? s, EventArgs e)
        {
            if (Map.Bounds.Width < 10)
                return;
            Map.LayoutUpdated -= OnLayout;
            Dispatcher.UIThread.Post(() => Fit(points), DispatcherPriority.Background);
        }

        Map.LayoutUpdated += OnLayout;
    }

    /// <summary>Like <see cref="MapView.ZoomToFit"/>, in the part of the map the statistics card leaves free.</summary>
    private void Fit(IReadOnlyList<PointLatLng> points)
    {
        var provider = Map.Provider;
        var width = Map.Bounds.Width;
        var height = Map.Bounds.Height;
        if (provider == null || points.Count == 0 || width < 10 || height < 10)
            return;

        // the card sits top-left; on a narrow map it is better to fit under it than beside it
        var inset = width > 2 * StatsCard.Bounds.Right ? StatsCard.Bounds.Right : 0;
        var free = width - inset;

        var minLat = points.Min(p => p.Lat);
        var maxLat = points.Max(p => p.Lat);
        var minLng = points.Min(p => p.Lng);
        var maxLng = points.Max(p => p.Lng);
        var proj = provider.Projection;
        var zoom = Math.Min(18, provider.MaxZoom ?? 20);
        for (; zoom > 2; zoom--)
        {
            var a = proj.FromLatLngToPixel(maxLat, minLng, zoom);
            var b = proj.FromLatLngToPixel(minLat, maxLng, zoom);
            if (b.X - a.X < free * 0.88 && b.Y - a.Y < height * 0.88)
                break;
        }

        var nw = proj.FromLatLngToPixel(maxLat, minLng, zoom);
        var se = proj.FromLatLngToPixel(minLat, maxLng, zoom);
        // centre of the box, shifted so it lands in the middle of the free part
        var cx = (nw.X + se.X) / 2.0 - inset / 2;
        var cy = (nw.Y + se.Y) / 2.0;
        var centre = proj.FromPixelToLatLng((long)cx, (long)cy, zoom);
        Map.SetCurrentValue(MapView.ZoomProperty, zoom);
        Map.CenterOn(centre.Lat, centre.Lng);
    }
}
