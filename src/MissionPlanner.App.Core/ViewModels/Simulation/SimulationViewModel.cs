using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.Simulation;

/// <summary>Simulation: download and run ArduPilot SITL and connect to it. STUB - replaced by the page's implementation.</summary>
public sealed partial class SimulationViewModel : PageViewModel
{
    public SimulationViewModel(AppServices services)
        : base("Simulation")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "download and run ArduPilot SITL and connect to it";
}
