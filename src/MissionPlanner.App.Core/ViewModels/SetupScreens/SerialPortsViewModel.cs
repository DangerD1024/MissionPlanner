using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>Serial Ports: SERIALn protocol, baud and options. STUB - replaced by the page's implementation.</summary>
public sealed partial class SerialPortsViewModel : PageViewModel
{
    public SerialPortsViewModel(AppServices services)
        : base("Serial Ports")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "SERIALn protocol, baud and options";
}
