using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;
using MissionPlanner.App.Core.ViewModels.SetupScreens.MotorSetup;

namespace MissionPlanner.App.Controls.SetupScreens;

/// <summary>
/// Top view of a multirotor frame for the motor setup card: nose up, arms, each motor with its
/// number, test letter, rotation arrow and a caption (its output); the spinning motor glows, and
/// when <see cref="MotorDiagramState.Clickable"/> a click on a motor runs <see cref="ClickCommand"/>
/// with the motor number. Positions come from <see cref="MotorDiagramGeometry"/>, shared with the
/// click test. Colours are set from the theme's brushes in XAML.
/// </summary>
public sealed class MotorDiagram : Control
{
    public static readonly StyledProperty<MotorDiagramState?> StateProperty =
        AvaloniaProperty.Register<MotorDiagram, MotorDiagramState?>(nameof(State));

    public static readonly StyledProperty<ICommand?> ClickCommandProperty =
        AvaloniaProperty.Register<MotorDiagram, ICommand?>(nameof(ClickCommand));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<MotorDiagram, IBrush?>(nameof(Foreground), Brushes.White);

    public static readonly StyledProperty<IBrush?> MutedProperty =
        AvaloniaProperty.Register<MotorDiagram, IBrush?>(nameof(Muted), Brushes.Gray);

    public static readonly StyledProperty<IBrush?> FrameProperty =
        AvaloniaProperty.Register<MotorDiagram, IBrush?>(nameof(Frame), Brushes.Gray);

    public static readonly StyledProperty<IBrush?> BodyProperty =
        AvaloniaProperty.Register<MotorDiagram, IBrush?>(nameof(Body), Brushes.DimGray);

    public static readonly StyledProperty<IBrush?> ClockwiseProperty =
        AvaloniaProperty.Register<MotorDiagram, IBrush?>(nameof(Clockwise), Brushes.YellowGreen);

    public static readonly StyledProperty<IBrush?> CounterClockwiseProperty =
        AvaloniaProperty.Register<MotorDiagram, IBrush?>(nameof(CounterClockwise), Brushes.SteelBlue);

    public static readonly StyledProperty<IBrush?> HighlightProperty =
        AvaloniaProperty.Register<MotorDiagram, IBrush?>(nameof(Highlight), Brushes.Orange);

    public static readonly StyledProperty<IBrush?> DangerProperty =
        AvaloniaProperty.Register<MotorDiagram, IBrush?>(nameof(Danger), Brushes.IndianRed);

    private static readonly Typeface Bold = new(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);
    private static readonly Typeface Semi = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    private MotorDiagramGeometry _geometry = MotorDiagramGeometry.Empty;
    private int? _hover;

    static MotorDiagram()
    {
        AffectsRender<MotorDiagram>(StateProperty, ForegroundProperty, MutedProperty, FrameProperty, BodyProperty,
            ClockwiseProperty, CounterClockwiseProperty, HighlightProperty, DangerProperty);
    }

