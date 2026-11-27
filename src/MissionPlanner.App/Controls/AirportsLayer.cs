using Avalonia;
using Avalonia.Media;
using MissionPlanner.App.Controls.FlightData;
using MissionPlanner.App.Core.Maps;

namespace MissionPlanner.App.Controls;

/// <summary>
/// Airports on a map (DATA and PLAN, Map settings > Show airports), as Mission Planner marks them: a faint
/// red disc of the airport's radius (9 km, 5.6 km in Australia) with a dashed edge, a dot on the airport and,
/// closer in, its ICAO code and name. From zoom 7 (a region) inward; the list is read on first use.
/// </summary>
public sealed class AirportsLayer(AirportCatalog catalog) : MapLayer
{
    public const int MinZoom = 7;
    private const int LabelZoom = 10;

    // Mission Planner fills with Color.FromArgb(25, Color.Red)
    private static readonly IBrush Disc = new SolidColorBrush(Color.FromArgb(25, 255, 0, 0));
    private static readonly IPen Edge = new Pen(new SolidColorBrush(Color.FromArgb(120, 255, 70, 70)), 1, new DashStyle([4, 4], 0));
    private static readonly IBrush Dot = new SolidColorBrush(Color.Parse("#ff5a4f"));
    private static readonly IPen DotOutline = new Pen(new SolidColorBrush(Color.FromArgb(220, 16, 19, 23)), 1.5);

    public override void Render(DrawingContext ctx, MapViewport vp)
    {
        if (!MapOptions.ShowAirports || vp.Zoom < MinZoom)
            return;
        if (!catalog.IsLoaded)
        {
            // the view redraws when it is read
            _ = catalog.LoadAsync();
            return;
        }

        // the view plus room for a circle reaching in from outside it
        var nw = vp.ToLatLng(new Point(0, 0));
        var se = vp.ToLatLng(new Point(vp.Size.Width, vp.Size.Height));
        const double latMargin = 0.1;
        var lngMargin = 0.1 / Math.Max(0.1, Math.Cos(Math.Clamp(nw.Lat, -85, 85) * Math.PI / 180));
        var west = nw.Lng - lngMargin;
        var east = se.Lng + lngMargin;
        if (east - west >= 360)
        {
            west = -180;
            east = 180;
        }
        else
        {
            west = Wrap(west);
            east = Wrap(east);
        }

        var labels = vp.Zoom >= LabelZoom;
        foreach (var a in catalog.Within(se.Lat - latMargin, nw.Lat + latMargin, west, east))
        {
            var p = vp.ToScreen(a.Lat, a.Lng);
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y))
                continue;
            var r = a.RadiusM / vp.MetersPerPixel(a.Lat);
            if (double.IsFinite(r) && r > 2)
                ctx.DrawEllipse(Disc, Edge, p, r, r);
            ctx.DrawEllipse(Dot, DotOutline, p, 4, 4);
            if (labels)
                MapPaint.DrawOutlined(ctx, a.Name.Length > 0 ? $"{a.Ident} {a.Name}" : a.Ident, 11, MapPaint.White, new Point(p.X + 8, p.Y - 7));
        }
    }

    private static double Wrap(double lng) => lng < -180 ? lng + 360 : lng > 180 ? lng - 360 : lng;
}
