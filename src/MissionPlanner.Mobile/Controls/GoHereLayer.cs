using Avalonia;
using Avalonia.Media;
using MissionPlanner.App.Controls;
using MissionPlanner.App.Core.ViewModels.Mobile;

namespace MissionPlanner.Mobile.Controls;

/// <summary>The point tapped on the Fly map, while "Go here?" is asked: a target ring.</summary>
public sealed class GoHereLayer(MobileShellViewModel vm) : MapLayer
{
    private static readonly IPen Ring = new Pen(new SolidColorBrush(Color.Parse("#3AA0FF")), 3);
    private static readonly IPen Halo = new Pen(new SolidColorBrush(Color.Parse("#B3000000")), 6);
    private static readonly IBrush Dot = new SolidColorBrush(Color.Parse("#3AA0FF"));

    public override void Render(DrawingContext ctx, MapViewport vp)
    {
        if (vm.GoHere is not { } p)
            return;
        var c = vp.ToScreen(p.Lat, p.Lng);
        ctx.DrawEllipse(null, Halo, c, 14, 14);
        ctx.DrawEllipse(null, Ring, c, 14, 14);
        ctx.DrawEllipse(Dot, null, c, 4, 4);
    }
}
