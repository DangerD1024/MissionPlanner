using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using MissionPlanner.App.Controls;
using MissionPlanner.App.Controls.FlightData;
using MissionPlanner.App.Core.ViewModels.FlightData;
using MissionPlanner.App.Core.ViewModels.Mobile;

namespace MissionPlanner.Mobile.Views;

public sealed partial class FlyView : UserControl
{
    private MobileShellViewModel? _vm;
    private long _mapVersion = -1;

    public FlyView()
    {
        InitializeComponent();
        Map.UserPanned += (_, _) => _vm?.Fly.OnUserPanned();
        Map.MapClick += (_, e) =>
        {
            if (e.Hit is AdsbContact contact)
                _vm?.Fly.ShowAdsbDetails(contact);
            else
                _vm?.MapTapped(e.Position.Lat, e.Position.Lng);
        };
        Map.MapDoubleClick += (_, _) => _vm?.Fly.ZoomInCommand.Execute(null);
        DataContextChanged += (_, _) => Attach(DataContext as MobileShellViewModel);
    }

    private void Attach(MobileShellViewModel? vm)
    {
        if (_vm != null)
        {
            _vm.Fly.TelemetryUpdated -= OnTelemetry;
            _vm.PropertyChanged -= OnVmChanged;
        }

        _vm = vm;
        Map.Layers.Clear();
        _mapVersion = -1;
        if (vm == null)
            return;
        vm.Fly.TelemetryUpdated += OnTelemetry;
        vm.PropertyChanged += OnVmChanged;
        Map.Layers.Add(new MissionLayer(vm.Fly.MapModel));
        Map.Layers.Add(new TrackLayer(vm.Fly.MapModel));
        Map.Layers.Add(new VehicleLayer(vm.Fly.MapModel));
        Map.Layers.Add(new PoiLayer(vm.Services.Pois));
        Map.Layers.Add(new Controls.GoHereLayer(vm));
        InvalidateMeasure();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MobileShellViewModel.IsHudLarge))
            InvalidateMeasure();
        else if (e.PropertyName == nameof(MobileShellViewModel.GoHere))
            Map.InvalidateVisual();
    }

    /// <summary>The HUD takes 2/3 of the width in height (large) or 0.4 (small); the map gets the rest.</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var w = availableSize.Width;
        if (w > 0 && !double.IsInfinity(w))
        {
            var large = _vm?.IsHudLarge ?? true;
            var h = Math.Round(w * (large ? 0.68 : 0.4));
            // leave the map at least half of what is left
            if (!double.IsInfinity(availableSize.Height))
                h = Math.Min(h, Math.Max(140, availableSize.Height * 0.45));
            if (Math.Abs(HudHost.Height - h) > 0.5 || double.IsNaN(HudHost.Height))
                HudHost.Height = h;
        }

        return base.MeasureOverride(availableSize);
    }

    private void OnTelemetry(object? sender, EventArgs e)
    {
        if (!IsEffectivelyVisible)
            return;
        HudControl.Refresh();
        var version = _vm?.Fly.MapModel.Version ?? -1;
        if (version != _mapVersion)
        {
            _mapVersion = version;
            Map.InvalidateVisual();
        }
    }
}
