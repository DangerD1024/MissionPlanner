using Avalonia;
using Avalonia.Media;
using MissionPlanner.App.Core.ViewModels.Logs;

namespace MissionPlanner.App.Controls.Logs;

/// <summary>A log's GPS track as a line on the map, with start/end dots and an optional cursor.</summary>
public sealed class TrackLayer : MapLayer
{
    private static readonly IPen Casing = new Pen(new SolidColorBrush(Color.Parse("#a0000000")), 5, lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round);
    private static readonly IPen Line = new Pen(new SolidColorBrush(Color.Parse("#f0b429")), 2.5, lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round);
    private static readonly IBrush StartBrush = new SolidColorBrush(Color.Parse("#8cc63f"));
    private static readonly IBrush EndBrush = new SolidColorBrush(Color.Parse("#e5534b"));
    private static readonly IBrush CursorBrush = new SolidColorBrush(Color.Parse("#4fa3e3"));
    private static readonly IPen Outline = new Pen(Brushes.White, 1.5);

    private IReadOnlyList<LogTrackPoint> _points = [];

    public IReadOnlyList<LogTrackPoint> Points
    {
        get => _points;
        set => _points = value ?? [];
    }

    /// <summary>The position at the graph's cursor time; null hides it.</summary>
    public LogTrackPoint? Cursor { get; set; }

    public override void Render(DrawingContext ctx, MapViewport vp)
    {
        var pts = _points;
        if (pts.Count == 0)
            return;

        if (pts.Count > 1)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                var first = true;
                Point last = default;
                foreach (var p in pts)
                {
                    var s = vp.ToScreen(p.Lat, p.Lng);
                    if (first)
                    {
                        g.BeginFigure(s, false);
                        first = false;
                        last = s;
                        continue;
                    }

                    // points closer than a pixel add nothing but work
                    if (Math.Abs(s.X - last.X) < 0.7 && Math.Abs(s.Y - last.Y) < 0.7)
                        continue;
                    g.LineTo(s);
                    last = s;
                }

                g.EndFigure(false);
            }

            ctx.DrawGeometry(null, Casing, geometry);
            ctx.DrawGeometry(null, Line, geometry);
        }

        Dot(ctx, vp, pts[0], StartBrush, 5);
        Dot(ctx, vp, pts[^1], EndBrush, 5);
        if (Cursor is { } c)
            Dot(ctx, vp, c, CursorBrush, 7);
    }

    private static void Dot(DrawingContext ctx, MapViewport vp, LogTrackPoint p, IBrush fill, double r) =>
        ctx.DrawEllipse(fill, Outline, vp.ToScreen(p.Lat, p.Lng), r, r);
}
