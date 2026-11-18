using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>ADSB: ADS-B receiver/transponder setup. STUB - replaced by the page's implementation.</summary>
public sealed partial class AdsbSetupViewModel : PageViewModel
{
    public AdsbSetupViewModel(AppServices services)
        : base("ADSB")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "ADS-B receiver/transponder setup";
}
