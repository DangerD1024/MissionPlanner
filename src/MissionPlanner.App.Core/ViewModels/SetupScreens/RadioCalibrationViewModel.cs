using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>Radio Calibration: live RC inputs and min/max/trim calibration. STUB - replaced by the page's implementation.</summary>
public sealed partial class RadioCalibrationViewModel : PageViewModel
{
    public RadioCalibrationViewModel(AppServices services)
        : base("Radio Calibration")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "live RC inputs and min/max/trim calibration";
}
