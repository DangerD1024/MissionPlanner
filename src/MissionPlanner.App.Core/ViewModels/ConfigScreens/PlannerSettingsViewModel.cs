using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>Planner: application settings. STUB - replaced by the page's implementation.</summary>
public sealed partial class PlannerSettingsViewModel : PageViewModel
{
    public PlannerSettingsViewModel(AppServices services)
        : base("Planner")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "application settings";
}
