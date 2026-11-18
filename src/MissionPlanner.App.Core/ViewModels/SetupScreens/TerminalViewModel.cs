using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>Terminal: serial and MAVLink shell terminal. STUB - replaced by the page's implementation.</summary>
public sealed partial class TerminalViewModel : PageViewModel
{
    public TerminalViewModel(AppServices services)
        : base("Terminal")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "serial and MAVLink shell terminal";
}
