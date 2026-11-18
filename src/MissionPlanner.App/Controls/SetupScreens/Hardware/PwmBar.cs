using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MissionPlanner.App.Controls.SetupScreens.Hardware;

/// <summary>A horizontal PWM bar (800-2200 by default) with the value written on it.</summary>
public sealed class PwmBar : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<PwmBar, double>(nameof(Value));

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<PwmBar, double>(nameof(Minimum), 800);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<PwmBar, double>(nameof(Maximum), 2200);

    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<PwmBar, string?>(nameof(Label));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<PwmBar, IBrush?>(nameof(Fill));

    private static readonly Typeface Face = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly IBrush Track = new SolidColorBrush(Color.Parse("#252b33"));
    private static readonly IBrush DefaultFill = new SolidColorBrush(Color.Parse("#8cc63f"));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#e6e9ed"));
    private static readonly IBrush MutedText = new SolidColorBrush(Color.Parse("#6b7480"));
    private static readonly IBrush Shade =new SolidColorBrush(Color.FromArgb(140, 0, 0, 0));
    private static readonly IPen CenterPen = new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1);

    static PwmBar()
    {
        AffectsRender<PwmBar>(ValueProperty, MinimumProperty, MaximumProperty, LabelProperty, FillProperty);
    }

    public PwmBar()
    {
        Height = 20;
        MinWidth = 60;
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

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 4 || h < 4)
            return;

        var rect = new Rect(0, 0, w, h);
        ctx.DrawRectangle(Track, null, rect, 4, 4);

        var span = Maximum - Minimum;
        var v = Value;
        if (span > 0 && v > 0)
        {
            var frac = Math.Clamp((v - Minimum) / span, 0, 1);
            if (frac > 0)
                ctx.DrawRectangle(Fill ?? DefaultFill, null, new Rect(0, 0, Math.Max(4, w * frac), h), 4, 4);
        }

        var size = Math.Min(12, h - 6);
        if (v <= 0)
        {
            // no output on this channel
            var none = new FormattedText("no output", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, MutedText);
            ctx.DrawText(none, new Point((w - none.Width) / 2, (h - none.Height) / 2));
            return;
        }

        // 1500 µs mark
        if (span > 0 && 1500 > Minimum && 1500 < Maximum)
        {
            var x = Math.Round(w * (1500 - Minimum) / span) + 0.5;
            ctx.DrawLine(CenterPen, new Point(x, 2), new Point(x, h - 2));
        }

        var text = v.ToString("0", CultureInfo.InvariantCulture);
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, TextBrush);
        var shade = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, Shade);
        var origin = new Point((w - ft.Width) / 2, (h - ft.Height) / 2);
        ctx.DrawText(shade, origin + new Point(0.8, 0.8));
        ctx.DrawText(ft, origin);

        if (!string.IsNullOrEmpty(Label))
        {
            var lt = new FormattedText(Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, TextBrush);
            ctx.DrawText(lt, new Point(6, (h - lt.Height) / 2));
        }
    }
}
