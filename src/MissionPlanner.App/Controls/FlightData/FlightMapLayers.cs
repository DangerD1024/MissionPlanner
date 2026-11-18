using System.Globalization;
using Avalonia;
using Avalonia.Media;
using GMap.NET;
using MissionPlanner.App.Core.ViewModels.FlightData;

namespace MissionPlanner.App.Controls.FlightData;

/// <summary>Brushes, pens and label cache shared by the Flight Data map layers.</summary>
internal static class MapPaint
{
    public static readonly Typeface Face = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    public static readonly Typeface FaceBold = new(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);

    public static readonly IBrush Shadow = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0));
    public static readonly IBrush White = Brushes.White;
    public static readonly IBrush Dark = new SolidColorBrush(Color.Parse("#101317"));
    public static readonly IBrush MarkerFill = new SolidColorBrush(Color.FromArgb(215, 16, 19, 23));

    // Mission Planner's heading lines: red heading, green nav bearing, orange bearing to the waypoint, black GPS track
    public static readonly Color HeadingColor = Color.Parse("#ff3b30");
    public static readonly Color NavBearingColor = Color.Parse("#2ecc40");
    public static readonly Color TargetBearingColor = Color.Parse("#ff8000");
    public static readonly Color GpsTrackColor = Colors.Black;

    public static IPen LinePen(Color c, double width = 2) => new Pen(new SolidColorBrush(c), width, lineCap: PenLineCap.Round);

    private static readonly Dictionary<(string, double, IBrush), FormattedText> Labels = new();

    /// <summary>Labels are few and repeat every frame: build each once.</summary>
    public static FormattedText Label(string text, double size, IBrush brush, bool bold = true)
    {
        var key = (text, size, brush);
        if (!Labels.TryGetValue(key, out var ft))
        {
            if (Labels.Count > 4000)
                Labels.Clear();
            ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold ? FaceBold : Face, size, brush);
            Labels[key] = ft;
        }

        return ft;
    }

    public static void DrawCentered(DrawingContext ctx, FormattedText t, Point at) =>
        ctx.DrawText(t, new Point(at.X - t.Width / 2, at.Y - t.Height / 2));

    /// <summary>Text with a dark halo, readable on any imagery.</summary>
    public static void DrawOutlined(DrawingContext ctx, string text, double size, IBrush brush, Point at)
    {
        var shadow = Label(text, size, Dark);
        var fill = Label(text, size, brush);
        ctx.DrawText(shadow, at + new Point(1, 1));
        ctx.DrawText(shadow, at + new Point(-1, 0));
        ctx.DrawText(fill, at);
    }

    /// <summary>Screen points of a circle of a ground radius.</summary>
    public static StreamGeometry Circle(MapViewport vp, double lat, double lng, double radiusMetres)
    {
        var g = new StreamGeometry();
        using var c = g.Open();
        const int steps = 72;
        for (var i = 0; i <= steps; i++)
        {
            var p = Offset(lat, lng, i * 360.0 / steps, radiusMetres);
            var s = vp.ToScreen(p.Lat, p.Lng);
            if (i == 0)
                c.BeginFigure(s, true);
            else
                c.LineTo(s);
        }

        c.EndFigure(true);
        return g;
    }

    /// <summary>The point at a bearing and distance (spherical earth).</summary>
    public static PointLatLng Offset(double lat, double lng, double bearingDeg, double metres)
    {
        const double r = 6371000;
        var d = metres / r;
        var b = bearingDeg * Math.PI / 180;
        var la = lat * Math.PI / 180;
        var lo = lng * Math.PI / 180;
        var la2 = Math.Asin(Math.Sin(la) * Math.Cos(d) + Math.Cos(la) * Math.Sin(d) * Math.Cos(b));
        var lo2 = lo + Math.Atan2(Math.Sin(b) * Math.Sin(d) * Math.Cos(la), Math.Cos(d) - Math.Sin(la) * Math.Sin(la2));
        return new PointLatLng(la2 * 180 / Math.PI, lo2 * 180 / Math.PI);
    }
}

