using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>Accel Calibration: 6-position, level and simple accelerometer calibration. STUB - replaced by the page's implementation.</summary>
public sealed partial class AccelCalibrationViewModel : PageViewModel
{
    public AccelCalibrationViewModel(AppServices services)
        : base("Accel Calibration")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "6-position, level and simple accelerometer calibration";
}
