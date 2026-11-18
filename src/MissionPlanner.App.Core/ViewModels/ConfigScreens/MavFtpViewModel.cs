using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>MAVFtp: browse, download, upload and delete files on the vehicle. STUB - replaced by the page's implementation.</summary>
public sealed partial class MavFtpViewModel : PageViewModel
{
    public MavFtpViewModel(AppServices services)
        : base("MAVFtp")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "browse, download, upload and delete files on the vehicle";
}
