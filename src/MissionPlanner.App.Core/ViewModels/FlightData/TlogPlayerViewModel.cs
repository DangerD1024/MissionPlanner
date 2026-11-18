using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.FlightData;

/// <summary>Telemetry Logs: tlog playback controls. STUB - replaced by the page's implementation.</summary>
public sealed partial class TlogPlayerViewModel : PageViewModel
{
    public TlogPlayerViewModel(AppServices services)
        : base("Telemetry Logs")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "tlog playback controls";
}
