using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>Mount: gimbal mount type, axes, ranges and stabilisation. STUB - replaced by the page's implementation.</summary>
public sealed partial class MountViewModel : PageViewModel
{
    public MountViewModel(AppServices services)
        : base("Mount")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "gimbal mount type, axes, ranges and stabilisation";
}
