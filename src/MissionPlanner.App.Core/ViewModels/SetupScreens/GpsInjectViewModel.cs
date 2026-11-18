using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>RTK/GPS Inject: RTCM corrections from NTRIP or a serial base station to the vehicle. STUB - replaced by the page's implementation.</summary>
public sealed partial class GpsInjectViewModel : PageViewModel
{
    public GpsInjectViewModel(AppServices services)
        : base("RTK/GPS Inject")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "RTCM corrections from NTRIP or a serial base station to the vehicle";
}
