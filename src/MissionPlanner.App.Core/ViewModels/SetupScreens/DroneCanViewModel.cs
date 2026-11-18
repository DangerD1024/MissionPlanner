using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>DroneCAN/UAVCAN: CAN bus nodes, their parameters and firmware. STUB - replaced by the page's implementation.</summary>
public sealed partial class DroneCanViewModel : PageViewModel
{
    public DroneCanViewModel(AppServices services)
        : base("DroneCAN/UAVCAN")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "CAN bus nodes, their parameters and firmware";
}
