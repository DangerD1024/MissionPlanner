using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>Standard Params: documented standard-level parameters with friendly editors.</summary>
public sealed partial class StandardParamsViewModel : FriendlyParamsViewModel
{
    public StandardParamsViewModel(AppServices services)
        : base("Standard Params", services, includeAdvanced: false)
    {
    }
}
