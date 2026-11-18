using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>Standard Params: documented standard-level parameters with friendly editors. STUB - replaced by the page's implementation.</summary>
public sealed partial class StandardParamsViewModel : PageViewModel
{
    public StandardParamsViewModel(AppServices services)
        : base("Standard Params")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "documented standard-level parameters with friendly editors";
}
