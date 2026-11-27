using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MissionPlanner.App.Core.ViewModels.FlightData;

namespace MissionPlanner.App.Views.FlightData;

/// <summary>The telemetry log player in one row, shown at the bottom of the window away from DATA.</summary>
public sealed partial class TlogMiniBar : UserControl
{
    public TlogMiniBar()
    {
        InitializeComponent();

        // while the thumb is held, playback must not move it back under the pointer (as TlogPlayerView)
        PositionSlider.AddHandler(PointerPressedEvent, (_, _) => SetScrubbing(true), RoutingStrategies.Tunnel, handledEventsToo: true);
        PositionSlider.AddHandler(PointerReleasedEvent, (_, _) => SetScrubbing(false), RoutingStrategies.Tunnel, handledEventsToo: true);
        PositionSlider.AddHandler(PointerCaptureLostEvent, (_, _) => SetScrubbing(false), RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void SetScrubbing(bool value)
    {
        if (DataContext is TlogPlayerViewModel vm)
            vm.IsScrubbing = value;
    }
}
