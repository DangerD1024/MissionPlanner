using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>HW ID: detected devices (IMU, compass, baro, airspeed) decoded from DEV_ID parameters. STUB - replaced by the page's implementation.</summary>
public sealed partial class HardwareIdViewModel : PageViewModel
{
    public HardwareIdViewModel(AppServices services)
        : base("HW ID")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "detected devices (IMU, compass, baro, airspeed) decoded from DEV_ID parameters";
}
