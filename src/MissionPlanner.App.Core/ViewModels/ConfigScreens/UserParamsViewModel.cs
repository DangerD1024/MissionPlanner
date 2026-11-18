using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>User Params: a user-chosen list of parameters to watch and edit. STUB - replaced by the page's implementation.</summary>
public sealed partial class UserParamsViewModel : PageViewModel
{
    public UserParamsViewModel(AppServices services)
        : base("User Params")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "a user-chosen list of parameters to watch and edit";
}
