using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>MAVLink Inspector: live message rates and field values. STUB - replaced by the page's implementation.</summary>
public sealed partial class InspectorViewModel : PageViewModel
{
    public InspectorViewModel(AppServices services)
        : base("MAVLink Inspector")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "live message rates and field values";
}
