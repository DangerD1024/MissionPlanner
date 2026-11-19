using System.Globalization;
using Avalonia;
using Avalonia.Media;
using MissionPlanner.App.Core.ViewModels.FlightData;

namespace MissionPlanner.App.Controls.FlightData;

/// <summary>
/// ADS-B traffic: an aircraft icon turned to its heading, with callsign, altitude (display units)
/// and a climb / descent arrow. Orange normally, yellow / red for COLLISION threat levels, grey
/// and translucent once stale (not heard for 10 s).
/// </summary>
public sealed class AdsbLayer(FlightMapModel model) : MapLayer
{
    private static readonly Geometry Aircraft = StreamGeometry.Parse(
        "M0,-13 C1.4,-13 2,-11.5 2,-10 L2,-4 L12,2 L12,4.5 L2,1.5 L1.5,8 L5,10.5 L5,12.5 L0,11 L-5,12.5 L-5,10.5 L-1.5,8 L-2,1.5 L-12,4.5 L-12,2 L-2,-4 L-2,-10 C-2,-11.5 -1.4,-13 0,-13 Z");

    private static readonly IBrush Normal = new SolidColorBrush(Color.Parse("#ff9f1a"));
    private static readonly IBrush Warning = new SolidColorBrush(Color.Parse("#ffd21f"));
    private static readonly IBrush Danger = new SolidColorBrush(Color.Parse("#ff3b30"));
    private static readonly IBrush Stale = new SolidColorBrush(Color.FromArgb(150, 160, 168, 178));
    private static readonly IPen Outline = new Pen(new SolidColorBrush(Color.FromArgb(200, 16, 19, 23)), 1.4, lineJoin: PenLineJoin.Round);
    private static readonly IPen StaleOutline = new Pen(new SolidColorBrush(Color.FromArgb(120, 16, 19, 23)), 1, lineJoin: PenLineJoin.Round);
    private static readonly IBrush StaleText = new SolidColorBrush(Color.FromArgb(190, 230, 233, 237));

    public override void Render(DrawingContext ctx, MapViewport vp)
    {
        var traffic = model.Traffic;
        if (traffic.Count == 0)
            return;

        // stale ones first so live traffic draws on top
        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var t in traffic)
            {
                if (t.IsStale != (pass == 0))
                    continue;
                var p = vp.ToScreen(t.Lat, t.Lng);
                if (p.X < -60 || p.Y < -60 || p.X > vp.Size.Width + 60 || p.Y > vp.Size.Height + 60)
                    continue;
                Draw(ctx, t, p);
            }
        }
    }

    private static void Draw(DrawingContext ctx, AdsbContact t, Point p)
    {
        var fill = t.IsStale
            ? Stale
            : t.Threat switch
            {
                MAVLink.MAV_COLLISION_THREAT_LEVEL.HIGH => Danger,
                MAVLink.MAV_COLLISION_THREAT_LEVEL.LOW => Warning,
                _ => Normal,
            };

        var rotate = Matrix.CreateRotation(t.HeadingDeg * Math.PI / 180);
        if (!t.IsStale)
        {
            using (ctx.PushTransform(rotate * Matrix.CreateTranslation(p.X + 1.5, p.Y + 2)))
                ctx.DrawGeometry(MapPaint.Shadow, null, Aircraft);
        }

        using (ctx.PushTransform(rotate * Matrix.CreateTranslation(p.X, p.Y)))
            ctx.DrawGeometry(fill, t.IsStale ? StaleOutline : Outline, Aircraft);

        // label: callsign, then altitude with a climb / descent arrow
        var alt = t.AltitudeM * CurrentState.multiplieralt;
        var unit = Units.Altitude;
        var trend = t.VerticalSpeedMs > 0.5 ? " ↑" : t.VerticalSpeedMs < -0.5 ? " ↓" : "";
        var line2 = string.Create(CultureInfo.InvariantCulture, $"{alt:0} {unit}{trend}");
        if (t.IsStale)
            line2 += string.Create(CultureInfo.InvariantCulture, $"  ({t.Age.TotalSeconds:0}s)");
        var brush = t.IsStale ? StaleText : MapPaint.White;
        MapPaint.DrawOutlined(ctx, t.Label, 11, brush, new Point(p.X + 14, p.Y - 14));
        MapPaint.DrawOutlined(ctx, line2, 10, brush, new Point(p.X + 14, p.Y - 1));
    }
}
