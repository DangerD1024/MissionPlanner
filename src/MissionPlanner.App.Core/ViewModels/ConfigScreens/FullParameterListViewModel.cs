using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>Full Parameter List: every parameter with tree, search, write, file load/save and compare. STUB - replaced by the page's implementation.</summary>
public sealed partial class FullParameterListViewModel : PageViewModel
{
    public FullParameterListViewModel(AppServices services)
        : base("Full Parameter List")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "every parameter with tree, search, write, file load/save and compare";
}
