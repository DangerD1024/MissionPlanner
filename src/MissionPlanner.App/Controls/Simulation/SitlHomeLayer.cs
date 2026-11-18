using Avalonia;
using Avalonia.Media;
using MissionPlanner.App.Core.ViewModels.Simulation;

namespace MissionPlanner.App.Controls.Simulation;

/// <summary>The simulator's start location: an "H" marker with a heading tick, draggable.</summary>
public sealed class SitlHomeLayer(SimulationViewModel vm) : MapLayer
{
    private static readonly IBrush Fill = new SolidColorBrush(Color.Parse("#8cc63f"));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#0f1a05"));
    private static readonly IPen Outline = new Pen(Brushes.White, 2);
    private static readonly IPen HeadingPen = new Pen(new SolidColorBrush(Color.Parse("#f0b429")), 2.5, lineCap: PenLineCap.Round);
    private static readonly Typeface Bold = new(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);

    /// <summary>The hit-test token for the marker (dragging it moves home).</summary>
    public static readonly object HomeToken = new();

    public override void Render(DrawingContext ctx, MapViewport vp)
    {
        var home = vm.Home;
        if (!home.IsValid)
            return;
        var p = vp.ToScreen(home.Lat, home.Lng);

        var rad = home.Heading * Math.PI / 180;
        var tip = new Point(p.X + Math.Sin(rad) * 30, p.Y - Math.Cos(rad) * 30);
        ctx.DrawLine(HeadingPen, p, tip);

        ctx.DrawEllipse(Fill, Outline, p, 12, 12);
        var text = new FormattedText("H", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Bold, 13, TextBrush);
        ctx.DrawText(text, new Point(p.X - text.Width / 2, p.Y - text.Height / 2));
    }

    public override object? HitTest(Point screen, MapViewport vp)
    {
        var home = vm.Home;
        if (!home.IsValid)
            return null;
        var p = vp.ToScreen(home.Lat, home.Lng);
        var dx = screen.X - p.X;
        var dy = screen.Y - p.Y;
        return dx * dx + dy * dy <= 16 * 16 ? HomeToken : null;
    }
}
