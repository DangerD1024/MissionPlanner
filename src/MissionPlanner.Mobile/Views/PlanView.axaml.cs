using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GMap.NET;
using MissionPlanner.App.Controls;
using MissionPlanner.App.Controls.FlightPlan;
using MissionPlanner.App.Core.ViewModels.FlightPlan;

namespace MissionPlanner.Mobile.Views;

public sealed partial class PlanView : UserControl
{
    private FlightPlanViewModel? _vm;
    private bool _listOpen = true;

    public PlanView()
    {
        InitializeComponent();
        Map.MapClick += (_, e) => _vm?.MapClick(e.Position, e.Hit);
        Map.ItemDragged += (_, e) => _vm?.ItemDragged(e.Item, e.Position);
        Map.ItemDragCompleted += (_, e) => _vm?.ItemDragCompleted(e.Item, e.Position);
        DataContextChanged += (_, _) => Attach(DataContext as FlightPlanViewModel);
        PropertyChanged += (_, e) =>
        {
            // the tab is shown: fit the mission the first time
            if (e.Property == IsVisibleProperty && IsVisible && _vm?.TakePendingZoom() is { } pending)
                Dispatcher.UIThread.Post(() => Map.ZoomToFit(pending), DispatcherPriority.Background);
        };
    }

    private void Attach(FlightPlanViewModel? vm)
    {
        if (_vm != null)
        {
            _vm.MapChanged -= OnMapChanged;
            _vm.ZoomRequested -= OnZoom;
        }

        _vm = vm;
        Map.Layers.Clear();
        if (vm == null)
            return;
        Map.Layers.Add(new FenceShapesLayer(vm));
        Map.Layers.Add(new MissionRouteLayer(vm));
        Map.Layers.Add(new PlanMarkerLayer(vm, MAVLink.MAV_MISSION_TYPE.MISSION));
        Map.Layers.Add(new HomeLayer(vm));
        Map.Layers.Add(new PlanVehicleLayer(vm));
        vm.MapChanged += OnMapChanged;
        vm.ZoomRequested += OnZoom;
        UpdateTexts();
    }

    private void OnMapChanged(object? sender, EventArgs e)
    {
        Map.InvalidateVisual();
        UpdateTexts();
    }

    private void OnZoom(object? sender, IReadOnlyList<PointLatLng> points)
    {
        if (Map.Bounds.Width >= 10)
            Map.ZoomToFit(points);
        else
            Dispatcher.UIThread.Post(() => Map.ZoomToFit(points), DispatcherPriority.Background);
    }

    private void UpdateTexts()
    {
        if (_vm == null)
            return;
        var n = _vm.Store.Mission.Count;
        SummaryText.Text = $"{n} item{(n == 1 ? "" : "s")} · {_vm.DistanceText.Replace("Distance: ", "")}";
        ListHeader.Text = $"Waypoints ({n})";
    }

    private void OnToggleList(object? sender, RoutedEventArgs e)
    {
        _listOpen = !_listOpen;
        Items.IsVisible = _listOpen;
        ListChevron.Data = (Avalonia.Media.Geometry)this.FindResource(_listOpen ? "IcChevronDown" : "IcChevronUp")!;
    }

    private void OnAltUp(object? sender, RoutedEventArgs e) => NudgeAlt(5);

    private void OnAltDown(object? sender, RoutedEventArgs e) => NudgeAlt(-5);

    private void NudgeAlt(float delta)
    {
        if (_vm?.SelectedItem is { } item)
            item.Alt = Math.Max(0, item.Alt + delta);
    }

    private void OnDeleteSelected(object? sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedItem is { } item)
            _vm.DeleteItem(item);
    }
}
