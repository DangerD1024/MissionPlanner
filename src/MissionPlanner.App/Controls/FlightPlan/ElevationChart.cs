using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MissionPlanner.App.Core.ViewModels.FlightPlan;

namespace MissionPlanner.App.Controls.FlightPlan;

/// <summary>The elevation graph: terrain filled, the planned route as a line, waypoints numbered (metres AMSL against distance).</summary>
public sealed class ElevationChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<ProfileSample>?> SamplesProperty =
        AvaloniaProperty.Register<ElevationChart, IReadOnlyList<ProfileSample>?>(nameof(Samples));

    public static readonly StyledProperty<IReadOnlyList<ProfileWaypoint>?> WaypointsProperty =
        AvaloniaProperty.Register<ElevationChart, IReadOnlyList<ProfileWaypoint>?>(nameof(Waypoints));

    private static readonly IBrush GroundFill = new SolidColorBrush(Color.Parse("#8d6e63"), 0.55);
    private static readonly Pen GroundPen = new(new SolidColorBrush(Color.Parse("#8d6e63")), 1.5);
    private static readonly Pen PathPen = new(new SolidColorBrush(Color.Parse("#1e88e5")), 2);
    private static readonly IBrush WpBrush = new SolidColorBrush(Color.Parse("#1e88e5"));
    private static readonly IBrush AxisBrush = new SolidColorBrush(Color.Parse("#8a8f98"));
    private static readonly Pen GridPen = new(new SolidColorBrush(Color.Parse("#8a8f98"), 0.25), 1);

    static ElevationChart() => AffectsRender<ElevationChart>(SamplesProperty, WaypointsProperty);

    public IReadOnlyList<ProfileSample>? Samples
    {
        get => GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public IReadOnlyList<ProfileWaypoint>? Waypoints
    {
        get => GetValue(WaypointsProperty);
        set => SetValue(WaypointsProperty, value);
    }

    public override void Render(DrawingContext ctx)
    {
        var samples = Samples;
        if (samples == null || samples.Count < 2)
            return;
        const double left = 56, bottom = 28, top = 10, right = 12;
        var w = Bounds.Width - left - right;
        var h = Bounds.Height - top - bottom;
        if (w < 20 || h < 20)
            return;

        var maxDist = Math.Max(1, samples[^1].Distance);
        var alts = samples.Select(s => s.Path).Concat(samples.Where(s => s.Ground != null).Select(s => s.Ground!.Value)).ToList();
        var lo = alts.Min();
        var hi = alts.Max();
        var pad = Math.Max(5, (hi - lo) * 0.08);
        lo -= pad;
        hi += pad;
        double X(double d) => left + d / maxDist * w;
        double Y(double a) => top + (hi - a) / (hi - lo) * h;

        // grid and axis labels
        var altStep = NiceStep((hi - lo) / 5);
        for (var a = Math.Ceiling(lo / altStep) * altStep; a <= hi; a += altStep)
        {
            ctx.DrawLine(GridPen, new Point(left, Y(a)), new Point(left + w, Y(a)));
            Label(ctx, a.ToString("0", CultureInfo.InvariantCulture) + " m", new Point(4, Y(a) - 7));
        }

        var distStep = NiceStep(maxDist / 6);
        for (var d = 0.0; d <= maxDist + 1e-6; d += distStep)
        {
            ctx.DrawLine(GridPen, new Point(X(d), top), new Point(X(d), top + h));
            var text = maxDist >= 2000 ? (d / 1000).ToString("0.#", CultureInfo.InvariantCulture) + " km" : d.ToString("0", CultureInfo.InvariantCulture) + " m";
            Label(ctx, text, new Point(X(d) - 12, top + h + 6));
        }

        // terrain (gaps where it is unknown)
        var run = new List<Point>();
        void Flush()
        {
            if (run.Count >= 2)
            {
                var fill = new StreamGeometry();
                using (var g = fill.Open())
                {
                    g.BeginFigure(new Point(run[0].X, top + h), true);
                    foreach (var p in run)
                        g.LineTo(p);
                    g.LineTo(new Point(run[^1].X, top + h));
                    g.EndFigure(true);
                }

                ctx.DrawGeometry(GroundFill, null, fill);
                ctx.DrawGeometry(null, GroundPen, Line(run));
            }

            run.Clear();
        }

        foreach (var s in samples)
        {
            if (s.Ground is { } g)
                run.Add(new Point(X(s.Distance), Y(g)));
            else
                Flush();
        }

        Flush();

        ctx.DrawGeometry(null, PathPen, Line(samples.Select(s => new Point(X(s.Distance), Y(s.Path))).ToList()));
        foreach (var wp in Waypoints ?? [])
        {
            var p = new Point(X(wp.Distance), Y(wp.Path));
            ctx.DrawEllipse(WpBrush, null, p, 3.5, 3.5);
            Label(ctx, wp.Seq == 0 ? "H" : wp.Seq.ToString(CultureInfo.InvariantCulture), new Point(p.X - 3, p.Y - 18), WpBrush);
        }
    }

    private static StreamGeometry Line(IReadOnlyList<Point> pts)
    {
        var geo = new StreamGeometry();
        using var g = geo.Open();
        g.BeginFigure(pts[0], false);
        for (var i = 1; i < pts.Count; i++)
            g.LineTo(pts[i]);
        g.EndFigure(false);
        return geo;
    }

    private static void Label(DrawingContext ctx, string text, Point at, IBrush? brush = null) =>
        ctx.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, brush ?? AxisBrush), at);

    private static double NiceStep(double raw)
    {
        if (raw <= 0)
            return 1;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var f = raw / mag;
        return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10) * mag;
    }
}
