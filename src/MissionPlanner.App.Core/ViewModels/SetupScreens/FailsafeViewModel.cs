using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>FailSafe: radio, battery and GCS failsafes. STUB - replaced by the page's implementation.</summary>
public sealed partial class FailsafeViewModel : PageViewModel
{
    public FailsafeViewModel(AppServices services)
        : base("FailSafe")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "radio, battery and GCS failsafes";
}