/// <summary>
/// The flown track. The polyline is built in pixel space relative to its first point, once per
/// track change or zoom, and only translated while the map pans; points closer than a pixel are
/// skipped.
/// </summary>
public sealed class TrackLayer(FlightMapModel model) : MapLayer
{
    private static readonly IPen Halo = new Pen(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), 4.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
    private static readonly IPen Line = new Pen(new SolidColorBrush(Color.Parse("#c77dff")), 2.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

    private StreamGeometry? _geometry;
    private long _version = -1;
    private int _zoom = -1;
    private PureProjection? _projection;
    private GPoint _reference;

    public override void Render(DrawingContext ctx, MapViewport vp)
    {
        var track = model.Track;
        if (track.Count < 2)
            return;

        if (_geometry == null || _version != track.Version || _zoom != vp.Zoom || !ReferenceEquals(_projection, vp.Projection))
            Build(track, vp);
        if (_geometry == null)
            return;

        using (ctx.PushTransform(Matrix.CreateTranslation(_reference.X - vp.OriginX, _reference.Y - vp.OriginY)))
        {
            ctx.DrawGeometry(null, Halo, _geometry);
            ctx.DrawGeometry(null, Line, _geometry);
        }
    }

    private void Build(TrackBuffer track, MapViewport vp)
    {
        _version = track.Version;
        _zoom = vp.Zoom;
        _projection = vp.Projection;
        var first = track[0];
        _reference = vp.Projection.FromLatLngToPixel(first.Lat, first.Lng, vp.Zoom);

        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(0, 0), false);
            double lx = 0, ly = 0;
            for (var i = 1; i < track.Count; i++)
            {
                var p = track[i];
                var px = vp.Projection.FromLatLngToPixel(p.Lat, p.Lng, vp.Zoom);
                double x = px.X - _reference.X, y = px.Y - _reference.Y;
                if (Math.Abs(x - lx) + Math.Abs(y - ly) < 1 && i != track.Count - 1)
                    continue;
                c.LineTo(new Point(x, y));
                lx = x;
                ly = y;
            }

            c.EndFigure(false);
        }

        _geometry = g;
    }
}

/// <summary>The planned mission, fence and rally points from the shared mission store.</summary>
public sealed class MissionLayer(FlightMapModel model) : MapLayer
{
    private static readonly IPen RoutePen = new Pen(new SolidColorBrush(Color.Parse("#ffd21f")), 3, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
    private static readonly IPen RouteHalo = new Pen(new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)), 5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
    private static readonly IPen HomeLegPen = new Pen(new SolidColorBrush(Color.Parse("#ffd21f")), 2, new DashStyle([4, 3], 0));
    private static readonly IPen InclusionPen = new Pen(new SolidColorBrush(Color.Parse("#ff7ac8")), 2);
    private static readonly IPen ExclusionPen = new Pen(new SolidColorBrush(Color.Parse("#ff4d4d")), 2);
    private static readonly IBrush ExclusionFill = new SolidColorBrush(Color.FromArgb(40, 255, 0, 0));
    private static readonly IPen CircleInclusionPen = new Pen(new SolidColorBrush(Color.Parse("#4fa3e3")), 2, new DashStyle([5, 3], 0));

    private static readonly IBrush WaypointBrush = new SolidColorBrush(Color.Parse("#ffd21f"));
    private static readonly IBrush CurrentBrush = new SolidColorBrush(Color.Parse("#8cc63f"));
    private static readonly IBrush OtherBrush = new SolidColorBrush(Color.Parse("#9aa4b0"));
    private static readonly IBrush FenceInBrush = new SolidColorBrush(Color.Parse("#4fa3e3"));
    private static readonly IBrush FenceOutBrush = new SolidColorBrush(Color.Parse("#ff4d4d"));
    private static readonly IBrush OrangeBrush = new SolidColorBrush(Color.Parse("#ff9f1a"));

    public override void Render(DrawingContext ctx, MapViewport vp)
    {
        var m = model.Mission;
        if (m.IsEmpty)
            return;

        foreach (var poly in m.FencePolygons)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(vp.ToScreen(poly.Points[0].Lat, poly.Points[0].Lng), true);
                for (var i = 1; i < poly.Points.Count; i++)
                    c.LineTo(vp.ToScreen(poly.Points[i].Lat, poly.Points[i].Lng));
                c.EndFigure(true);
            }

