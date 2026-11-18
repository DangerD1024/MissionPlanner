using System.Globalization;
using Avalonia;
using Avalonia.Media;
using GMap.NET;
using MissionPlanner.App.Core.Mission;
using MissionPlanner.App.Core.ViewModels.FlightPlan;

namespace MissionPlanner.App.Controls.FlightPlan;

/// <summary>Brushes, pens and cached labels shared by the Flight Plan layers.</summary>
internal static class PlanPaint
{
    public static readonly Typeface Bold = new(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);
    public static readonly Typeface Normal = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    public static readonly IBrush Route = Brush("#f2c94c");
    public static readonly IBrush Outline = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
    public static readonly IBrush DarkText = Brush("#11160c");
    public static readonly IBrush White = Brushes.White;

    public static readonly IPen RoutePen = new Pen(Route, 2.2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
    public static readonly IPen RouteShadow = new Pen(Outline, 4.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
    public static readonly IPen RouteDashed = new Pen(Route, 2.2, new DashStyle([3, 2.5], 0), PenLineCap.Flat);
    public static readonly IPen MarkerOutline = new Pen(new SolidColorBrush(Color.FromArgb(200, 10, 12, 14)), 1.4);
    public static readonly IPen SelectedRing = new Pen(Brushes.White, 2.2);
    public static readonly IPen SelectedRingShadow = new Pen(Outline, 4.4);
    public static readonly IPen WpRadiusPen = new Pen(new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), 1, new DashStyle([4, 3], 0));
    public static readonly IPen LoiterPen = new Pen(Brush("#7dd3fc"), 1.6);

    public static readonly IBrush InclusionStroke = Brush("#4fa3e3");
    public static readonly IBrush InclusionFill = new SolidColorBrush(Color.FromArgb(22, 79, 163, 227));
    public static readonly IBrush ExclusionStroke = Brush("#e5534b");
    public static readonly IBrush ExclusionFill = new SolidColorBrush(Color.FromArgb(55, 229, 83, 75));
    public static readonly IPen InclusionPen = new Pen(InclusionStroke, 2.2, lineJoin: PenLineJoin.Round);
    public static readonly IPen ExclusionPen = new Pen(ExclusionStroke, 2.2, lineJoin: PenLineJoin.Round);
    public static readonly IPen HomeCirclePen = new Pen(InclusionStroke, 2, new DashStyle([5, 3], 0));

    public static readonly IBrush PolygonStroke = Brush("#e879f9");
    public static readonly IBrush PolygonFill = new SolidColorBrush(Color.FromArgb(30, 232, 121, 249));
    public static readonly IPen PolygonPen = new Pen(PolygonStroke, 2, new DashStyle([5, 3], 0), lineJoin: PenLineJoin.Round);
    public static readonly IPen PolygonVertexPen = new Pen(PolygonStroke, 2);

    public static readonly IBrush MidpointFill = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255));
    public static readonly IPen MidpointPen = new Pen(new SolidColorBrush(Color.FromArgb(200, 20, 24, 28)), 1.2);

    public static readonly IBrush Vehicle = Brush("#e5534b");

    private static readonly Dictionary<(string, double, IBrush, bool), FormattedText> TextCache = new();

    public static IBrush Fill(PlanMarkerKind kind) => kind switch
    {
        PlanMarkerKind.Spline => SplineFill,
        PlanMarkerKind.Loiter => LoiterFill,
        PlanMarkerKind.Land or PlanMarkerKind.LandStart => LandFill,
        PlanMarkerKind.Roi => RoiFill,
        PlanMarkerKind.FenceInclusion => InclusionStroke,
        PlanMarkerKind.FenceExclusion => ExclusionStroke,
        PlanMarkerKind.FenceReturn or PlanMarkerKind.Rally => RallyFill,
        PlanMarkerKind.Other => OtherFill,
        _ => WaypointFill,
    };

    private static readonly IBrush WaypointFill = Brush("#8cc63f");
    private static readonly IBrush SplineFill = Brush("#2dd4bf");
    private static readonly IBrush LoiterFill = Brush("#7dd3fc");
    private static readonly IBrush LandFill = Brush("#f97316");
    private static readonly IBrush RoiFill = Brush("#f87171");
    private static readonly IBrush RallyFill = Brush("#f0b429");
    private static readonly IBrush OtherFill = Brush("#c084fc");
    public static readonly IBrush HomeFill = Brush("#f0b429");

    public static FormattedText Text(string text, double size, IBrush brush, bool bold = true)
    {
        var key = (text, size, brush, bold);
        if (!TextCache.TryGetValue(key, out var ft))
        {
            if (TextCache.Count > 4000)
                TextCache.Clear();
            ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold ? Bold : Normal, size, brush);
            TextCache[key] = ft;
        }

        return ft;
    }

    /// <summary>Text centred on a point.</summary>
    public static void DrawCentred(DrawingContext ctx, FormattedText ft, Point centre) =>
        ctx.DrawText(ft, new Point(centre.X - ft.Width / 2, centre.Y - ft.Height / 2));

    /// <summary>Text with a dark halo, readable on imagery.</summary>
    public static void DrawLabel(DrawingContext ctx, string text, Point at, double size = 11)
    {
        var shadow = Text(text, size, Outline, bold: false);
        var fg = Text(text, size, White, bold: false);
        ctx.DrawText(shadow, new Point(at.X + 1, at.Y + 1));
        ctx.DrawText(fg, at);
    }

    public static StreamGeometry Polyline(IReadOnlyList<PointLatLng> points, MapViewport vp, bool closed, bool filled)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(vp.ToScreen(points[0]), filled);
            for (var i = 1; i < points.Count; i++)
                g.LineTo(vp.ToScreen(points[i]));
            g.EndFigure(closed);
        }

        return geo;
    }

    /// <summary>Radius in pixels of a ground distance at a point.</summary>
    public static double Pixels(double metres, PointLatLng at, MapViewport vp)
    {
        var mpp = vp.MetersPerPixel(at.Lat);
        return mpp <= 0 ? 0 : metres / mpp;
    }

    public static bool OnScreen(Point p, MapViewport vp, double margin = 40) =>
        p.X >= -margin && p.Y >= -margin && p.X <= vp.Size.Width + margin && p.Y <= vp.Size.Height + margin;

    private static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));
}

