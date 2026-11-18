using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MissionPlanner.App.Controls.SetupScreens;

/// <summary>
/// An RC input bar like Mission Planner's HorizontalProgressBar2/VerticalProgressBar2: the PWM
/// value over 800-2200 µs, with red min/max markers while calibrating. A reversed channel fills
/// from the other end. Drawn directly; cheap to update at the page's 20 Hz.
/// </summary>
public sealed class RcBar : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<RcBar, double>(nameof(Value));
    public static readonly StyledProperty<double> MinimumProperty = AvaloniaProperty.Register<RcBar, double>(nameof(Minimum), 800);
    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<RcBar, double>(nameof(Maximum), 2200);
    public static readonly StyledProperty<double> MinLineProperty = AvaloniaProperty.Register<RcBar, double>(nameof(MinLine));
    public static readonly StyledProperty<double> MaxLineProperty = AvaloniaProperty.Register<RcBar, double>(nameof(MaxLine));
    public static readonly StyledProperty<bool> ShowRangeProperty = AvaloniaProperty.Register<RcBar, bool>(nameof(ShowRange));
    public static readonly StyledProperty<bool> ReversedProperty = AvaloniaProperty.Register<RcBar, bool>(nameof(Reversed));
    public static readonly StyledProperty<Orientation> OrientationProperty =
        AvaloniaProperty.Register<RcBar, Orientation>(nameof(Orientation), Orientation.Horizontal);
    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<RcBar, string?>(nameof(Label));

    private static readonly IBrush Track = new SolidColorBrush(Color.Parse("#252b33"));
    private static readonly IPen TrackPen = new Pen(new SolidColorBrush(Color.Parse("#2c333c")), 1);
    private static readonly IBrush Fill = new SolidColorBrush(Color.Parse("#8cc63f"));
    private static readonly IBrush FillReversed = new SolidColorBrush(Color.Parse("#4fa3e3"));
    private static readonly IPen RangePen = new Pen(new SolidColorBrush(Color.Parse("#ff5a52")), 2);
    private static readonly IBrush RangeShade = new SolidColorBrush(Color.FromArgb(40, 255, 90, 82));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#e6e9ed"));
    private static readonly IBrush TextShadow = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0));
    private static readonly IPen CentrePen = new Pen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), 1, new DashStyle([2, 3], 0));
    private static readonly Typeface Face = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    static RcBar()
    {
        AffectsRender<RcBar>(ValueProperty, MinimumProperty, MaximumProperty, MinLineProperty, MaxLineProperty,
            ShowRangeProperty, ReversedProperty, OrientationProperty, LabelProperty);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double MinLine
    {
        get => GetValue(MinLineProperty);
        set => SetValue(MinLineProperty, value);
    }

    public double MaxLine
    {
        get => GetValue(MaxLineProperty);
        set => SetValue(MaxLineProperty, value);
    }

    public bool ShowRange
    {
        get => GetValue(ShowRangeProperty);
        set => SetValue(ShowRangeProperty, value);
    }

    public bool Reversed
    {
        get => GetValue(ReversedProperty);
        set => SetValue(ReversedProperty, value);
    }

    public Orientation Orientation
    {
        get => GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    /// <summary>Drawn inside the bar on the left (horizontal) or at the top (vertical).</summary>
    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        Orientation == Orientation.Horizontal ? new Size(200, 26) : new Size(56, 220);

    /// <summary>0..1 along the bar for a PWM value (0 = left/bottom).</summary>
    private double Fraction(double v)
    {
        var span = Maximum - Minimum;
        return span <= 0 ? 0 : Math.Clamp((v - Minimum) / span, 0, 1);
    }

    public override void Render(DrawingContext ctx)
    {
        var b = new Rect(Bounds.Size).Deflate(0.5);
        if (b.Width <= 2 || b.Height <= 2)
            return;

        var horizontal = Orientation == Orientation.Horizontal;
        ctx.DrawRectangle(Track, TrackPen, b, 4, 4);

        // value: from the low end, or from the high end when reversed
        var f = Value > 0 ? Fraction(Value) : 0;
        if (f > 0)
        {
            var len = (horizontal ? b.Width : b.Height) * (Reversed ? 1 - f : f);
            Rect fill = horizontal
                ? Reversed ? new Rect(b.Right - len, b.Y, len, b.Height) : new Rect(b.X, b.Y, len, b.Height)
                : Reversed ? new Rect(b.X, b.Y, b.Width, len) : new Rect(b.X, b.Bottom - len, b.Width, len);
            using (ctx.PushClip(new RoundedRect(b, 4)))
                ctx.DrawRectangle(Reversed ? FillReversed : Fill, null, fill);
        }

        // 1500 µs centre
        var c = Fraction(1500);
        if (horizontal)
            ctx.DrawLine(CentrePen, new Point(b.X + b.Width * c, b.Y + 2), new Point(b.X + b.Width * c, b.Bottom - 2));
        else
            ctx.DrawLine(CentrePen, new Point(b.X + 2, b.Bottom - b.Height * c), new Point(b.Right - 2, b.Bottom - b.Height * c));

        if (ShowRange && MinLine > 0 && MaxLine >= MinLine)
        {
            var lo = Fraction(MinLine);
            var hi = Fraction(MaxLine);
            if (horizontal)
            {
                var x0 = b.X + b.Width * lo;
                var x1 = b.X + b.Width * hi;
                ctx.DrawRectangle(RangeShade, null, new Rect(x0, b.Y, Math.Max(0, x1 - x0), b.Height));
                ctx.DrawLine(RangePen, new Point(x0, b.Y), new Point(x0, b.Bottom));
                ctx.DrawLine(RangePen, new Point(x1, b.Y), new Point(x1, b.Bottom));
            }
            else
            {
                var y0 = b.Bottom - b.Height * lo;
                var y1 = b.Bottom - b.Height * hi;
                ctx.DrawRectangle(RangeShade, null, new Rect(b.X, y1, b.Width, Math.Max(0, y0 - y1)));
                ctx.DrawLine(RangePen, new Point(b.X, y0), new Point(b.Right, y0));
                ctx.DrawLine(RangePen, new Point(b.X, y1), new Point(b.Right, y1));
            }
        }

        var value = Value.ToString("0", CultureInfo.InvariantCulture);
        if (horizontal)
        {
            var size = Math.Clamp(b.Height * 0.5, 10, 16);
            if (!string.IsNullOrEmpty(Label))
                DrawText(ctx, Label, size, new Point(b.X + 8, b.Center.Y), alignRight: false, centre: false);
            DrawText(ctx, value, size, new Point(b.Right - 8, b.Center.Y), alignRight: true, centre: false);
        }
        else
        {
            var size = Math.Clamp(b.Width * 0.28, 10, 15);
            DrawText(ctx, value, size, new Point(b.Center.X, b.Y + 14), alignRight: false, centre: true);
        }
    }

    private static void DrawText(DrawingContext ctx, string text, double size, Point anchor, bool alignRight, bool centre)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, TextBrush);
        var x = alignRight ? anchor.X - ft.Width : centre ? anchor.X - ft.Width / 2 : anchor.X;
        var y = anchor.Y - ft.Height / 2;
        var shadow = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, TextShadow);
        ctx.DrawText(shadow, new Point(x + 1, y + 1));
        ctx.DrawText(ft, new Point(x, y));
    }
}