            ctx.DrawGeometry(poly.Inclusion ? null : ExclusionFill, poly.Inclusion ? InclusionPen : ExclusionPen, g);
        }

        foreach (var circle in m.FenceCircles)
        {
            var g = MapPaint.Circle(vp, circle.Lat, circle.Lng, circle.RadiusMetres);
            ctx.DrawGeometry(circle.Inclusion ? null : ExclusionFill, circle.Inclusion ? CircleInclusionPen : ExclusionPen, g);
        }

        var route = m.Route;
        if (route.Count >= 2)
        {
            var hasHome = m.Home != null;
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                var start = hasHome ? 1 : 0;
                c.BeginFigure(vp.ToScreen(route[start].Lat, route[start].Lng), false);
                for (var i = start + 1; i < route.Count; i++)
                    c.LineTo(vp.ToScreen(route[i].Lat, route[i].Lng));
                c.EndFigure(false);
            }

            if (hasHome)
            {
                // dashed legs from home to the first point and from the last point back
                var home = vp.ToScreen(route[0].Lat, route[0].Lng);
                ctx.DrawLine(HomeLegPen, home, vp.ToScreen(route[1].Lat, route[1].Lng));
                if (route.Count > 2)
                    ctx.DrawLine(HomeLegPen, vp.ToScreen(route[^1].Lat, route[^1].Lng), home);
            }

            ctx.DrawGeometry(null, RouteHalo, g);
            ctx.DrawGeometry(null, RoutePen, g);
        }

        var current = model.CurrentWp;
        foreach (var marker in m.Markers)
        {
            var p = vp.ToScreen(marker.Lat, marker.Lng);
            if (p.X < -30 || p.Y < -30 || p.X > vp.Size.Width + 30 || p.Y > vp.Size.Height + 30)
                continue;

            var (ring, r) = marker.Kind switch
            {
                MissionMarkerKind.Waypoint => (marker.Label == current ? CurrentBrush : WaypointBrush, 10.0),
                MissionMarkerKind.Other => (OtherBrush, 9.0),
                MissionMarkerKind.FenceInclusion => (FenceInBrush, 7.0),
                MissionMarkerKind.FenceExclusion => (FenceOutBrush, 7.0),
                _ => (OrangeBrush, 9.0),
            };
            ctx.DrawEllipse(MapPaint.Shadow, null, p + new Point(1, 1.5), r + 1.5, r + 1.5);
            ctx.DrawEllipse(MapPaint.MarkerFill, new Pen(ring, 2.2), p, r, r);
            if (marker.Kind is MissionMarkerKind.Waypoint or MissionMarkerKind.Other or MissionMarkerKind.Rally or MissionMarkerKind.FenceReturn)
            {
                var label = marker.Kind == MissionMarkerKind.FenceReturn ? "F" : marker.Label;
                MapPaint.DrawCentered(ctx, MapPaint.Label(label, label.Length > 2 ? 9 : 11, MapPaint.White), p);
            }
        }
    }
}

/// <summary>Vehicle icon (by type), heading lines, home and guided target.</summary>
public sealed class VehicleLayer(FlightMapModel model) : MapLayer
{
    private const double LineLength = 500; // pixels, as Mission Planner

    private static readonly IPen HeadingPen = MapPaint.LinePen(MapPaint.HeadingColor);
    private static readonly IPen NavPen = MapPaint.LinePen(MapPaint.NavBearingColor);
    private static readonly IPen TargetPen = MapPaint.LinePen(MapPaint.TargetBearingColor);
    private static readonly IPen TrackPen = MapPaint.LinePen(MapPaint.GpsTrackColor);