/// <summary>Mission route: solid legs, dashed legs to and from home, waypoint and loiter radius circles.</summary>
public sealed class MissionRouteLayer(FlightPlanViewModel vm) : MapLayer
{
    public override void Render(DrawingContext ctx, MapViewport vp)
    {
        var g = vm.MissionGeometry;
        var faded = !vm.IsMissionMode;
        using (ctx.PushOpacity(faded ? 0.45 : 1))
        {
            foreach (var m in g.Markers)
            {
                var c = vp.ToScreen(m.Position);
                if (!PlanPaint.OnScreen(c, vp, 400))
                    continue;
                if (m.Kind == PlanMarkerKind.Loiter)
                {
                    var r = PlanPaint.Pixels(m.Radius, m.Position, vp);
                    if (r > 4)
                        ctx.DrawEllipse(null, PlanPaint.LoiterPen, c, r, r);
                }
                else if (m.Kind is PlanMarkerKind.Waypoint or PlanMarkerKind.Spline && m.Radius > 0)
                {
                    var r = PlanPaint.Pixels(m.Radius, m.Position, vp);
                    if (r > 14)
                        ctx.DrawEllipse(null, PlanPaint.WpRadiusPen, c, r, r);
                }
            }

            foreach (var line in g.Lines)
            {
                var geo = PlanPaint.Polyline(line.Points, vp, closed: false, filled: false);
                ctx.DrawGeometry(null, PlanPaint.RouteShadow, geo);
                ctx.DrawGeometry(null, line.Dashed ? PlanPaint.RouteDashed : PlanPaint.RoutePen, geo);
                if (!line.Dashed)
                    DrawArrows(ctx, vp, line.Points);
            }
        }
    }

