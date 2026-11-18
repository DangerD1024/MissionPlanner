using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>Full Parameter Tree: every parameter as a tree grouped by prefix with editors. STUB - replaced by the page's implementation.</summary>
public sealed partial class FullParameterTreeViewModel : PageViewModel
{
    public FullParameterTreeViewModel(AppServices services)
        : base("Full Parameter Tree")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "every parameter as a tree grouped by prefix with editors";
}
