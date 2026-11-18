using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>Airspeed: airspeed sensor type, use, ratio, calibration and live airspeed. STUB - replaced by the page's implementation.</summary>
public sealed partial class AirspeedViewModel : PageViewModel
{
    public AirspeedViewModel(AppServices services)
        : base("Airspeed")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "airspeed sensor type, use, ratio, calibration and live airspeed";
}