    public MotorDiagramState? State
    {
        get => GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public ICommand? ClickCommand
    {
        get => GetValue(ClickCommandProperty);
        set => SetValue(ClickCommandProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IBrush? Muted
    {
        get => GetValue(MutedProperty);
        set => SetValue(MutedProperty, value);
    }

    public IBrush? Frame
    {
        get => GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    public IBrush? Body
    {
        get => GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    public IBrush? Clockwise
    {
        get => GetValue(ClockwiseProperty);
        set => SetValue(ClockwiseProperty, value);
    }

    public IBrush? CounterClockwise
    {
        get => GetValue(CounterClockwiseProperty);
        set => SetValue(CounterClockwiseProperty, value);
    }

    public IBrush? Highlight
    {
        get => GetValue(HighlightProperty);
        set => SetValue(HighlightProperty, value);
    }

    public IBrush? Danger
    {
        get => GetValue(DangerProperty);
        set => SetValue(DangerProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StateProperty)
        {
            _hover = null;
            UpdateCursor();
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (State is not { Clickable: true })
            return;
        var p = e.GetPosition(this);
        var hit = _geometry.HitTest(p.X, p.Y);
        if (hit != _hover)
        {
            _hover = hit;
            UpdateCursor();
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover != null)
        {
            _hover = null;
            UpdateCursor();
            InvalidateVisual();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (State is not { Clickable: true } || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        var p = e.GetPosition(this);
        if (_geometry.HitTest(p.X, p.Y) is { } motor && ClickCommand is { } cmd && cmd.CanExecute(motor))
        {
            cmd.Execute(motor);
            e.Handled = true;
        }
    }

    private void UpdateCursor() =>
        Cursor = State is { Clickable: true } && _hover != null ? new Cursor(StandardCursorType.Hand) : Cursor.Default;

    public override void Render(DrawingContext ctx)
    {
        var state = State;
        var layout = state?.Layout;
        var w = Bounds.Width;
        var h = Bounds.Height;
        // room for the banner line is always kept, so the drawing does not jump when it appears
        const double bannerRoom = 24;
        _geometry = MotorDiagramGeometry.Compute(layout, w, h - bannerRoom, 40);
        if (state == null || layout == null || _geometry.Spots.Count == 0)
            return;

        var size = Math.Min(w, h - bannerRoom);
        var c = new Point(_geometry.CenterX, _geometry.CenterY);
        var frame = Frame ?? Brushes.Gray;
        var armPen = new Pen(frame, Math.Max(3, size * 0.03), lineCap: PenLineCap.Round);

        // arms first, then the body over them
        foreach (var arm in _geometry.Spots.Select(s => (s.ArmX, s.ArmY)).Distinct())
            ctx.DrawLine(armPen, c, new Point(arm.ArmX, arm.ArmY));
        foreach (var s in _geometry.Spots.Where(s => s.X != s.ArmX || s.Y != s.ArmY))
            ctx.DrawLine(new Pen(frame, Math.Max(2, size * 0.015)), new Point(s.ArmX, s.ArmY), new Point(s.X, s.Y));

        var bodyR = _geometry.BodyRadius;
        ctx.DrawRectangle(Body, new Pen(frame, 1.5), new Rect(c.X - bodyR, c.Y - bodyR * 1.2, bodyR * 2, bodyR * 2.4), bodyR * 0.45, bodyR * 0.45);
        DrawNose(ctx, c, bodyR);

        foreach (var spot in _geometry.Spots)
        {
            var motor = layout.Motors.First(m => m.Number == spot.Motor);
            DrawMotor(ctx, spot, motor, state.MarkOf(spot.Motor), c, state.Clickable && _hover == spot.Motor);
        }

        if (state.Banner is { } banner)
        {
            var t = Text(banner, 13, Foreground, Semi);
            ctx.DrawText(t, new Point((w - t.Width) / 2, h - bannerRoom + (bannerRoom - t.Height) / 2));
        }
    }

    private void DrawNose(DrawingContext ctx, Point c, double bodyR)
    {
        var accent = Clockwise ?? Brushes.YellowGreen;
        var g = new StreamGeometry();
        using (var s = g.Open())
        {
            s.BeginFigure(new Point(c.X, c.Y - bodyR * 0.95), true);
            s.LineTo(new Point(c.X + bodyR * 0.55, c.Y + bodyR * 0.2));
            s.LineTo(new Point(c.X, c.Y - bodyR * 0.1));
            s.LineTo(new Point(c.X - bodyR * 0.55, c.Y + bodyR * 0.2));
            s.EndFigure(true);
        }

        ctx.DrawGeometry(accent, null, g);
        var front = Text("FRONT", Math.Max(9, bodyR * 0.42), Muted, Bold);
        var box = new Rect(c.X - front.Width / 2 - 4, c.Y - bodyR * 1.2 - front.Height - 4, front.Width + 8, front.Height + 2);
        ctx.DrawRectangle(Body, null, box, 3, 3);
        ctx.DrawText(front, new Point(box.X + 4, box.Y + 1));
    }

    private void DrawMotor(DrawingContext ctx, MotorSpot spot, MotorInfo motor, MotorMark? mark, Point center, bool hover)
    {
        var at = new Point(spot.X, spot.Y);
        var r = spot.Radius;
        var rot = motor.IsClockwise ? Clockwise : motor.IsCounterClockwise ? CounterClockwise : Muted;
        var kind = mark?.Kind ?? MotorMarkKind.Normal;

        if (kind == MotorMarkKind.Spinning)
        {
            // glow around a spinning motor
            ctx.DrawEllipse(Tint(Highlight, 0.22), null, at, r * 1.5, r * 1.5);
            ctx.DrawEllipse(null, new Pen(Highlight, Math.Max(2, r * 0.12)), at, r * 1.45, r * 1.45);
        }

        var fill = Tint(rot, kind == MotorMarkKind.Placed ? 0.45 : 0.22);
        var ring = kind switch
        {
            MotorMarkKind.Spinning => new Pen(Highlight, Math.Max(2.5, r * 0.16)),
            MotorMarkKind.Open => new Pen(Foreground, Math.Max(1.5, r * 0.09), new DashStyle([3, 2], 0)),
            _ => new Pen(rot, Math.Max(2, r * 0.12)),
        };
        if (hover)
            ring = new Pen(Highlight, Math.Max(3, r * 0.18));
        ctx.DrawEllipse(fill, ring, at, r, r);
        DrawSpin(ctx, motor, at, r * 1.28, rot);

        var num = Text(motor.Number.ToString(CultureInfo.InvariantCulture), r * 0.9, Foreground, Bold);
        ctx.DrawText(num, new Point(at.X - num.Width / 2, at.Y - num.Height / 2));

        // the test letter outside the motor, away from the centre
        var ox = spot.ArmX - center.X;
        var oy = spot.ArmY - center.Y;
        var ol = Math.Sqrt(ox * ox + oy * oy);
        if (ol < 1e-6)
        {
            ox = spot.X - center.X;
            oy = 1;
            ol = Math.Sqrt(ox * ox + oy * oy);
        }

        var dir = new Point(ox / ol, oy / ol);

        // caption (output) in a pill under the motor
        Rect? captionBox = null;
        if (mark?.Caption is { Length: > 0 } caption)
        {
            var t = Text(caption, Math.Max(9.5, r * 0.42), Foreground, Semi);
            var box = new Rect(at.X - t.Width / 2 - 5, at.Y + r + 3, t.Width + 10, t.Height + 2);
            ctx.DrawRectangle(Tint(kind == MotorMarkKind.Placed ? Clockwise : Body, kind == MotorMarkKind.Placed ? 0.5 : 0.95),
                new Pen(kind == MotorMarkKind.Placed ? Clockwise : Frame, 1), box, 4, 4);
            ctx.DrawText(t, new Point(box.X + 5, box.Y + 1));
            captionBox = box;
        }

        // the test letter outside the motor, away from the centre (beside the caption on bottom motors)
        var letter = Text(motor.TestLetter, Math.Max(10, r * 0.62), Muted, Bold);
        var lp = new Point(at.X + dir.X * r * 1.75, at.Y + dir.Y * r * 1.75);
        var lr = new Rect(lp.X - letter.Width / 2, lp.Y - letter.Height / 2, letter.Width, letter.Height);
        if (captionBox is { } cb && lr.Intersects(cb))
            lr = new Rect(cb.Right + 5, cb.Y + (cb.Height - lr.Height) / 2, lr.Width, lr.Height);
        ctx.DrawText(letter, lr.TopLeft);

        var above = at.Y - r * 1.4 - 2;
        if (mark?.Reversed == true)
        {
            var rev = Text("REV", Math.Max(9, r * 0.38), Danger, Bold);
            ctx.DrawText(rev, new Point(at.X - rev.Width / 2, above - rev.Height));
            above -= rev.Height;
        }

        if (mark?.Badge is { Length: > 0 } badge)
        {
            var b = Text(badge, Math.Max(9.5, r * 0.42), Highlight, Bold);
            ctx.DrawText(b, new Point(at.X - b.Width / 2, above - b.Height));
        }
    }

    /// <summary>A 140° arc with an arrow head showing which way the propeller turns.</summary>
    private static void DrawSpin(DrawingContext ctx, MotorInfo motor, Point at, double radius, IBrush? brush)
    {
        if (!motor.HasRotation || brush == null)
            return;
        var cw = motor.IsClockwise;
        var pen = new Pen(brush, Math.Max(1.5, radius * 0.09), lineCap: PenLineCap.Round);
        var a0 = -160.0 * Math.PI / 180;
        var a1 = -20.0 * Math.PI / 180;
        var p0 = new Point(at.X + radius * Math.Cos(a0), at.Y + radius * Math.Sin(a0));
        var p1 = new Point(at.X + radius * Math.Cos(a1), at.Y + radius * Math.Sin(a1));
        var g = new StreamGeometry();
        using (var s = g.Open())
        {
            s.BeginFigure(p0, false);
            s.ArcTo(p1, new Size(radius, radius), 0, false, SweepDirection.Clockwise);
            s.EndFigure(false);
        }

        ctx.DrawGeometry(null, pen, g);

        var tip = cw ? p1 : p0;
        var ang = cw ? a1 + Math.PI / 2 : a0 - Math.PI / 2;
        var head = radius * 0.32;
        ctx.DrawLine(pen, tip, new Point(tip.X - head * Math.Cos(ang - 0.55), tip.Y - head * Math.Sin(ang - 0.55)));
        ctx.DrawLine(pen, tip, new Point(tip.X - head * Math.Cos(ang + 0.55), tip.Y - head * Math.Sin(ang + 0.55)));
    }

    private static IBrush? Tint(IBrush? brush, double opacity) =>
        brush is ISolidColorBrush s ? new SolidColorBrush(s.Color, opacity * s.Opacity) : brush;

    private static FormattedText Text(string s, double size, IBrush? brush, Typeface face) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, Math.Max(6, size), brush ?? Brushes.White);
}
