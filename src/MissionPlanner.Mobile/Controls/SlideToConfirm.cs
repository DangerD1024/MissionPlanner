using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace MissionPlanner.Mobile.Controls;

/// <summary>
/// "Slide to arm": a thumb dragged along a track to the end confirms (as QGroundControl's and DJI's
/// guarded actions). A tap or a short slide does nothing; let go early and it springs back.
/// </summary>
public sealed class SlideToConfirm : Control
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<SlideToConfirm, string>(nameof(Text), "Slide to confirm");

    public static readonly StyledProperty<IBrush?> AccentProperty =
        AvaloniaProperty.Register<SlideToConfirm, IBrush?>(nameof(Accent), new SolidColorBrush(Color.Parse("#3AA0FF")));

    /// <summary>How far (0..1) the thumb must go to confirm.</summary>
    public const double Threshold = 0.9;

    private static readonly IBrush Track = new SolidColorBrush(Color.Parse("#1E242D"));
    private static readonly IBrush Label = new SolidColorBrush(Color.Parse("#C9D1DB"));
    private static readonly IPen Arrow = new Pen(new SolidColorBrush(Color.Parse("#06121F")), 2.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

    private double _progress;
    private double? _dragFrom;
    private double _dragStartProgress;
    private DispatcherTimer? _spring;

    static SlideToConfirm()
    {
        AffectsRender<SlideToConfirm>(TextProperty, AccentProperty);
        HeightProperty.OverrideDefaultValue<SlideToConfirm>(60);
    }

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public IBrush? Accent
    {
        get => GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    /// <summary>0 at rest, 1 at the end.</summary>
    public double Progress => _progress;

    public event EventHandler? Confirmed;

    /// <summary>Back to the start (when the sheet is shown again).</summary>
    public void Reset()
    {
        _spring?.Stop();
        _progress = 0;
        _dragFrom = null;
        InvalidateVisual();
    }

    private double Travel => Math.Max(1, Bounds.Width - Bounds.Height);

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= h)
            return;
        var r = h / 2;
        var accent = Accent ?? Brushes.DodgerBlue;
        ctx.DrawRectangle(Track, null, new RoundedRect(new Rect(0, 0, w, h), r));

        // the filled part behind the thumb
        var x = _progress * Travel;
        if (x > 1)
        {
            using (ctx.PushOpacity(0.35))
                ctx.DrawRectangle(accent, null, new RoundedRect(new Rect(0, 0, x + h, h), r));
        }

        var text = new FormattedText(Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), 16, Label);
        using (ctx.PushOpacity(Math.Clamp(1 - _progress * 1.6, 0, 1)))
            ctx.DrawText(text, new Point(h + (w - h - text.Width) / 2, (h - text.Height) / 2));

        // the thumb: a circle with a chevron
        var c = new Point(x + r, r);
        ctx.DrawEllipse(accent, null, c, r - 4, r - 4);
        var a = r * 0.28;
        var g = new StreamGeometry();
        using (var s = g.Open())
        {
            s.BeginFigure(new Point(c.X - a * 0.6, c.Y - a), false);
            s.LineTo(new Point(c.X + a * 0.6, c.Y));
            s.LineTo(new Point(c.X - a * 0.6, c.Y + a));
            s.EndFigure(false);
        }

        ctx.DrawGeometry(null, Arrow, g);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _spring?.Stop();
        _dragFrom = e.GetPosition(this).X;
        _dragStartProgress = _progress;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragFrom is not { } from)
            return;
        _progress = Math.Clamp(_dragStartProgress + (e.GetPosition(this).X - from) / Travel, 0, 1);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragFrom == null)
            return;
        _dragFrom = null;
        e.Pointer.Capture(null);
        e.Handled = true;
        if (_progress >= Threshold)
        {
            _progress = 1;
            InvalidateVisual();
            Confirmed?.Invoke(this, EventArgs.Empty);
            return;
        }

        SpringBack();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_dragFrom != null)
        {
            _dragFrom = null;
            SpringBack();
        }
    }

    private void SpringBack()
    {
        _spring?.Stop();
        _spring = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
        {
            _progress = Math.Max(0, _progress - 0.08);
            InvalidateVisual();
            if (_progress <= 0)
                _spring?.Stop();
        });
        _spring.Start();
    }
}
