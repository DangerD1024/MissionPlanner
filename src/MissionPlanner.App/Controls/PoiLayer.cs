using System.Globalization;
using Avalonia;
using Avalonia.Media;
using MissionPlanner.App.Core.Mission;

namespace MissionPlanner.App.Controls;

/// <summary>Points of interest on a map: a purple pin with the name beside it (DATA and PLAN).</summary>
public sealed class PoiLayer(PoiStore store) : MapLayer
{
    private static readonly IBrush Fill = new SolidColorBrush(Color.Parse("#b45cff"));
    private static readonly IPen Outline = new Pen(new SolidColorBrush(Color.FromArgb(220, 16, 19, 23)), 1.5);
    private static readonly IBrush LabelBack = new SolidColorBrush(Color.FromArgb(170, 16, 19, 23));
    private static readonly Geometry Pin = StreamGeometry.Parse("M0,0 C-2,-6 -7,-9 -7,-14 A7,7 0 1 1 7,-14 C7,-9 2,-6 0,0 Z");

    public override void Render(DrawingContext ctx, MapViewport vp)
    {
        foreach (var poi in store.Items)
        {
            var p = vp.ToScreen(poi.Lat, poi.Lng);
            if (p.X < -40 || p.Y < -40 || p.X > vp.Size.Width + 200 || p.Y > vp.Size.Height + 40)
                continue;
            using (ctx.PushTransform(Matrix.CreateTranslation(p.X, p.Y)))
            {
                ctx.DrawGeometry(Fill, Outline, Pin);
                ctx.DrawEllipse(Brushes.White, null, new Point(0, -14), 2.5, 2.5);
            }

            if (poi.Name.Length == 0)
                continue;
            var text = new FormattedText(poi.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 11.5, Brushes.White);
            var at = new Point(p.X + 9, p.Y - 22);
            ctx.DrawRectangle(LabelBack, null, new Rect(at.X - 3, at.Y - 1, text.Width + 6, text.Height + 2), 3, 3);
            ctx.DrawText(text, at);
        }
    }

    public override object? HitTest(Point screen, MapViewport vp)
    {
        foreach (var poi in store.Items)
        {
            var p = vp.ToScreen(poi.Lat, poi.Lng);
            if (Math.Abs(screen.X - p.X) <= 9 && screen.Y <= p.Y + 2 && screen.Y >= p.Y - 24)
                return poi;
        }

        return null;
    }
}
