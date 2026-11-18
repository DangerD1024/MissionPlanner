using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.Logs;

/// <summary>Log Browser: graph DataFlash (.bin/.log) and telemetry (.tlog) logs. STUB - replaced by the page's implementation.</summary>
public sealed partial class LogBrowserViewModel : PageViewModel
{
    public LogBrowserViewModel(AppServices services)
        : base("Log Browser")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "graph DataFlash (.bin/.log) and telemetry (.tlog) logs";
}
