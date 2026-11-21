using System.Globalization;
using Avalonia;
using Avalonia.Media;
using GMap.NET;
using MissionPlanner.App.Core.ViewModels.FlightPlan;

namespace MissionPlanner.App.Controls.FlightPlan;

/// <summary>Brushes and pens of the Survey (Grid) preview.</summary>
internal static class SurveyPaint
{
    public static readonly IBrush Shadow = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
    public static readonly IBrush Route = Brush("#f2c94c");

    public static readonly IPen LanePen = new Pen(Route, 2.4, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
    public static readonly IPen LaneShadow = new Pen(Shadow, 4.8, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
    public static readonly IPen ExtensionPen = new Pen(new SolidColorBrush(Color.FromArgb(170, 242, 201, 76)), 2, lineCap: PenLineCap.Round);
    public static readonly IPen TransitPen = new Pen(new SolidColorBrush(Color.FromArgb(190, 242, 201, 76)), 1.6, lineCap: PenLineCap.Round);
    public static readonly IPen HomeLegPen = new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 1.4, new DashStyle([4, 3], 0));

    public static readonly IBrush PolygonStroke = Brush("#e879f9");
    public static readonly IBrush PolygonFill = new SolidColorBrush(Color.FromArgb(34, 232, 121, 249));
    public static readonly IPen PolygonPen = new Pen(PolygonStroke, 2, lineJoin: PenLineJoin.Round);
    public static readonly IPen VertexPen = new Pen(PolygonStroke, 2);

    public static readonly IBrush TriggerFill = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255));
    public static readonly IPen TriggerOutline = new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 1);

    public static readonly IBrush StartFill = Brush("#8cc63f");
    public static readonly IBrush EndFill = Brush("#e5534b");
    public static readonly IBrush HomeFill = Brush("#f0b429");
    public static readonly IBrush DarkText = Brush("#11160c");
    public static readonly IPen MarkerOutline = new Pen(new SolidColorBrush(Color.FromArgb(200, 10, 12, 14)), 1.4);

    private static readonly Typeface Bold = new(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);
    private static readonly Dictionary<(string, IBrush), FormattedText> Labels = new();

    public static FormattedText Label(string text, IBrush brush)
    {
        if (!Labels.TryGetValue((text, brush), out var ft))
        {
            ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Bold, 11, brush);
            Labels[(text, brush)] = ft;
        }

        return ft;
    }

    /// <summary>All segments as one geometry (one draw call per style).</summary>
    public static StreamGeometry? Segments(IReadOnlyList<(PointLatLng A, PointLatLng B)> segments, MapViewport vp)
    {
        if (segments.Count == 0)
            return null;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            foreach (var (a, b) in segments)
            {
                g.BeginFigure(vp.ToScreen(a), false);
                g.LineTo(vp.ToScreen(b));
                g.EndFigure(false);
            }
        }

        return geo;
    }

    public static bool OnScreen(Point p, MapViewport vp, double margin = 20) =>
        p.X >= -margin && p.Y >= -margin && p.X <= vp.Size.Width + margin && p.Y <= vp.Size.Height + margin;

    private static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));
}

/// <summary>The survey polygon: translucent fill, outline and vertices.</summary>
public sealed class SurveyPolygonLayer(SurveyGridViewModel vm) : MapLayer
{
    public override void Render(DrawingContext ctx, MapViewport vp)
    {
        var points = vm.PolygonPoints;
        if (points.Count < 2)
            return;

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(vp.ToScreen(points[0]), true);
            for (var i = 1; i < points.Count; i++)
                g.LineTo(vp.ToScreen(points[i]));
            g.EndFigure(true);
        }

        ctx.DrawGeometry(SurveyPaint.PolygonFill, SurveyPaint.PolygonPen, geo);
        foreach (var p in points)
            ctx.DrawEllipse(SurveyPaint.Shadow, SurveyPaint.VertexPen, vp.ToScreen(p), 3.5, 3.5);
    }
}

