using System.Globalization;
using Avalonia;
using Avalonia.Media;
using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Controls;

/// <summary>The ground station's own position (TOOLS > Moving Base): a blue dot with its name (DATA and PLAN).</summary>
public sealed class GcsLocationLayer(GcsLocationService location) : MapLayer
{
    private static readonly IBrush Fill = new SolidColorBrush(Color.Parse("#2f80ed"));
    private static readonly IBrush Halo = new SolidColorBrush(Color.FromArgb(70, 47, 128, 237));
    private static readonly IPen Outline = new Pen(Brushes.White, 2);
    private static readonly IBrush LabelBack = new SolidColorBrush(Color.FromArgb(170, 16, 19, 23));

    public override void Render(DrawingContext ctx, MapViewport vp)
    {
        if (location.Current is not { } p)
            return;
        var at = vp.ToScreen(p.Lat, p.Lng);
        if (at.X < -40 || at.Y < -40 || at.X > vp.Size.Width + 200 || at.Y > vp.Size.Height + 40)
            return;

        ctx.DrawEllipse(Halo, null, at, 16, 16);
        ctx.DrawEllipse(Fill, Outline, at, 7.5, 7.5);

        var text = new FormattedText(p.Source, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 11.5, Brushes.White);
        var label = new Point(at.X + 14, at.Y - 8);
        ctx.DrawRectangle(LabelBack, null, new Rect(label.X - 3, label.Y - 1, text.Width + 6, text.Height + 2), 3, 3);
        ctx.DrawText(text, label);
    }
}
