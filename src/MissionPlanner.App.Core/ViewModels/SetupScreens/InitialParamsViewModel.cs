using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>Initial Parameters: initial tuning parameters calculated from prop size, battery and frame. STUB - replaced by the page's implementation.</summary>
public sealed partial class InitialParamsViewModel : PageViewModel
{
    public InitialParamsViewModel(AppServices services)
        : base("Initial Parameters")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "initial tuning parameters calculated from prop size, battery and frame";
}
