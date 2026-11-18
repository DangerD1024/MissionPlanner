using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>Compass: onboard magnetometer calibration and compass priority. STUB - replaced by the page's implementation.</summary>
public sealed partial class CompassViewModel : PageViewModel
{
    public CompassViewModel(AppServices services)
        : base("Compass")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "onboard magnetometer calibration and compass priority";
}
