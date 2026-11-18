using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>Motor Test: spin individual motors. STUB - replaced by the page's implementation.</summary>
public sealed partial class MotorTestViewModel : PageViewModel
{
    public MotorTestViewModel(AppServices services)
        : base("Motor Test")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "spin individual motors";
}
