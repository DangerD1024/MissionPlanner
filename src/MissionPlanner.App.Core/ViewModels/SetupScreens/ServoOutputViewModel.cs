using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>Servo Output: SERVOn function, range, reverse and live outputs. STUB - replaced by the page's implementation.</summary>
public sealed partial class ServoOutputViewModel : PageViewModel
{
    public ServoOutputViewModel(AppServices services)
        : base("Servo Output")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "SERVOn function, range, reverse and live outputs";
}
