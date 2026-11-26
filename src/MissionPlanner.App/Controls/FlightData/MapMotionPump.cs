using Avalonia.Controls;
using MissionPlanner.App.Core.ViewModels.FlightData;

namespace MissionPlanner.App.Controls.FlightData;

/// <summary>
/// Moves the vehicle on a Flight Data map at the display's frame rate while it is moving
/// (<see cref="FlightDataViewModel.IsVehicleMoving"/>): one <see cref="FlightDataViewModel.AdvanceVehicleMotion"/>
/// per animation frame, then <c>redraw</c>. Idle while the vehicle is still, the map is hidden or
/// off screen; the view-model's 20 Hz tick keeps the vehicle current then and calls
/// <see cref="Kick"/> (through the view) when it starts to move.
/// </summary>
public sealed class MapMotionPump
{
    private readonly MapView _map;
    private readonly Func<FlightDataViewModel?> _vm;
    private readonly Action _redraw;
    private readonly Action<TimeSpan> _onFrame;
    private bool _pending;
    private long _pendingSince;
    private TimeSpan _lastFrame = TimeSpan.MinValue;

    public MapMotionPump(MapView map, Func<FlightDataViewModel?> vm, Action redraw)
    {
        _map = map;
        _vm = vm;
        _redraw = redraw;
        _onFrame = OnFrame;
    }

    /// <summary>Asks for animation frames if the vehicle is moving on a visible map.</summary>
    public void Kick()
    {
        // a request that never ran (window minimised mid-way) must not stop the map for good
        if (_pending && Environment.TickCount64 - _pendingSince < 1000)
            return;
        if (_vm() is not { IsVehicleMoving: true } || !_map.IsEffectivelyVisible || _map.Bounds.Width < 1 || _map.Bounds.Height < 1)
            return;
        if (TopLevel.GetTopLevel(_map) is not { } top)
            return;
        _pending = true;
        _pendingSince = Environment.TickCount64;
        top.RequestAnimationFrame(_onFrame);
    }

    private void OnFrame(TimeSpan frame)
    {
        // a late request and its replacement in the same frame: one advance, one chain
        if (frame == _lastFrame)
            return;
        _lastFrame = frame;
        _pending = false;
        if (_vm() is not { } vm)
            return;
        vm.AdvanceVehicleMotion();
        _redraw();
        Kick();
    }
}
