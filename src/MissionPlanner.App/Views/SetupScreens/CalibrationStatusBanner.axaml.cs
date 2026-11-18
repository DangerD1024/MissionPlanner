using Avalonia.Controls;

namespace MissionPlanner.App.Views.SetupScreens;

/// <summary>Shows a <see cref="Core.ViewModels.SetupScreens.Calibration.StatusLine"/>: info, success, warning or error.</summary>
public sealed partial class CalibrationStatusBanner : UserControl
{
    public CalibrationStatusBanner() => InitializeComponent();
}