    private static readonly IBrush BodyFill = new SolidColorBrush(Color.Parse("#e5534b"));
    private static readonly IPen BodyStroke = new Pen(Brushes.White, 1.6, lineJoin: PenLineJoin.Round);
    private static readonly IBrush ShadowFill = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0));
    private static readonly IBrush RotorFill = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255));
    private static readonly IPen ArmPen = new Pen(Brushes.White, 4.5, lineCap: PenLineCap.Round);
    private static readonly IPen ArmShadow = new Pen(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), 6.5, lineCap: PenLineCap.Round);
    private static readonly IBrush HomeFill = new SolidColorBrush(Color.Parse("#8cc63f"));
    private static readonly IBrush HomeText = new SolidColorBrush(Color.Parse("#0f1a05"));
    private static readonly IPen GuidedPen = new Pen(new SolidColorBrush(Color.Parse("#4fa3e3")), 2.2);
    private static readonly IPen GuidedLeg = new Pen(new SolidColorBrush(Color.Parse("#4fa3e3")), 1.6, new DashStyle([4, 4], 0));

    // all icons drawn nose up (-Y), centred on the vehicle
    private static readonly Geometry Plane = Build(
        "M0,-26 L4,-13 L25,1 L27,6 L3,2 L2,9 L2,17 L9,22 L9,24 L1,24 L1,27 L-1,27 L-1,24 L-9,24 L-9,22 L-2,17 L-2,9 L-3,2 L-27,6 L-25,1 L-4,-13 Z");

    private static readonly Geometry RoverBody = Build("M-9,-15 L9,-15 Q11,-15 11,-13 L11,15 Q11,17 9,17 L-9,17 Q-11,17 -11,15 L-11,-13 Q-11,-15 -9,-15 Z");
    private static readonly Geometry RoverNose = Build("M0,-12 L6,-4 L-6,-4 Z");
    private static readonly Geometry Boat = Build("M0,-24 C9,-14 11,-2 10,10 L8,20 L-8,20 L-10,10 C-11,-2 -9,-14 0,-24 Z");
    private static readonly Geometry BoatDeck = Build("M0,-12 L5,-2 L-5,-2 Z");
    private static readonly Geometry QuadNose = Build("M0,-13 L6,-4 L-6,-4 Z");

    public override void Render(DrawingContext ctx, MapViewport vp)
    {
        var m = model;

        if (m.HasHome)
        {
            var h = vp.ToScreen(m.HomeLat, m.HomeLng);
            ctx.DrawEllipse(MapPaint.Shadow, null, h + new Point(1, 1.5), 12, 12);
            ctx.DrawEllipse(HomeFill, new Pen(Brushes.White, 1.6), h, 11, 11);
            MapPaint.DrawCentered(ctx, MapPaint.Label("H", 12, HomeText), h);
        }

        if (!m.HasPosition)
            return;

        var p = vp.ToScreen(m.Lat, m.Lng);

        if (m.HasGuided)
        {
            var g = vp.ToScreen(m.GuidedLat, m.GuidedLng);
            ctx.DrawLine(GuidedLeg, p, g);
            ctx.DrawEllipse(null, GuidedPen, g, 11, 11);
            ctx.DrawLine(GuidedPen, g + new Point(-16, 0), g + new Point(-6, 0));
            ctx.DrawLine(GuidedPen, g + new Point(6, 0), g + new Point(16, 0));
            ctx.DrawLine(GuidedPen, g + new Point(0, -16), g + new Point(0, -6));
            ctx.DrawLine(GuidedPen, g + new Point(0, 6), g + new Point(0, 16));
            MapPaint.DrawOutlined(ctx, string.Create(CultureInfo.InvariantCulture, $"Guided {m.GuidedAlt:0}"), 11, MapPaint.White, g + new Point(14, 6));
        }

        // lines first, so the icon sits on top
        Line(ctx, TrackPen, p, m.GroundCourse);
        if (m.ShowNavLines)
        {
            Line(ctx, NavPen, p, m.NavBearing);
            Line(ctx, TargetPen, p, m.TargetBearing);
        }

        Line(ctx, HeadingPen, p, m.Heading);

        var rotate = Matrix.CreateRotation(m.Heading * Math.PI / 180);
        using (ctx.PushTransform(rotate * Matrix.CreateTranslation(p.X + 2, p.Y + 3)))
            DrawIcon(ctx, m.Kind, shadow: true);
        using (ctx.PushTransform(rotate * Matrix.CreateTranslation(p.X, p.Y)))
            DrawIcon(ctx, m.Kind, shadow: false);
    }

    private static void Line(DrawingContext ctx, IPen pen, Point from, double bearingDeg)
    {
        var a = bearingDeg * Math.PI / 180;
        ctx.DrawLine(pen, from, new Point(from.X + Math.Sin(a) * LineLength, from.Y - Math.Cos(a) * LineLength));
    }

    private static void DrawIcon(DrawingContext ctx, VehicleIconKind kind, bool shadow)
    {
        var fill = shadow ? ShadowFill : BodyFill;
        var stroke = shadow ? null : BodyStroke;
        switch (kind)
        {
            case VehicleIconKind.Plane:
                ctx.DrawGeometry(fill, stroke, Plane);
                break;
            case VehicleIconKind.Rover:
                ctx.DrawGeometry(fill, stroke, RoverBody);
                if (!shadow)
                {
                    foreach (var (x, y) in new[] { (-13.0, -10.0), (13.0, -10.0), (-13.0, 10.0), (13.0, 10.0) })
                        ctx.FillRectangle(MapPaint.Dark, new Rect(x - 2.5, y - 4.5, 5, 9), 1.5f);
                    ctx.DrawGeometry(Brushes.White, null, RoverNose);
                }

                break;
            case VehicleIconKind.Boat:
                ctx.DrawGeometry(fill, stroke, Boat);
                if (!shadow)
                    ctx.DrawGeometry(Brushes.White, null, BoatDeck);
                break;
            default:
            {
                // X frame: four arms, rotor discs, a body and a nose marker
                const double arm = 15;
                var ends = new[] { new Point(-arm, -arm), new Point(arm, -arm), new Point(-arm, arm), new Point(arm, arm) };
                if (shadow)
                {
                    foreach (var e in ends)
                    {
                        ctx.DrawLine(ArmShadow, default, e);
                        ctx.DrawEllipse(ShadowFill, null, e, 8, 8);
                    }

                    break;
                }

                foreach (var e in ends)
                    ctx.DrawLine(ArmPen, default, e);
                for (var i = 0; i < ends.Length; i++)
                    ctx.DrawEllipse(i < 2 ? BodyFill : RotorFill, BodyStroke, ends[i], 7.5, 7.5);
                ctx.DrawEllipse(BodyFill, BodyStroke, default, 7, 7);
                ctx.DrawGeometry(Brushes.White, null, QuadNose);
                break;
            }
        }
    }

    private static Geometry Build(string path) => StreamGeometry.Parse(path);
}
