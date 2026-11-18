using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>Advanced Params: every documented parameter (standard and advanced) with friendly editors.</summary>
public sealed partial class AdvancedParamsViewModel : FriendlyParamsViewModel
{
    public AdvancedParamsViewModel(AppServices services)
        : base("Advanced Params", services, includeAdvanced: true)
    {
    }
}
