using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>SiK Radio: 3DR/RFD SiK telemetry radio settings (local and remote). STUB - replaced by the page's implementation.</summary>
public sealed partial class SikRadioViewModel : PageViewModel
{
    public SikRadioViewModel(AppServices services)
        : base("SiK Radio")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "3DR/RFD SiK telemetry radio settings (local and remote)";
}
