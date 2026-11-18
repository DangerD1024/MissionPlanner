using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>Rangefinder: rangefinder type, pins, range and live distance. STUB - replaced by the page's implementation.</summary>
public sealed partial class RangefinderViewModel : PageViewModel
{
    public RangefinderViewModel(AppServices services)
        : base("Rangefinder")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "rangefinder type, pins, range and live distance";
}
