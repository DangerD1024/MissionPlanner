using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>Flight Modes: six flight-mode slots and the active switch position. STUB - replaced by the page's implementation.</summary>
public sealed partial class FlightModesViewModel : PageViewModel
{
    public FlightModesViewModel(AppServices services)
        : base("Flight Modes")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "six flight-mode slots and the active switch position";
}
