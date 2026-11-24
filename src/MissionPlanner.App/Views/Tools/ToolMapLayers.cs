using Avalonia;
using Avalonia.Media;
using GMap.NET;
using MissionPlanner.App.Controls;

namespace MissionPlanner.App.Views.Tools;

/// <summary>Rectangles on a map (DEM coverage, GeoTIFF outlines), the selected one highlighted.</summary>
internal sealed class AreaLayer : MapLayer
{
    private static readonly IPen Pen = new Pen(new SolidColorBrush(Color.Parse("#f0b429")), 1.5);
    private static readonly IPen SelectedPen = new Pen(new SolidColorBrush(Color.Parse("#4fa3e3")), 3);
    private static readonly IBrush SelectedFill = new SolidColorBrush(Color.FromArgb(40, 79, 163, 227));

    public IReadOnlyList<RectLatLng> Areas { get; set; } = [];

    public RectLatLng? Selected { get; set; }

    public override void Render(DrawingContext ctx, MapViewport vp)
    {
        foreach (var a in Areas)
            ctx.DrawRectangle(null, Pen, ToRect(a, vp));
        if (Selected is { } s)
            ctx.DrawRectangle(SelectedFill, SelectedPen, ToRect(s, vp));
    }

    private static Rect ToRect(RectLatLng a, MapViewport vp) =>
        new(vp.ToScreen(a.Top, a.Left), vp.ToScreen(a.Bottom, a.Right));

    /// <summary>The corners of an area, for <see cref="MapView.ZoomToFit"/>.</summary>
    public static List<PointLatLng> Corners(RectLatLng a) =>
        [new(a.Top, a.Left), new(a.Bottom, a.Right)];
}

/// <summary>Dots on a map (camera positions, photos), the selected one larger.</summary>
internal sealed class DotLayer(Color colour, double radius) : MapLayer
{
    private readonly IBrush _fill = new SolidColorBrush(colour);
    private static readonly IPen Outline = new Pen(Brushes.White, 1);
    private static readonly IBrush SelectedFill = new SolidColorBrush(Color.Parse("#4fa3e3"));

    public IReadOnlyList<PointLatLng> Points { get; set; } = [];

    public PointLatLng? Selected { get; set; }

    public override void Render(DrawingContext ctx, MapViewport vp)
    {
        foreach (var p in Points)
        {
            var s = vp.ToScreen(p);
            if (s.X < -10 || s.Y < -10 || s.X > vp.Size.Width + 10 || s.Y > vp.Size.Height + 10)
                continue;
            ctx.DrawEllipse(_fill, Outline, s, radius, radius);
        }

        if (Selected is { } sel)
            ctx.DrawEllipse(SelectedFill, Outline, vp.ToScreen(sel), radius + 3, radius + 3);
    }
}
