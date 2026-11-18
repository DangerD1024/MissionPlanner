using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>Optical Flow: optical flow sensor setup and live flow values. STUB - replaced by the page's implementation.</summary>
public sealed partial class OpticalFlowViewModel : PageViewModel
{
    public OpticalFlowViewModel(AppServices services)
        : base("Optical Flow")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "optical flow sensor setup and live flow values";
}
