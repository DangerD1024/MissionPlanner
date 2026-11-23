using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MissionPlanner.Mobile.Controls;

/// <summary>
/// A line icon drawn from a 24×24 design grid, scaled to fit and always centred (a Path with
/// Stretch moves glyphs that do not fill the grid, such as a minus, to its top-left corner).
/// The stroke stays the same width at any size.
/// </summary>
public sealed class Icon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<Icon, IBrush?>(nameof(Stroke), Brushes.White);

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(StrokeThickness), 2);

    private const double Grid = 24;

    static Icon()
    {
        AffectsRender<Icon>(DataProperty, StrokeProperty, StrokeThicknessProperty);
        WidthProperty.OverrideDefaultValue<Icon>(25);
        HeightProperty.OverrideDefaultValue<Icon>(25);
        IsHitTestVisibleProperty.OverrideDefaultValue<Icon>(false);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public override void Render(DrawingContext ctx)
    {
        if (Data is not { } g || Stroke is not { } brush)
            return;
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0)
            return;
        var scale = size / Grid;
        var dx = (Bounds.Width - size) / 2;
        var dy = (Bounds.Height - size) / 2;
        var pen = new Pen(brush, StrokeThickness / scale, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (ctx.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(dx, dy)))
            ctx.DrawGeometry(null, pen, g);
    }
}
