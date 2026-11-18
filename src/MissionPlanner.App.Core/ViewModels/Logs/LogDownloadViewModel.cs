using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.Logs;

/// <summary>DataFlash Logs: list and download onboard logs over MAVLink. STUB - replaced by the page's implementation.</summary>
public sealed partial class LogDownloadViewModel : PageViewModel
{
    public LogDownloadViewModel(AppServices services)
        : base("DataFlash Logs")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "list and download onboard logs over MAVLink";
}