    /// <summary>Small direction chevrons at the middle of legs long enough to show them.</summary>
    private static void DrawArrows(DrawingContext ctx, MapViewport vp, IReadOnlyList<PointLatLng> points)
    {
        for (var i = 1; i < points.Count; i++)
        {
            var a = vp.ToScreen(points[i - 1]);
            var b = vp.ToScreen(points[i]);
            var d = b - a;
            var len = Math.Sqrt(d.X * d.X + d.Y * d.Y);
            if (len < 90)
                continue;
            var mid = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            if (!PlanPaint.OnScreen(mid, vp))
                continue;
            var ux = d.X / len;
            var uy = d.Y / len;
            // a quarter along, clear of the "+" insert handle at the middle
            var at = new Point(a.X + d.X * 0.3, a.Y + d.Y * 0.3);
            var tip = new Point(at.X + ux * 6, at.Y + uy * 6);
            var l = new Point(at.X - ux * 4 - uy * 5, at.Y - uy * 4 + ux * 5);
            var r = new Point(at.X - ux * 4 + uy * 5, at.Y - uy * 4 - ux * 5);
            var geo = new StreamGeometry();
            using (var gc = geo.Open())
            {
                gc.BeginFigure(tip, true);
                gc.LineTo(l);
                gc.LineTo(r);
                gc.EndFigure(true);
            }

            ctx.DrawGeometry(PlanPaint.Route, PlanPaint.MarkerOutline, geo);
        }
    }
}

/// <summary>Fence polygons and circles (inclusion blue, exclusion red).</summary>
public sealed class FenceShapesLayer(FlightPlanViewModel vm) : MapLayer
{
    public override void Render(DrawingContext ctx, MapViewport vp)
    {
        var g = vm.FenceGeometry;
        var faded = vm.MissionType != MAVLink.MAV_MISSION_TYPE.FENCE;
        using (ctx.PushOpacity(faded ? 0.6 : 1))
        {
            foreach (var poly in g.Polygons)
            {
                if (poly.Points.Count < 2)
                    continue;
                var geo = PlanPaint.Polyline(poly.Points, vp, closed: true, filled: true);
                ctx.DrawGeometry(poly.Inclusion ? PlanPaint.InclusionFill : PlanPaint.ExclusionFill,
                    poly.Inclusion ? PlanPaint.InclusionPen : PlanPaint.ExclusionPen, geo);
            }

            foreach (var circle in g.Circles)
            {
                var c = vp.ToScreen(circle.Center);
                var r = PlanPaint.Pixels(circle.Radius, circle.Center, vp);
                if (r < 1)
                    continue;
                var pen = circle.AroundHome ? PlanPaint.HomeCirclePen : circle.Inclusion ? PlanPaint.InclusionPen : PlanPaint.ExclusionPen;
                ctx.DrawEllipse(circle.Inclusion ? PlanPaint.InclusionFill : PlanPaint.ExclusionFill, pen, c, r, r);
            }
        }
    }
}

/// <summary>
/// Numbered markers of one list. Only the list being edited is draggable and shows "+" insert
/// handles; the others are drawn faded for reference.
/// </summary>
public sealed class PlanMarkerLayer(FlightPlanViewModel vm, MAVLink.MAV_MISSION_TYPE type) : MapLayer
{
    private const double Radius = 10.5;
    private const double MidpointRadius = 6;

    private bool Editable => vm.MissionType == type;

    private PlanGeometry Geometry => type switch
    {
        MAVLink.MAV_MISSION_TYPE.FENCE => vm.FenceGeometry,
        MAVLink.MAV_MISSION_TYPE.RALLY => vm.RallyGeometry,
        _ => vm.MissionGeometry,
    };

