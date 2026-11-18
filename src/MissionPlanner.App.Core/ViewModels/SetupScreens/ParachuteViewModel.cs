using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>Parachute: parachute release setup. STUB - replaced by the page's implementation.</summary>
public sealed partial class ParachuteViewModel : PageViewModel
{
    public ParachuteViewModel(AppServices services)
        : base("Parachute")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "parachute release setup";
}