/// <summary>The generated route: lanes, lead-ins and overshoots, turns, legs to home and trigger points.</summary>
public sealed class SurveyRouteLayer(SurveyGridViewModel vm) : MapLayer
{
    public override void Render(DrawingContext ctx, MapViewport vp)
    {
        var preview = vm.Preview;

        if (SurveyPaint.Segments(preview.HomeLegs, vp) is { } home)
            ctx.DrawGeometry(null, SurveyPaint.HomeLegPen, home);

        var lanes = SurveyPaint.Segments(preview.Lanes, vp);
        var extensions = SurveyPaint.Segments(preview.Extensions, vp);
        var transits = SurveyPaint.Segments(preview.Transits, vp);
        if (lanes != null)
            ctx.DrawGeometry(null, SurveyPaint.LaneShadow, lanes);
        if (transits != null)
            ctx.DrawGeometry(null, SurveyPaint.TransitPen, transits);
        if (extensions != null)
            ctx.DrawGeometry(null, SurveyPaint.ExtensionPen, extensions);
        if (lanes != null)
            ctx.DrawGeometry(null, SurveyPaint.LanePen, lanes);

        DrawFootprints(ctx, vp, preview.Triggers);
        DrawTriggers(ctx, vp, preview.Triggers);
    }

    private static readonly IBrush FootprintFill = new SolidColorBrush(Color.FromArgb(28, 64, 200, 255));
    private static readonly IPen FootprintPen = new Pen(new SolidColorBrush(Color.FromArgb(110, 64, 200, 255)), 0.8);

    /// <summary>Each photo's ground area (Mission Planner's footprints), when a camera is chosen.</summary>
    private void DrawFootprints(DrawingContext ctx, MapViewport vp, IReadOnlyList<PointLatLng> triggers)
    {
        if (!vm.ShowFootprints || vm.FootprintAcross <= 0 || triggers.Count == 0 || triggers.Count > 5000)
            return;
        var angle = (double)(vm.Angle ?? 0);
        foreach (var t in triggers)
        {
            var c = SurveyCameraMath.Corners(t, vm.FootprintAcross, vm.FootprintAlong, angle);
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(vp.ToScreen(c[0]), true);
                for (var i = 1; i < 4; i++)
                    g.LineTo(vp.ToScreen(c[i]));
                g.EndFigure(true);
            }

            ctx.DrawGeometry(FootprintFill, FootprintPen, geo);
        }
    }

    private static void DrawTriggers(DrawingContext ctx, MapViewport vp, IReadOnlyList<PointLatLng> triggers)
    {
        if (triggers.Count < 2)
            return;

        // dots closer than a few pixels would just thicken the lane
        var a = vp.ToScreen(triggers[0]);
        var b = vp.ToScreen(triggers[1]);
        var spacing = Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        if (spacing < 5)
            return;

        var r = spacing < 10 ? 1.6 : 2.4;
        foreach (var t in triggers)
        {
            var p = vp.ToScreen(t);
            if (SurveyPaint.OnScreen(p, vp))
                ctx.DrawEllipse(SurveyPaint.TriggerFill, SurveyPaint.TriggerOutline, p, r, r);
        }
    }
}

/// <summary>Home, start and end of the survey.</summary>
public sealed class SurveyMarkerLayer(SurveyGridViewModel vm) : MapLayer
{
    public override void Render(DrawingContext ctx, MapViewport vp)
    {
        var preview = vm.Preview;
        if (vm.HomePoint is { } home)
            Marker(ctx, vp.ToScreen(home), "H", SurveyPaint.HomeFill);
        if (preview.End is { } end)
            Marker(ctx, vp.ToScreen(end), "E", SurveyPaint.EndFill);
        if (preview.Start is { } start)
            Marker(ctx, vp.ToScreen(start), "S", SurveyPaint.StartFill);
    }

    private static void Marker(DrawingContext ctx, Point c, string text, IBrush fill)
    {
        const double r = 9;
        ctx.DrawEllipse(SurveyPaint.Shadow, null, new Point(c.X + 1, c.Y + 1.5), r, r);
        ctx.DrawEllipse(fill, SurveyPaint.MarkerOutline, c, r, r);
        var ft = SurveyPaint.Label(text, SurveyPaint.DarkText);
        ctx.DrawText(ft, new Point(c.X - ft.Width / 2, c.Y - ft.Height / 2));
    }
}
