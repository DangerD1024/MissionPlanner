using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;

namespace MissionPlanner.App.Controls.SetupScreens.Hardware;

/// <summary>
/// Draws a frame's motors from APMotorLayout.json, front up: arms, motors coloured by spin
/// direction (green clockwise, blue counter-clockwise) and, with <see cref="ShowLabels"/>, the motor
/// number in each motor and its test letter beside it.
/// </summary>
public sealed class MotorLayoutView : Control
{
    public static readonly StyledProperty<MotorLayout?> LayoutProperty =
        AvaloniaProperty.Register<MotorLayoutView, MotorLayout?>(nameof(Layout));

    public static readonly StyledProperty<bool> ShowLabelsProperty =
        AvaloniaProperty.Register<MotorLayoutView, bool>(nameof(ShowLabels));

    public static readonly StyledProperty<bool> IsDimmedProperty =
        AvaloniaProperty.Register<MotorLayoutView, bool>(nameof(IsDimmed));

    private static readonly Typeface Face = new(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);
    private static readonly Color Cw = Color.Parse("#8cc63f");
    private static readonly Color Ccw = Color.Parse("#4fa3e3");
    private static readonly Color Unknown = Color.Parse("#9aa4b0");
    private static readonly Color ArmColor = Color.Parse("#5b6572");
    private static readonly Color BodyColor = Color.Parse("#3a424d");
    private static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#0f1a05"));
    private static readonly IBrush Light = new SolidColorBrush(Color.Parse("#e6e9ed"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#9aa4b0"));

    static MotorLayoutView()
    {
        AffectsRender<MotorLayoutView>(LayoutProperty, ShowLabelsProperty, IsDimmedProperty);
    }

    public MotorLayout? Layout
    {
        get => GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    public bool ShowLabels
    {
        get => GetValue(ShowLabelsProperty);
        set => SetValue(ShowLabelsProperty, value);
    }

    public bool IsDimmed
    {
        get => GetValue(IsDimmedProperty);
        set => SetValue(IsDimmedProperty, value);
    }

    public override void Render(DrawingContext ctx)
    {
        var layout = Layout;
        var w = Bounds.Width;
        var h = Bounds.Height;
        var size = Math.Min(w, h);
        if (layout == null || layout.Motors.Count == 0 || size < 20)
            return;

        var alpha = IsDimmed ? (byte)110 : (byte)255;
        var c = new Point(w / 2, h / 2);
        var reach = Math.Max(0.05, layout.Motors.Max(m => Math.Sqrt(m.X * m.X + m.Y * m.Y)));
        var motorR = Math.Clamp(size * (layout.Motors.Count > 8 ? 0.075 : 0.1), 5, 30);
        var labelRoom = ShowLabels ? motorR * 1.2 : 0;
        var scale = (size / 2 - motorR - labelRoom - 2) / reach;

        var armPen = new Pen(new SolidColorBrush(ArmColor, alpha / 255.0), Math.Max(2, size * 0.025), lineCap: PenLineCap.Round);
        var body = new SolidColorBrush(BodyColor, alpha / 255.0);

        // coaxial pairs share a position: draw them side by side across the arm
        var positions = new List<(MotorInfo Motor, Point At)>();
        foreach (var group in layout.Motors.GroupBy(m => (Math.Round(m.X, 2), Math.Round(m.Y, 2))))
        {
            var list = group.ToList();
            var at = new Point(c.X + list[0].X * scale, c.Y + list[0].Y * scale);
            ctx.DrawLine(armPen, c, at);
            if (list.Count == 1)
            {
                positions.Add((list[0], at));
                continue;
            }

            var dx = at.X - c.X;
            var dy = at.Y - c.Y;
            var len = Math.Max(1e-6, Math.Sqrt(dx * dx + dy * dy));
            var perp = new Point(-dy / len, dx / len);
            for (var i = 0; i < list.Count; i++)
            {
                var off = (i - (list.Count - 1) / 2.0) * motorR * 1.15;
                positions.Add((list[i], new Point(at.X + perp.X * off, at.Y + perp.Y * off)));
            }
        }

        var bodyR = Math.Max(4, size * 0.09);
        ctx.DrawRectangle(body, null, new Rect(c.X - bodyR, c.Y - bodyR, bodyR * 2, bodyR * 2), bodyR * 0.4, bodyR * 0.4);
        DrawFrontArrow(ctx, c, bodyR, alpha);

        var r = positions.Count > 1 && layout.Motors.GroupBy(m => (Math.Round(m.X, 2), Math.Round(m.Y, 2))).Any(g => g.Count() > 1)
            ? motorR * 0.62
            : motorR;
        foreach (var (motor, at) in positions)
        {
            var color = motor.IsClockwise ? Cw : motor.IsCounterClockwise ? Ccw : Unknown;
            var fill = new SolidColorBrush(color, (IsDimmed ? 0.35 : 0.9));
            var ring = new Pen(new SolidColorBrush(color, alpha / 255.0), Math.Max(1.5, r * 0.12));
            ctx.DrawEllipse(fill, ring, at, r, r);
            DrawSpin(ctx, motor, at, r * 1.35, alpha);

            if (!ShowLabels)
                continue;

            var num = Text(motor.Number.ToString(CultureInfo.InvariantCulture), r * 0.95, Ink);
            ctx.DrawText(num, new Point(at.X - num.Width / 2, at.Y - num.Height / 2));

            // the test letter sits outside the motor, away from the centre
            var ox = at.X - c.X;
            var oy = at.Y - c.Y;
            var ol = Math.Max(1e-6, Math.Sqrt(ox * ox + oy * oy));
            var lp = new Point(at.X + ox / ol * r * 1.9, at.Y + oy / ol * r * 1.9);
            var letter = Text(motor.TestLetter, r * 0.8, Light);
            ctx.DrawText(letter, new Point(lp.X - letter.Width / 2, lp.Y - letter.Height / 2));
        }
    }

    private static FormattedText Text(string s, double size, IBrush brush) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, Math.Max(6, size), brush);

    private static void DrawFrontArrow(DrawingContext ctx, Point c, double bodyR, byte alpha)
    {
        var g = new StreamGeometry();
        using (var s = g.Open())
        {
            s.BeginFigure(new Point(c.X, c.Y - bodyR * 0.75), true);
            s.LineTo(new Point(c.X + bodyR * 0.5, c.Y + bodyR * 0.35));
            s.LineTo(new Point(c.X - bodyR * 0.5, c.Y + bodyR * 0.35));
            s.EndFigure(true);
        }

        ctx.DrawGeometry(new SolidColorBrush(Colors.White, alpha / 255.0 * 0.85), null, g);
    }

    /// <summary>A short arc with an arrow head showing the propeller's direction.</summary>
    private static void DrawSpin(DrawingContext ctx, MotorInfo motor, Point at, double radius, byte alpha)
    {
        if (!motor.HasRotation)
            return;
        var cw = motor.IsClockwise;
        var color = cw ? Cw : Ccw;
        var pen = new Pen(new SolidColorBrush(color, alpha / 255.0 * 0.9), Math.Max(1, radius * 0.08), lineCap: PenLineCap.Round);

        // arc over the top of the motor, 120° wide
        var a0 = -150.0 * Math.PI / 180;
        var a1 = -30.0 * Math.PI / 180;
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

        // arrow head at the end the propeller moves towards
        var tip = cw ? p1 : p0;
        var ang = cw ? a1 + Math.PI / 2 : a0 - Math.PI / 2;
        var head = radius * 0.35;
        var left = new Point(tip.X - head * Math.Cos(ang - 0.5), tip.Y - head * Math.Sin(ang - 0.5));
        var right = new Point(tip.X - head * Math.Cos(ang + 0.5), tip.Y - head * Math.Sin(ang + 0.5));
        ctx.DrawLine(pen, tip, left);
        ctx.DrawLine(pen, tip, right);
    }
}
