using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>Extended Tuning: PID and navigation tuning. STUB - replaced by the page's implementation.</summary>
public sealed partial class ExtendedTuningViewModel : PageViewModel
{
    public ExtendedTuningViewModel(AppServices services)
        : base("Extended Tuning")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "PID and navigation tuning";
}
