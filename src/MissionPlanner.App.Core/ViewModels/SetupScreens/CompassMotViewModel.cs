using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>CompassMot: compass/motor interference calibration. STUB - replaced by the page's implementation.</summary>
public sealed partial class CompassMotViewModel : PageViewModel
{
    public CompassMotViewModel(AppServices services)
        : base("CompassMot")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "compass/motor interference calibration";
}
