using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>Battery Monitor: battery monitor type, sensor and calibration. STUB - replaced by the page's implementation.</summary>
public sealed partial class BatteryMonitorViewModel : PageViewModel
{
    public BatteryMonitorViewModel(AppServices services)
        : base("Battery Monitor")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "battery monitor type, sensor and calibration";
}