    public override void Render(DrawingContext ctx, MapViewport vp)
    {
        var g = Geometry;
        var editable = Editable;
        using (ctx.PushOpacity(editable ? 1 : 0.5))
        {
            if (editable)
            {
                foreach (var mid in g.Midpoints)
                    DrawMidpoint(ctx, vp.ToScreen(mid.Position), vp);
            }

            var showAlt = type != MAVLink.MAV_MISSION_TYPE.FENCE && g.Markers.Count <= 80;
            foreach (var m in g.Markers)
            {
                var c = vp.ToScreen(m.Position);
                if (!PlanPaint.OnScreen(c, vp))
                    continue;
                var selected = editable && ReferenceEquals(vm.SelectedItem, m.Item);
                DrawMarker(ctx, c, m, selected, small: type == MAVLink.MAV_MISSION_TYPE.FENCE && m.Kind is
                    PlanMarkerKind.FenceInclusion or PlanMarkerKind.FenceExclusion && !selected);
                if (showAlt && editable)
                    PlanPaint.DrawLabel(ctx, m.Item.Alt.ToString("0", CultureInfo.InvariantCulture), new Point(c.X + Radius + 3, c.Y - 7));
            }
        }
    }

    public override object? HitTest(Point screen, MapViewport vp)
    {
        if (!Editable)
            return null;
        var g = Geometry;
        // last drawn is on top
        for (var i = g.Markers.Count - 1; i >= 0; i--)
        {
            var c = vp.ToScreen(g.Markers[i].Position);
            if (Near(c, screen, Radius + 3))
                return g.Markers[i].Item;
        }

        foreach (var mid in g.Midpoints)
        {
            if (Near(vp.ToScreen(mid.Position), screen, MidpointRadius + 3))
                return mid;
        }

        return null;
    }

    internal static bool Near(Point a, Point b, double r)
    {
        var d = a - b;
        return d.X * d.X + d.Y * d.Y <= r * r;
    }

    internal static void DrawMidpoint(DrawingContext ctx, Point c, MapViewport vp)
    {
        if (!PlanPaint.OnScreen(c, vp))
            return;
        ctx.DrawEllipse(PlanPaint.MidpointFill, PlanPaint.MidpointPen, c, MidpointRadius, MidpointRadius);
        ctx.DrawLine(PlanPaint.MidpointPen, new Point(c.X - 3, c.Y), new Point(c.X + 3, c.Y));
        ctx.DrawLine(PlanPaint.MidpointPen, new Point(c.X, c.Y - 3), new Point(c.X, c.Y + 3));
    }

    private static void DrawMarker(DrawingContext ctx, Point c, PlanMarker m, bool selected, bool small)
    {
        var r = small ? 7.5 : Radius;
        if (selected)
        {
            ctx.DrawEllipse(null, PlanPaint.SelectedRingShadow, c, r + 4, r + 4);
            ctx.DrawEllipse(null, PlanPaint.SelectedRing, c, r + 4, r + 4);
        }

        var fill = PlanPaint.Fill(m.Kind);
        if (m.Kind == PlanMarkerKind.Roi)
        {
            // a diamond: looked at, not flown to
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(c.X, c.Y - r - 1), true);
                g.LineTo(new Point(c.X + r + 1, c.Y));
                g.LineTo(new Point(c.X, c.Y + r + 1));
                g.LineTo(new Point(c.X - r - 1, c.Y));
                g.EndFigure(true);
            }

            ctx.DrawGeometry(fill, PlanPaint.MarkerOutline, geo);
        }
        else if (m.Kind is PlanMarkerKind.FenceInclusion or PlanMarkerKind.FenceExclusion && small)
        {
            ctx.DrawRectangle(fill, PlanPaint.MarkerOutline, new Rect(c.X - r, c.Y - r, r * 2, r * 2), 2, 2);
        }
        else
        {
            ctx.DrawEllipse(fill, PlanPaint.MarkerOutline, c, r, r);
        }

        var label = m.Kind switch
        {
            PlanMarkerKind.Rally => "R" + m.Number,
            PlanMarkerKind.FenceReturn => "F",
            _ => m.Number.ToString(CultureInfo.InvariantCulture),
        };
        var size = small ? 9 : label.Length >= 3 ? 9.5 : 11;
        PlanPaint.DrawCentred(ctx, PlanPaint.Text(label, size, PlanPaint.DarkText), c);
    }
}

/// <summary>Mission Planner's drawn polygon: dashed outline, draggable vertices, "+" handles on its edges.</summary>
public sealed class DrawPolygonLayer(FlightPlanViewModel vm) : MapLayer
{
    private const double VertexHalf = 5;

