using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MissionPlanner.App.Controls.SetupScreens;

/// <summary>
/// A simple drawing of the orientation the accelerometer calibration asks for
/// (ACCELCAL_VEHICLE_POS: 1 LEVEL, 2 LEFT, 3 RIGHT, 4 NOSEDOWN, 5 NOSEUP, 6 BACK): a multicopter
/// resting on the ground, seen from the side (nose marked) for level / nose down / nose up, and
/// from behind (left and right marked) for the side and back positions. 0 draws a dimmed level vehicle.
/// </summary>
public sealed class VehicleOrientationGlyph : Control
{
    public static readonly StyledProperty<int> PositionProperty = AvaloniaProperty.Register<VehicleOrientationGlyph, int>(nameof(Position));

    // design space; scaled to fit the control
    private const double DesignW = 260;
    private const double DesignH = 200;
    private const double GroundY = 168;

    private static readonly IBrush BodyFill = new SolidColorBrush(Color.Parse("#3a4350"));
    private static readonly IPen BodyPen = new Pen(new SolidColorBrush(Color.Parse("#aeb7c2")), 1.6, lineJoin: PenLineJoin.Round);
    private static readonly IBrush ArmFill = new SolidColorBrush(Color.Parse("#5d6876"));
    private static readonly IBrush PropFill = new SolidColorBrush(Color.Parse("#d5dbe2"));
    private static readonly IPen LegPen = new Pen(new SolidColorBrush(Color.Parse("#aeb7c2")), 2.4, lineCap: PenLineCap.Round);
    private static readonly IBrush Nose = new SolidColorBrush(Color.Parse("#4fa3e3"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#8cc63f"));
    private static readonly IPen GroundPen = new Pen(new SolidColorBrush(Color.Parse("#9aa4b0")), 2, lineCap: PenLineCap.Round);
    private static readonly IPen HatchPen = new Pen(new SolidColorBrush(Color.Parse("#4a525c")), 1.2);
    private static readonly IBrush LetterBg = new SolidColorBrush(Color.Parse("#101317"));
    private static readonly IBrush LetterFg = new SolidColorBrush(Color.Parse("#e6e9ed"));
    private static readonly Typeface Face = new(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);

    // local extents of the two drawings (origin at the body centre, +x front or right, +y down)
    private static readonly Rect SideExtent = new(-76, -24, 152, 51);
    private static readonly Rect RearExtent = new(-76, -24, 152, 51);

    static VehicleOrientationGlyph()
    {
        AffectsRender<VehicleOrientationGlyph>(PositionProperty);
    }

    public int Position
    {
        get => GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(DesignW, DesignH);

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 10 || h < 10)
            return;

        var scale = Math.Min(w / DesignW, h / DesignH);
        var fit = Matrix.CreateScale(scale, scale) *
                  Matrix.CreateTranslation((w - DesignW * scale) / 2, (h - DesignH * scale) / 2);

        var (rear, degrees) = Position switch
        {
            2 => (true, -90.0), // LEFT side down: seen from behind, the top turns to the left
            3 => (true, 90.0), // RIGHT
            4 => (false, 90.0), // NOSEDOWN
            5 => (false, -90.0), // NOSEUP
            6 => (true, 180.0), // BACK
            _ => (false, 0.0), // LEVEL (and idle)
        };

        using (ctx.PushTransform(fit))
        {
            DrawGround(ctx);

            var rotation = Matrix.CreateRotation(degrees * Math.PI / 180);
            var extent = (rear ? RearExtent : SideExtent).TransformToAABB(rotation);
            var place = Matrix.CreateTranslation(DesignW / 2 - extent.Center.X, GroundY - 2 - extent.Bottom);
            var toDesign = rotation * place;

            using (ctx.PushOpacity(Position == 0 ? 0.45 : 1))
            {
                using (ctx.PushTransform(toDesign))
                {
                    if (rear)
                        DrawRear(ctx);
                    else
                        DrawSide(ctx);
                }

                // side letters stay upright wherever the drawing turned them
                if (rear)
                {
                    DrawLetter(ctx, "L", new Point(-56, -4).Transform(toDesign));
                    DrawLetter(ctx, "R", new Point(56, -4).Transform(toDesign));
                }
            }
        }
    }

    private static void DrawGround(DrawingContext ctx)
    {
        ctx.DrawLine(GroundPen, new Point(24, GroundY), new Point(DesignW - 24, GroundY));
        for (var x = 30.0; x < DesignW - 24; x += 12)
            ctx.DrawLine(HatchPen, new Point(x, GroundY + 2), new Point(x - 8, GroundY + 10));
    }

    /// <summary>Side view, nose to +x.</summary>
    private static void DrawSide(DrawingContext ctx)
    {
        DrawGear(ctx);
        ctx.DrawRectangle(ArmFill, null, new Rect(-56, -13, 112, 4), 1, 1);
        DrawMotor(ctx, -56);
        DrawMotor(ctx, 56);
        ctx.DrawRectangle(BodyFill, BodyPen, new Rect(-36, -9, 72, 18), 5, 5);

        // the nose
        var nose = new StreamGeometry();
        using (var g = nose.Open())
        {
            g.BeginFigure(new Point(30, -9), true);
            g.LineTo(new Point(50, 0));
            g.LineTo(new Point(30, 9));
            g.EndFigure(true);
        }

        ctx.DrawGeometry(Nose, null, nose);
    }

    /// <summary>Seen from behind: +x is the vehicle's right.</summary>
    private static void DrawRear(DrawingContext ctx)
    {
        DrawGear(ctx);
        ctx.DrawRectangle(ArmFill, null, new Rect(-56, -14, 112, 4), 1, 1);
        DrawMotor(ctx, -56);
        DrawMotor(ctx, 56);
        ctx.DrawRectangle(BodyFill, BodyPen, new Rect(-22, -11, 44, 22), 6, 6);

        // a lit tail marker so the back is recognisable
        ctx.DrawEllipse(Accent, null, new Point(0, 0), 4, 4);
    }

    private static void DrawMotor(DrawingContext ctx, double x)
    {
        ctx.DrawRectangle(BodyFill, BodyPen, new Rect(x - 5, -20, 10, 8), 2, 2);
        ctx.DrawRectangle(PropFill, null, new Rect(x - 20, -24, 40, 3.2), 1.6, 1.6);
    }

    private static void DrawGear(DrawingContext ctx)
    {
        ctx.DrawLine(LegPen, new Point(-20, 8), new Point(-30, 25));
        ctx.DrawLine(LegPen, new Point(20, 8), new Point(30, 25));
        ctx.DrawLine(LegPen, new Point(-40, 25), new Point(-20, 25));
        ctx.DrawLine(LegPen, new Point(20, 25), new Point(40, 25));
    }

    private static void DrawLetter(DrawingContext ctx, string letter, Point at)
    {
        var ft = new FormattedText(letter, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 12, LetterFg);
        ctx.DrawEllipse(LetterBg, BodyPen, at, 9, 9);
        ctx.DrawText(ft, new Point(at.X - ft.Width / 2, at.Y - ft.Height / 2));
    }
}
