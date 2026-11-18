using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>ESC Calibration: ESC calibration and motor PWM settings. STUB - replaced by the page's implementation.</summary>
public sealed partial class EscCalibrationViewModel : PageViewModel
{
    public EscCalibrationViewModel(AppServices services)
        : base("ESC Calibration")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "ESC calibration and motor PWM settings";
}