    public override void Render(DrawingContext ctx, MapViewport vp)
    {
        var pts = vm.Polygon;
        if (pts.Count == 0)
            return;

        if (pts.Count >= 2)
        {
            var geo = PlanPaint.Polyline(pts, vp, closed: pts.Count >= 3, filled: pts.Count >= 3);
            ctx.DrawGeometry(pts.Count >= 3 ? PlanPaint.PolygonFill : null, PlanPaint.PolygonPen, geo);
            foreach (var mid in PlanGeometry.PolygonMidpoints(pts))
                PlanMarkerLayer.DrawMidpoint(ctx, vp.ToScreen(mid.Position), vp);
        }

        for (var i = 0; i < pts.Count; i++)
        {
            var c = vp.ToScreen(pts[i]);
            ctx.DrawRectangle(Brushes.White, PlanPaint.PolygonVertexPen,
                new Rect(c.X - VertexHalf, c.Y - VertexHalf, VertexHalf * 2, VertexHalf * 2));
        }

        if (pts.Count >= 3)
        {
            var area = MissionEditing.FormatArea(MissionEditing.PolygonArea(pts));
            var centre = new PointLatLng(pts.Average(p => p.Lat), pts.Average(p => p.Lng));
            var c = vp.ToScreen(centre);
            var ft = PlanPaint.Text(area, 12, PlanPaint.White);
            PlanPaint.DrawLabel(ctx, area, new Point(c.X - ft.Width / 2, c.Y - ft.Height / 2), 12);
        }
    }

    public override object? HitTest(Point screen, MapViewport vp)
    {
        var pts = vm.Polygon;
        for (var i = pts.Count - 1; i >= 0; i--)
        {
            if (PlanMarkerLayer.Near(vp.ToScreen(pts[i]), screen, VertexHalf + 4))
                return new PolygonVertexHandle(i);
        }

        foreach (var mid in PlanGeometry.PolygonMidpoints(pts))
        {
            if (PlanMarkerLayer.Near(vp.ToScreen(mid.Position), screen, 9))
                return mid;
        }

        return null;
    }
}

/// <summary>The home marker, draggable.</summary>
public sealed class HomeLayer(FlightPlanViewModel vm) : MapLayer
{
    private const double Radius = 12;

    public override void Render(DrawingContext ctx, MapViewport vp)
    {
        if (vm.Home is not { } home)
            return;
        var c = vp.ToScreen(home);
        if (!PlanPaint.OnScreen(c, vp))
            return;
        ctx.DrawEllipse(PlanPaint.HomeFill, PlanPaint.MarkerOutline, c, Radius, Radius);
        PlanPaint.DrawCentred(ctx, PlanPaint.Text("H", 12, PlanPaint.DarkText), c);
    }

    public override object? HitTest(Point screen, MapViewport vp) =>
        vm.Home is { } home && PlanMarkerLayer.Near(vp.ToScreen(home), screen, Radius + 2) ? HomeHandle.Instance : null;
}

/// <summary>The connected vehicle: an arrow along its heading.</summary>
public sealed class PlanVehicleLayer(FlightPlanViewModel vm) : MapLayer
{
    public override void Render(DrawingContext ctx, MapViewport vp)
    {
        if (vm.VehiclePosition is not { } pos)
            return;
        var c = vp.ToScreen(pos);
        if (!PlanPaint.OnScreen(c, vp))
            return;
        var a = vm.VehicleHeading * Math.PI / 180;
        Point P(double fwd, double right) =>
            new(c.X + Math.Sin(a) * fwd + Math.Cos(a) * right, c.Y - Math.Cos(a) * fwd + Math.Sin(a) * right);

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(P(15, 0), true);
            g.LineTo(P(-10, 10));
            g.LineTo(P(-4, 0));
            g.LineTo(P(-10, -10));
            g.EndFigure(true);
        }

        ctx.DrawGeometry(PlanPaint.Vehicle, new Pen(Brushes.White, 1.6), geo);
    }
}
