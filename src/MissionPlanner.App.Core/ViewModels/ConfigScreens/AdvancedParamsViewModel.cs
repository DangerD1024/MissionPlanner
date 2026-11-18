using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>Advanced Params: documented advanced-level parameters with friendly editors. STUB - replaced by the page's implementation.</summary>
public sealed partial class AdvancedParamsViewModel : PageViewModel
{
    public AdvancedParamsViewModel(AppServices services)
        : base("Advanced Params")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "documented advanced-level parameters with friendly editors";
}
