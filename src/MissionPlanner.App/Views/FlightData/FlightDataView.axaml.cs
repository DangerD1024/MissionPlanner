using Avalonia.Controls;
using MissionPlanner.App.Core.ViewModels.FlightData;

namespace MissionPlanner.App.Views.FlightData;

public sealed partial class FlightDataView : UserControl
{
    private FlightDataViewModel? _vm;

    public FlightDataView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm != null)
                _vm.TelemetryUpdated -= OnTelemetry;
            _vm = DataContext as FlightDataViewModel;
            if (_vm != null)
                _vm.TelemetryUpdated += OnTelemetry;
        };
    }

    private void OnTelemetry(object? sender, EventArgs e) => HudControl.Refresh();
}
