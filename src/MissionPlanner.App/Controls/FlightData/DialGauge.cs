using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MissionPlanner.App.Controls.FlightData;

public enum DialKind
{
    /// <summary>A 270° scale from Minimum to Maximum, filled from Minimum.</summary>
    Arc,
    /// <summary>A 270° scale from -Maximum to +Maximum with zero at the top, filled from zero (vertical speed).</summary>
    Centered,
    /// <summary>A compass card turning under a fixed lubber line (heading).</summary>
    Compass,
}

/// <summary>
/// A flat round instrument for the Gauges tab: scale, value arc, needle, an optional second
/// needle (airspeed on the speed dial) or bug (navigation bearing on the compass), and the
/// value in the middle. Colours come from the theme through the brush properties.
/// </summary>
public sealed class DialGauge : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<DialGauge, double>(nameof(Value));
    public static readonly StyledProperty<double> SecondValueProperty = AvaloniaProperty.Register<DialGauge, double>(nameof(SecondValue), double.NaN);
    public static readonly StyledProperty<bool> ShowSecondProperty = AvaloniaProperty.Register<DialGauge, bool>(nameof(ShowSecond));
    public static readonly StyledProperty<double> MinimumProperty = AvaloniaProperty.Register<DialGauge, double>(nameof(Minimum));
    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<DialGauge, double>(nameof(Maximum), 100);
    public static readonly StyledProperty<DialKind> KindProperty = AvaloniaProperty.Register<DialGauge, DialKind>(nameof(Kind));
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<DialGauge, string?>(nameof(Title));
    public static readonly StyledProperty<string?> UnitProperty = AvaloniaProperty.Register<DialGauge, string?>(nameof(Unit));
    public static readonly StyledProperty<string?> SecondLabelProperty = AvaloniaProperty.Register<DialGauge, string?>(nameof(SecondLabel));
    public static readonly StyledProperty<string?> FirstLabelProperty = AvaloniaProperty.Register<DialGauge, string?>(nameof(FirstLabel));
    public static readonly StyledProperty<IBrush?> ForegroundProperty = AvaloniaProperty.Register<DialGauge, IBrush?>(nameof(Foreground));
    public static readonly StyledProperty<IBrush?> MutedProperty = AvaloniaProperty.Register<DialGauge, IBrush?>(nameof(Muted));
    public static readonly StyledProperty<IBrush?> TrackProperty = AvaloniaProperty.Register<DialGauge, IBrush?>(nameof(Track));
    public static readonly StyledProperty<IBrush?> FaceProperty = AvaloniaProperty.Register<DialGauge, IBrush?>(nameof(Face));
    public static readonly StyledProperty<IBrush?> AccentProperty = AvaloniaProperty.Register<DialGauge, IBrush?>(nameof(Accent));
    public static readonly StyledProperty<IBrush?> SecondaryProperty = AvaloniaProperty.Register<DialGauge, IBrush?>(nameof(Secondary));
    public static readonly StyledProperty<IBrush?> NegativeProperty = AvaloniaProperty.Register<DialGauge, IBrush?>(nameof(Negative));

    private const double Sweep = 270;
    private const double Start = -135; // degrees from 12 o'clock, clockwise

    private static readonly Typeface Face400 = new(FontFamily.Default, FontStyle.Normal, FontWeight.Normal);
    private static readonly Typeface Face600 = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface Face700 = new(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);

    static DialGauge()
    {
        AffectsRender<DialGauge>(ValueProperty, SecondValueProperty, ShowSecondProperty, MinimumProperty, MaximumProperty,
            KindProperty, TitleProperty, UnitProperty, SecondLabelProperty, FirstLabelProperty, ForegroundProperty,
            MutedProperty, TrackProperty, FaceProperty, AccentProperty, SecondaryProperty, NegativeProperty);
    }

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    /// <summary>A second needle (airspeed on the speed dial; the bug on the compass).</summary>
    public double SecondValue { get => GetValue(SecondValueProperty); set => SetValue(SecondValueProperty, value); }

    public bool ShowSecond { get => GetValue(ShowSecondProperty); set => SetValue(ShowSecondProperty, value); }

    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }

    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    public DialKind Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public string? Unit { get => GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

    /// <summary>Legend for the main needle when a second one shows ("GS").</summary>
    public string? FirstLabel { get => GetValue(FirstLabelProperty); set => SetValue(FirstLabelProperty, value); }

    /// <summary>Legend for the second needle ("AS").</summary>
    public string? SecondLabel { get => GetValue(SecondLabelProperty); set => SetValue(SecondLabelProperty, value); }

    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public IBrush? Muted { get => GetValue(MutedProperty); set => SetValue(MutedProperty, value); }

    public IBrush? Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    public IBrush? Face { get => GetValue(FaceProperty); set => SetValue(FaceProperty, value); }

    public IBrush? Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

    public IBrush? Secondary { get => GetValue(SecondaryProperty); set => SetValue(SecondaryProperty, value); }

    /// <summary>Fill for negative values on a centred dial (descending).</summary>
    public IBrush? Negative { get => GetValue(NegativeProperty); set => SetValue(NegativeProperty, value); }

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        var size = Math.Min(b.Width, b.Height);
        if (size < 40)
            return;

        var c = new Point(b.Width / 2, b.Height / 2 + (Kind == DialKind.Compass ? size * 0.03 : 0));
        var r = size / 2 - 6;
        var text = Foreground ?? Brushes.White;
        var muted = Muted ?? Brushes.Gray;
        var track = Track ?? Brushes.DimGray;
        var accent = Accent ?? Brushes.LimeGreen;

        // face
        ctx.DrawEllipse(Face, new Pen(track, 1), c, r, r);

        if (Kind == DialKind.Compass)
            RenderCompass(ctx, c, r * 0.9, text, muted, track, accent);
        else
            RenderScale(ctx, c, r * 0.86, text, muted, track, accent);
    }

    private void RenderScale(DrawingContext ctx, Point c, double r, IBrush text, IBrush muted, IBrush track, IBrush accent)
    {
        var centred = Kind == DialKind.Centered;
        var max = Math.Max(1e-6, Maximum);
        var min = centred ? -max : Minimum;
        if (max <= min)
            max = min + 1;

        var thickness = Math.Max(6, r * 0.11);
        var trackPen = new Pen(track, thickness, lineCap: PenLineCap.Round);
        ctx.DrawGeometry(null, trackPen, Arc(c, r, Start, Start + Sweep));

        double Angle(double v) => Start + Sweep * (Math.Clamp(v, min, max) - min) / (max - min);

        // value arc
        var value = double.IsNaN(Value) ? 0 : Value;
        var from = centred ? Angle(0) : Start;
        var to = Angle(value);
        if (Math.Abs(to - from) > 0.5)
        {
            var fill = centred && value < 0 ? Negative ?? accent : accent;
            ctx.DrawGeometry(null, new Pen(fill, thickness, lineCap: PenLineCap.Round), Arc(c, r, Math.Min(from, to), Math.Max(from, to)));
        }

        // ticks and numbers: about 5-10 major steps
        var step = Step((max - min) / 6);
        var labelSize = Math.Max(9, r * 0.13);
        var tickPen = new Pen(muted, 1.5);
        var minorPen = new Pen(muted, 1);
        var minor = step / 5;
        for (var v = Math.Ceiling(min / minor) * minor; v <= max + 1e-9; v += minor)
        {
            var a = Angle(v);
            var major = Math.Abs(v / step - Math.Round(v / step)) < 1e-6;
            var inner = r - thickness / 2 - (major ? r * 0.14 : r * 0.07);
            ctx.DrawLine(major ? tickPen : minorPen, Polar(c, inner, a), Polar(c, r - thickness / 2 - 2, a));
            if (major)
            {
                var label = Text(Format(v), labelSize, muted, Face400);
                var p = Polar(c, inner - labelSize * 1.0, a);
                ctx.DrawText(label, new Point(p.X - label.Width / 2, p.Y - label.Height / 2));
            }
        }

        // second needle under the first
        if (ShowSecond && !double.IsNaN(SecondValue))
            Needle(ctx, c, r * 0.78, Angle(SecondValue), Secondary ?? Brushes.DeepSkyBlue, r * 0.035);
        Needle(ctx, c, r * 0.82, Angle(value), text, r * 0.045);
        ctx.DrawEllipse(Face, new Pen(text, 2), c, r * 0.07, r * 0.07);

        // value under the hub, unit under it; the title (or the needle legend) sits in the gap at the bottom
        var big = Text(Format(value, precise: true), Math.Max(12, r * 0.30), text, Face700);
        var valueY = c.Y + r * 0.13;
        ctx.DrawText(big, new Point(c.X - big.Width / 2, valueY));
        if (!string.IsNullOrEmpty(Unit))
        {
            var unit = Text(Unit!, Math.Max(9, r * 0.12), muted, Face600);
            ctx.DrawText(unit, new Point(c.X - unit.Width / 2, valueY + big.Height - r * 0.04));
        }

        var bottom = c.Y + r * 0.80;
        var small = Math.Max(9, r * 0.12);
        if (ShowSecond && !double.IsNaN(SecondValue) && !string.IsNullOrEmpty(SecondLabel))
        {
            var first = Text($"{FirstLabel} {Format(value, true)}", small, text, Face600);
            var second = Text($"{SecondLabel} {Format(SecondValue, true)}", small, Secondary ?? Brushes.DeepSkyBlue, Face600);
            ctx.DrawText(first, new Point(c.X - first.Width - 5, bottom));
            ctx.DrawText(second, new Point(c.X + 5, bottom));
        }
        else if (!string.IsNullOrEmpty(Title))
        {
            var title = Text(Title!.ToUpperInvariant(), small, muted, Face600);
            ctx.DrawText(title, new Point(c.X - title.Width / 2, bottom));
        }
    }

    private void RenderCompass(DrawingContext ctx, Point c, double r, IBrush text, IBrush muted, IBrush track, IBrush accent)
    {
        var heading = double.IsNaN(Value) ? 0 : Value;
        var labelSize = Math.Max(10, r * 0.15);
        var tickPen = new Pen(muted, 1.5);
        var minorPen = new Pen(muted, 1);

        for (var d = 0; d < 360; d += 5)
        {
            var a = d - heading;
            var major = d % 30 == 0;
            var len = major ? r * 0.12 : d % 10 == 0 ? r * 0.07 : r * 0.04;
            ctx.DrawLine(major ? tickPen : minorPen, Polar(c, r - len, a), Polar(c, r, a));
            if (major)
            {
                var name = d switch
                {
                    0 => "N",
                    90 => "E",
                    180 => "S",
                    270 => "W",
                    _ => (d / 10).ToString(CultureInfo.InvariantCulture),
                };
                var cardinal = d % 90 == 0;
                var label = Text(name, cardinal ? labelSize * 1.2 : labelSize * 0.85, d == 0 ? accent : cardinal ? text : muted,
                    cardinal ? Face700 : Face400);
                var p = Polar(c, r - r * 0.12 - labelSize, a);
                // labels turn with the card, upright towards the centre
                using (ctx.PushTransform(Matrix.CreateTranslation(-p.X, -p.Y) * Matrix.CreateRotation(a * Math.PI / 180) *
                                         Matrix.CreateTranslation(p.X, p.Y)))
                    ctx.DrawText(label, new Point(p.X - label.Width / 2, p.Y - label.Height / 2));
            }
        }

        // navigation bearing bug
        if (ShowSecond && !double.IsNaN(SecondValue))
        {
            var a = SecondValue - heading;
            var bug = new StreamGeometry();
            using (var g = bug.Open())
            {
                g.BeginFigure(Polar(c, r * 0.99, a - 4), true);
                g.LineTo(Polar(c, r * 0.99, a + 4));
                g.LineTo(Polar(c, r * 0.86, a));
                g.EndFigure(true);
            }

            ctx.DrawGeometry(Secondary ?? Brushes.Orange, null, bug);
        }

        // lubber line and aircraft symbol, fixed
        var lubber = new StreamGeometry();
        using (var g = lubber.Open())
        {
            g.BeginFigure(new Point(c.X, c.Y - r * 1.06), true);
            g.LineTo(new Point(c.X - r * 0.07, c.Y - r * 1.18));
            g.LineTo(new Point(c.X + r * 0.07, c.Y - r * 1.18));
            g.EndFigure(true);
        }

        ctx.DrawGeometry(accent, null, lubber);
        var plane = new Pen(text, Math.Max(2, r * 0.03), lineCap: PenLineCap.Round);
        ctx.DrawLine(plane, new Point(c.X, c.Y - r * 0.40), new Point(c.X, c.Y + r * 0.02));
        ctx.DrawLine(plane, new Point(c.X - r * 0.22, c.Y - r * 0.20), new Point(c.X + r * 0.22, c.Y - r * 0.20));
        ctx.DrawLine(plane, new Point(c.X - r * 0.09, c.Y), new Point(c.X + r * 0.09, c.Y));

        // heading readout inside the card, clear of the cardinal letters
        var hdg = (heading % 360 + 360) % 360;
        var big = Text(hdg.ToString("000", CultureInfo.InvariantCulture) + "°", Math.Max(11, r * 0.2), text, Face700);
        var top = c.Y + r * 0.12;
        var box = new Rect(c.X - big.Width / 2 - 6, top, big.Width + 12, big.Height + 2);
        ctx.DrawRectangle(Face, new Pen(track, 1), box, 4, 4);
        ctx.DrawText(big, new Point(c.X - big.Width / 2, top + 1));
    }

    private static void Needle(DrawingContext ctx, Point c, double length, double angle, IBrush brush, double width)
    {
        var g = new StreamGeometry();
        using (var s = g.Open())
        {
            s.BeginFigure(Polar(c, length, angle), true);
            s.LineTo(Polar(c, width, angle + 90));
            s.LineTo(Polar(c, length * 0.14, angle + 180));
            s.LineTo(Polar(c, width, angle - 90));
            s.EndFigure(true);
        }

        ctx.DrawGeometry(brush, null, g);
    }

    /// <summary>A point at a distance and an angle (degrees clockwise from 12 o'clock).</summary>
    private static Point Polar(Point c, double r, double deg)
    {
        var a = deg * Math.PI / 180;
        return new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));
    }

    private static StreamGeometry Arc(Point c, double r, double from, double to)
    {
        var g = new StreamGeometry();
        using var s = g.Open();
        s.BeginFigure(Polar(c, r, from), false);
        s.ArcTo(Polar(c, r, to), new Size(r, r), 0, to - from > 180, SweepDirection.Clockwise);
        s.EndFigure(false);
        return g;
    }

    /// <summary>A 1-2-5 step near <paramref name="rough"/>.</summary>
    private static double Step(double rough)
    {
        if (rough <= 0 || double.IsNaN(rough))
            return 1;
        var decade = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        var m = rough / decade;
        return (m < 1.5 ? 1 : m < 3.5 ? 2 : m < 7.5 ? 5 : 10) * decade;
    }

    private static string Format(double v, bool precise = false)
    {
        if (double.IsNaN(v))
            return "-";
        var a = Math.Abs(v);
        var format = precise ? (a >= 100 ? "0" : "0.0") : (a >= 1000 ? "0" : a % 1 == 0 ? "0" : "0.#");
        if (!precise && a >= 1000)
            return (v / 1000).ToString("0.#", CultureInfo.InvariantCulture) + "k";
        return v.ToString(format, CultureInfo.InvariantCulture);
    }

    private static FormattedText Text(string s, double size, IBrush brush, Typeface face) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush);
}
