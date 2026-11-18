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

        Add(Page("Basic Tuning", () => new BasicTuningViewModel(s), Tunable));
        Add(Page("Extended Tuning", () => new ExtendedTuningViewModel(s), Tunable));
        Add(Page("Standard Params", () => new StandardParamsViewModel(s), Ready));
        Add(Page("Advanced Params", () => new AdvancedParamsViewModel(s), Ready));
        Add(Page("Full Parameter List", () => new FullParameterListViewModel(s)));
        Add(Page("MAVFtp", () => new MavFtpViewModel(s), () => Connected));
        Add(Page("MAVLink Inspector", () => new InspectorViewModel(s), () => Connected));
    }
}
