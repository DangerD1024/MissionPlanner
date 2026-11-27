using MissionPlanner.App.Core.Services;
using MissionPlanner.ArduPilot;
using static MissionPlanner.App.Core.ViewModels.MenuItemViewModel;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>CONFIG: tuning, parameters and tools, with Mission Planner's left menu.</summary>
public sealed class ConfigViewModel : MenuScreenViewModel
{
    public ConfigViewModel(AppServices s)
        : base("Config", s)
    {
        bool Ready() => Connected && s.Params.HasParams;
        bool Tunable() => Ready() && Firmware is Firmwares.ArduCopter2 or Firmwares.ArduPlane or Firmwares.ArduRover;

        Add(Page(TuningLayouts.BasicTitle, () => new BasicTuningViewModel(s), Tunable));
        Add(Page(TuningLayouts.ExtendedTitle, () => new ExtendedTuningViewModel(s), () => Ready() && Firmware == Firmwares.ArduCopter2));
        Add(Page(TuningLayouts.QuadPlaneExtendedTitle, () => new ExtendedTuningViewModel(s), () => Ready() && Firmware == Firmwares.ArduPlane && TuningLayouts.QuadPlaneEnabled(s.Params.Get)));
        Add(Page("Standard Params", () => new StandardParamsViewModel(s), Ready));
        Add(Page("Advanced Params", () => new AdvancedParamsViewModel(s), Ready));
        Add(Page("Full Parameter List", () => new FullParameterListViewModel(s)));
        Add(Page("Full Parameter Tree", () => new FullParameterTreeViewModel(s), Ready));
        Add(Page("User Params", () => new UserParamsViewModel(s), Ready));
        Add(Page("MAVFtp", () => new MavFtpViewModel(s), () => Connected));
        // a replayed tlog feeds the inspector like a live link (as in Mission Planner)
        Add(Page("MAVLink Inspector", () => new InspectorViewModel(s), () => Connected || Replaying));
    }
}
