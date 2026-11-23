using System.Globalization;
using Avalonia;
using Avalonia.Media;
using MissionPlanner.App.Core.ViewModels.FlightData;

namespace MissionPlanner.App.Controls.FlightData;

/// <summary>Mission Planner's camera overlap: each photo's ground footprint, translucent so overlaps show darker.</summary>
public sealed class PhotoLayer(FlightMapModel model) : MapLayer
{
    private static readonly IBrush Fill = new SolidColorBrush(Color.FromArgb(48, 64, 200, 255));
    private static readonly IPen Edge = new Pen(new SolidColorBrush(Color.FromArgb(170, 64, 200, 255)), 1);
    private static readonly IBrush Dot = new SolidColorBrush(Color.Parse("#40c8ff"));

    public override void Render(DrawingContext ctx, MapViewport vp)
    {
        if (!model.ShowPhotos || model.Photos.Count == 0)
            return;
        foreach (var photo in model.Photos)
        {
            if (photo.Corners.Count == 4)
            {
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    g.BeginFigure(vp.ToScreen(photo.Corners[0].Lat, photo.Corners[0].Lng), true);
                    for (var i = 1; i < 4; i++)
                        g.LineTo(vp.ToScreen(photo.Corners[i].Lat, photo.Corners[i].Lng));
                    g.EndFigure(true);
                }

                ctx.DrawGeometry(Fill, Edge, geo);
            }

            ctx.DrawEllipse(Dot, null, vp.ToScreen(photo.Lat, photo.Lng), 2.5, 2.5);
        }
    }
}
