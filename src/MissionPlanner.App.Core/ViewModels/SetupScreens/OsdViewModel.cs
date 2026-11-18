using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>OSD: onboard OSD screens and items. STUB - replaced by the page's implementation.</summary>
public sealed partial class OsdViewModel : PageViewModel
{
    public OsdViewModel(AppServices services)
        : base("OSD")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "onboard OSD screens and items";
}
