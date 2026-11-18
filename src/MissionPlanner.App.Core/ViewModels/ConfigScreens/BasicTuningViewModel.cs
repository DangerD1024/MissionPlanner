using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>Basic Tuning: the main tuning values for the vehicle type. STUB - replaced by the page's implementation.</summary>
public sealed partial class BasicTuningViewModel : PageViewModel
{
    public BasicTuningViewModel(AppServices services)
        : base("Basic Tuning")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "the main tuning values for the vehicle type";
}
