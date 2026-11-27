using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MissionPlanner.App.Core.ViewModels.Tools;

namespace MissionPlanner.App.Views.Tools;

/// <summary>
/// Mission Planner's ProximityControl drawing: range rings every step with their distance, spokes
/// every 45°, the vehicle in the middle nose up, a red 45° arc per distance sensor (with its
/// distance) and a yellow arc per obstacle-distance reading.
/// </summary>
public sealed class ProximityRadar : Control
{
    public static readonly StyledProperty<ProximityFrame?> FrameProperty =
        AvaloniaProperty.Register<ProximityRadar, ProximityFrame?>(nameof(Frame));

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<ProximityRadar, IBrush?>(nameof(Background));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<ProximityRadar, IBrush?>(nameof(Foreground));

    private static readonly Typeface Bold = new(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);
    private static readonly IPen GridPen = new Pen(Brushes.DimGray, 1);
    private static readonly IPen SensorPen = new Pen(Brushes.Red, 3);
    private static readonly IPen ObstaclePen = new Pen(Brushes.Yellow, 3);
    private static readonly IBrush RingText = Brushes.Green;
    private static readonly IBrush SpokeText = Brushes.DimGray;

    static ProximityRadar() => AffectsRender<ProximityRadar>(FrameProperty, BackgroundProperty, ForegroundProperty);

    public ProximityFrame? Frame { get => GetValue(FrameProperty); set => SetValue(FrameProperty, value); }

    public IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }

    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (Background != null)
            ctx.FillRectangle(Background, new Rect(0, 0, w, h));
        var frame = Frame ?? ProximityFrame.Empty;
        var c = new Point(w / 2, h / 2);
        var scale = ProximityGeometry.CmPerPixel(frame.RangeCm, w, h);
        if (!double.IsFinite(scale) || scale <= 0 || !Finite(c.X, c.Y))
            return;

        // range rings and their distances
        foreach (var ring in ProximityGeometry.Rings(frame.RangeCm))
        {
            var r = ring / scale;
            ctx.DrawEllipse(null, GridPen, c, r, r);
            Text(ctx, (ring / 100).ToString("0.0", CultureInfo.CurrentCulture) + "m", new Point(c.X + 5, c.Y - r), RingText);
        }

        // spokes every 45°, labelled at the outer ring
        var outer = frame.RangeCm / scale;
        for (var a = 0; a < 360; a += 45)
        {
            var (x, y) = ProximityGeometry.Point(c.X, c.Y, a, outer);
            ctx.DrawLine(GridPen, c, new Point(x, y));
            var (lx, ly) = ProximityGeometry.Point(c.X, c.Y, a, outer + 12);
            Text(ctx, a.ToString(CultureInfo.InvariantCulture), new Point(lx, ly), SpokeText, centre: true);
        }

        DrawVehicle(ctx, c, frame.VehicleSizeCm / scale);

        foreach (var s in frame.Sectors)
        {
            // a NaN or infinite reading would make NaN geometry (Mission Planner shows a red X there): skip it
            var r = ProximityGeometry.RadiusPx(s.DistanceCm, frame.RangeCm, scale);
            if (!double.IsFinite(r) || r <= 0.5 || Arc(c, r, s.StartDeg, s.EndDeg) is not { } arc)
                continue;
            ctx.DrawGeometry(null, s.IsCustom ? ObstaclePen : SensorPen, arc);
            if (!s.IsCustom)
            {
                var (x, y) = ProximityGeometry.Point(c.X, c.Y, s.CenterDeg, r + 14);
                if (Finite(x, y))
                    Text(ctx, (s.DistanceCm / 100).ToString("0.0", CultureInfo.CurrentCulture) + "m", new Point(x, y), RingText, centre: true);
            }
        }
    }

    private static bool Finite(double x, double y) => double.IsFinite(x) && double.IsFinite(y);

    private void DrawVehicle(DrawingContext ctx, Point c, double size)
    {
        var half = Math.Max(4, size / 2);
        var brush = Foreground ?? Brushes.White;
        var pen = new Pen(brush, 2);
        ctx.DrawEllipse(null, pen, c, half, half);
        // the nose
        var g = new StreamGeometry();
        using (var s = g.Open())
        {
            s.BeginFigure(new Point(c.X, c.Y - half - Math.Max(4, half * 0.35)), true);
            s.LineTo(new Point(c.X - half * 0.35, c.Y - half * 0.55));
            s.LineTo(new Point(c.X + half * 0.35, c.Y - half * 0.55));
            s.EndFigure(true);
        }

        ctx.DrawGeometry(brush, null, g);
    }

    /// <summary>The arc of a sector, or null when its end points are not finite.</summary>
    private static StreamGeometry? Arc(Point c, double r, double from, double to)
    {
        var sweep = Math.Clamp(to - from, 0.5, 359.5);
        var (x0, y0) = ProximityGeometry.Point(c.X, c.Y, from, r);
        var (x1, y1) = ProximityGeometry.Point(c.X, c.Y, from + sweep, r);
        if (!double.IsFinite(sweep) || !Finite(x0, y0) || !Finite(x1, y1))
            return null;
        var g = new StreamGeometry();
        using var s = g.Open();
        s.BeginFigure(new Point(x0, y0), false);
        s.ArcTo(new Point(x1, y1), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise);
        s.EndFigure(false);
        return g;
    }

    private static void Text(DrawingContext ctx, string text, Point at, IBrush brush, bool centre = false)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Bold, 12, brush);
        var p = centre ? new Point(at.X - ft.Width / 2, at.Y - ft.Height / 2) : new Point(at.X, at.Y - ft.Height);
        ctx.DrawText(ft, p);
    }
}
