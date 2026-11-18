using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>Install Firmware: firmware manifest, board detection and upload. STUB - replaced by the page's implementation.</summary>
public sealed partial class FirmwareViewModel : PageViewModel
{
    public FirmwareViewModel(AppServices services)
        : base("Install Firmware")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "firmware manifest, board detection and upload";
}
