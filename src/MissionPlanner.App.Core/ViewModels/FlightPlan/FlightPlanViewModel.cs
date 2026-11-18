using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.FlightPlan;

/// <summary>Flight Plan: mission, fence and rally editor. STUB - replaced by the page's implementation.</summary>
public sealed partial class FlightPlanViewModel : PageViewModel
{
    public FlightPlanViewModel(AppServices services)
        : base("Flight Plan")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "mission, fence and rally editor";
}
