using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.FlightData;

/// <summary>Tuning: live graph of selected telemetry values. STUB - replaced by the page's implementation.</summary>
public sealed partial class TuningGraphViewModel : PageViewModel
{
    public TuningGraphViewModel(AppServices services)
        : base("Tuning")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "live graph of selected telemetry values";
}
