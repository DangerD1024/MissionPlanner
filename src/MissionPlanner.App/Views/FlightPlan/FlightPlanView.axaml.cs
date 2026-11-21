using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GMap.NET;
using MissionPlanner.App.Controls;
using MissionPlanner.App.Controls.FlightPlan;
using MissionPlanner.App.Core.Mission;
using MissionPlanner.App.Core.ViewModels.FlightPlan;

namespace MissionPlanner.App.Views.FlightPlan;

public sealed partial class FlightPlanView : UserControl
{
    /// <summary>Grid columns whose header follows the selected row's command (P1-P4, Lat, Long, Alt).</summary>
    private const int FirstValueColumn = 2;

    private FlightPlanViewModel? _vm;
    private bool _attached;

    public FlightPlanView()
    {
        InitializeComponent();

        Map.MapClick += (_, e) =>
        {
            _vm?.MapClick(e.Position, e.Hit);
            Map.Focus();
        };
        Map.ItemDragged += (_, e) => _vm?.ItemDragged(e.Item, e.Position);
        Map.ItemDragCompleted += (_, e) => _vm?.ItemDragCompleted(e.Item, e.Position);
        Map.PointerPositionChanged += (_, p) => _vm?.PointerMoved(p);
        Map.MapContextRequested += OnMapContextRequested;

        // a click anywhere in a row (its combo boxes and buttons too) selects the row
        Rows.AddHandler(PointerPressedEvent, OnGridPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        DataContextChanged += (_, _) => Bind(DataContext as FlightPlanViewModel);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Bind(DataContext as FlightPlanViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Bind(null);
    }

    /// <summary>Views are rebuilt on every visit to the page; only the attached one listens to the view-model.</summary>
    private void Bind(FlightPlanViewModel? vm)
    {
        if (!_attached)
            vm = null;
        if (ReferenceEquals(vm, _vm))
            return;

        if (_vm != null)
        {
            _vm.MapChanged -= OnMapChanged;
            _vm.ZoomRequested -= OnZoomRequested;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _vm = vm;
        Map.Layers.Clear();
        if (vm == null)
            return;

        Map.Layers.Add(new FenceShapesLayer(vm));
        Map.Layers.Add(new MissionRouteLayer(vm));
        Map.Layers.Add(new PlanMarkerLayer(vm, MAVLink.MAV_MISSION_TYPE.RALLY));
        Map.Layers.Add(new PlanMarkerLayer(vm, MAVLink.MAV_MISSION_TYPE.FENCE));
        Map.Layers.Add(new PlanMarkerLayer(vm, MAVLink.MAV_MISSION_TYPE.MISSION));
        Map.Layers.Add(new HomeLayer(vm));
        Map.Layers.Add(new DrawPolygonLayer(vm));
        Map.Layers.Add(new PlanVehicleLayer(vm));
        Map.Layers.Add(new PoiLayer(vm.Services.Pois));
        vm.Services.Pois.Changed -= OnPoisChanged;
        vm.Services.Pois.Changed += OnPoisChanged;

        vm.MapChanged += OnMapChanged;
        vm.ZoomRequested += OnZoomRequested;
        vm.PropertyChanged += OnViewModelPropertyChanged;

        UpdateHeaders();
        UpdateTexts();

        if (vm.TakePendingZoom() is { } pending)
            ZoomWhenLaidOut(pending);
    }

    private void OnMapChanged(object? sender, EventArgs e)
    {
        Map.InvalidateVisual();
        UpdateTexts();
    }

    private void OnZoomRequested(object? sender, IReadOnlyList<PointLatLng> points) => ZoomWhenLaidOut(points);

    /// <summary>The map can only fit points once it has a size.</summary>
    private void ZoomWhenLaidOut(IReadOnlyList<PointLatLng> points)
    {
        if (Map.Bounds.Width >= 10)
        {
            Map.ZoomToFit(points);
            return;
        }

        void OnLayout(object? s, EventArgs e)
        {
            if (Map.Bounds.Width < 10)
                return;
            Map.LayoutUpdated -= OnLayout;
            Dispatcher.UIThread.Post(() => Map.ZoomToFit(points), DispatcherPriority.Background);
        }

        Map.LayoutUpdated += OnLayout;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(FlightPlanViewModel.Headers):
                UpdateHeaders();
                break;
            case nameof(FlightPlanViewModel.SelectedItem):
                if (_vm?.SelectedItem is { } item)
                    Dispatcher.UIThread.Post(() => Rows.ScrollIntoView(item, null), DispatcherPriority.Background);
                break;
            case nameof(FlightPlanViewModel.SelectedMissionType):
            case nameof(FlightPlanViewModel.PolygonMode):
                UpdateTexts();
                break;
        }
    }

    private void UpdateHeaders()
    {
        if (_vm == null)
            return;
        var headers = _vm.Headers;
        for (var i = 0; i < headers.Count && FirstValueColumn + i < Rows.Columns.Count; i++)
            Rows.Columns[FirstValueColumn + i].Header = headers[i];
    }

    private void UpdateTexts()
    {
        if (_vm == null)
            return;
        var list = _vm.CurrentList;
        CountText.Text = _vm.IsMissionMode
            ? $"{list.Count} item{(list.Count == 1 ? "" : "s")} · {_vm.DistanceText.Replace("Distance: ", "")}"
            : $"{list.Count} item{(list.Count == 1 ? "" : "s")}";
        EmptyText.Text = list.Count > 0
            ? ""
            : _vm.MissionType switch
            {
                MAVLink.MAV_MISSION_TYPE.FENCE => "No fence. Draw a polygon (right click › Draw Polygon), then right click › Fence.",
                MAVLink.MAV_MISSION_TYPE.RALLY => "No rally points. Click the map to add one.",
                _ => "No waypoints. Click the map to add one, or Load File / Read.",
            };
        HintText.Text = _vm.PolygonMode
            ? "Click: add polygon point · drag a square to move it · right click for more"
            : _vm.MissionType switch
            {
                MAVLink.MAV_MISSION_TYPE.FENCE => "Click: add exclusion circle · drag to move · right click for fence tools",
                MAVLink.MAV_MISSION_TYPE.RALLY => "Click: add rally point · drag to move · right click for more",
                _ => "Click: add waypoint · drag to move · drag + to insert · right click for more",
            };
    }

    // ---- grid ----------------------------------------------------------------------------------

    private void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm != null && (e.Source as Control)?.DataContext is MissionItem item && !ReferenceEquals(_vm.SelectedItem, item))
            _vm.SelectedItem = item;
    }

    private void OnCommandSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // only the user's choice is written: rows are recycled and lists swapped under the combo
        if (sender is ComboBox { IsDropDownOpen: true } or ComboBox { IsKeyboardFocusWithin: true } &&
            sender is ComboBox { DataContext: MissionItem item, SelectedItem: MAVLink.MAV_CMD cmd } &&
            item.Command != cmd)
        {
            item.Command = cmd;
        }
    }

    private void OnFrameSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { IsDropDownOpen: true } or ComboBox { IsKeyboardFocusWithin: true } &&
            sender is ComboBox { DataContext: MissionItem item, SelectedItem: AltFrame frame } &&
            item.Frame != frame)
        {
            item.Frame = frame;
        }
    }

    private void OnDeleteRow(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is MissionItem item)
            _vm?.DeleteItem(item);
    }

    private void OnMoveUp(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is MissionItem item)
            _vm?.MoveUp(item);
    }

    private void OnMoveDown(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is MissionItem item)
            _vm?.MoveDown(item);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm == null)
            return;
        if (e.Key == Key.Delete && e.Source is not TextBox && !Rows.IsEditing())
        {
            _vm.DeleteSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _vm.PolygonMode)
        {
            _vm.FinishPolygonCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ---- map context menu ----------------------------------------------------------------------

    private void OnPoisChanged(object? sender, EventArgs e) => Map.InvalidateVisual();

    private void OnMapContextRequested(object? sender, MapPointerEventArgs e)
    {
        var vm = _vm;
        if (vm == null)
            return;
        var at = e.Position;
        var hit = e.Hit;
        var menu = new ContextMenu();
        var items = new List<Control>();

        switch (hit)
        {
            case MissionItem item when vm.CurrentList.Contains(item):
                vm.SelectedItem = item;
                items.Add(Item($"Delete {(vm.IsMissionMode ? "WP" : "Item")} {item.Seq}", () => vm.DeleteItem(item)));
                break;
            case PolygonVertexHandle v:
                items.Add(Item($"Delete Polygon Point {v.Index + 1}", () => vm.DeletePolygonPoint(v.Index)));
                break;
            default:
                items.Add(Item("Delete WP", () => { }, enabled: false));
                break;
        }

        items.Add(Sub("Insert WP",
            Item("Here", () => _ = vm.InsertWaypointAsync(at, spline: false)),
            Item("At Current Position", () => _ = vm.InsertWaypointAtVehicleAsync(), vm.VehiclePosition != null)));
        items.Add(Item("Insert Spline WP", () => _ = vm.InsertWaypointAsync(at, spline: true)));
        items.Add(Sub("Loiter",
            Item("Forever", () => vm.AddLoiterUnlimited(at)),
            Item("Circles", () => _ = vm.AddLoiterTurnsAsync(at)),
            Item("Time", () => _ = vm.AddLoiterTimeAsync(at))));
        items.Add(Item("Takeoff", () => _ = vm.AddTakeoffAsync()));
        items.Add(Item("Land", () => vm.AddLand(at)));
        items.Add(Item("RTL", vm.AddReturnToLaunch));
        items.Add(Sub("Jump",
            Item("Start", () => _ = vm.AddJumpAsync(toStart: true)),
            Item("WP #", () => _ = vm.AddJumpAsync(toStart: false))));
        items.Add(Item("DO_SET_ROI", () => vm.AddRoiHere(at)));
        items.Add(new Separator());
        items.Add(Item("Set Home Here", () => _ = vm.SetHomeHereAsync(at)));
        items.Add(Sub("Draw Polygon",
            Item("Add Polygon Point", () => vm.AddPolygonPoint(at)),
            Item("Clear Polygon", () => vm.ClearPolygonCommand.Execute(null), vm.HasPolygon),
            new Separator(),
            Item("From Current Waypoints", vm.PolygonFromWaypoints, vm.Store.Mission.Count >= 3),
            Item("From SHP", () => _ = vm.PolygonFromShapefileAsync()),
            Item("Offset Polygon", () => _ = vm.OffsetPolygonAsync(), vm.Polygon.Count >= 3),
            new Separator(),
            Item("Save Polygon", () => vm.SavePolygonCommand.Execute(null), vm.HasPolygon),
            Item("Load Polygon", () => vm.LoadPolygonCommand.Execute(null))));
        items.Add(Sub("Auto WP",
            Item("Survey (Grid)", () => vm.SurveyGridCommand.Execute(null), vm.Polygon.Count >= 3)));
        items.Add(Sub("Fence",
            Item("Inclusion from Polygon", () => _ = vm.FenceFromPolygonAsync(inclusion: true), vm.Polygon.Count >= 3),
            Item("Exclusion from Polygon", () => _ = vm.FenceFromPolygonAsync(inclusion: false), vm.Polygon.Count >= 3),
            new Separator(),
            Item("Inclusion Circle Here", () => _ = vm.FenceCircleAsync(at, inclusion: true)),
            Item("Exclusion Circle Here", () => _ = vm.FenceCircleAsync(at, inclusion: false)),
            Item("Circle Around Home", () => _ = vm.FenceHomeCircleAsync(), vm.Home != null),
            Item("Return Point Here", () => vm.FenceReturnPointHere(at)),
            new Separator(),
            Item("Load from File (.fen)", () => _ = vm.LoadFenceFileAsync()),
            Item("Save to File (.fen)", () => _ = vm.SaveFenceFileAsync(), vm.Store.Fence.Count > 0)));
        items.Add(Sub("Rally Points",
            Item("Rally Point Here", () => vm.AddRallyPointHere(at)),
            Item("Load from File (.ral)", () => _ = vm.LoadRallyFileAsync()),
            Item("Save to File (.ral)", () => _ = vm.SaveRallyFileAsync(), vm.Store.Rally.Count > 0)));
        items.Add(Sub("File Load/Save",
            Item("Load WP File (waypoints, plan, mission, KML, SHP)", () => _ = vm.LoadAnyFileAsync(append: false)),
            Item("Load and Append", () => _ = vm.LoadAnyFileAsync(append: true)),
            Item("Save as Plan / Mission", () => _ = vm.SaveAnyFileAsync(), vm.Store.Mission.Count > 0)));
        var poi = hit as Core.Mission.Poi;
        items.Add(Sub("POI",
            Item("Add POI Here", () => _ = vm.Poi.AddHereAsync(at.Lat, at.Lng)),
            Item("Add POI at Coords", () => _ = vm.Poi.AddAtCoordsAsync()),
            Item("Edit POI", () => _ = vm.Poi.EditAsync(poi!), poi != null),
            Item("Delete POI", () => vm.Poi.Delete(poi!), poi != null),
            new Separator(),
            Item("Save POIs", () => _ = vm.Poi.SaveAsync()),
            Item("Load POIs", () => _ = vm.Poi.LoadAsync())));
        items.Add(new Separator());
        items.Add(Item(vm.ClearListText, () => vm.ClearListCommand.Execute(null), vm.CurrentList.Count > 0));
        items.Add(Sub("Map Tool",
            Item(vm.IsMeasuring ? "Measure Distance (second point)" : "Measure Distance", () => _ = vm.MeasureDistanceAsync(at)),
            Item("Reverse WPs", vm.ReverseWaypoints, vm.Store.Mission.Count > 1),
            Item("Modify Alt", () => _ = vm.ModifyAltitudesAsync(), vm.Store.Mission.Count > 0),
            Item("Enter UTM Coord", () => _ = vm.AddUtmWaypointAsync())));
        items.Add(Sub("Zoom",
            Item("Zoom to Mission", () => vm.ZoomToMissionCommand.Execute(null)),
            Item("Zoom to Vehicle", vm.ZoomToVehicle, vm.VehiclePosition != null),
            Item("Zoom to Home", vm.ZoomToHome, vm.Home != null)));

        menu.ItemsSource = items;
        menu.Closed += (_, _) => menu.ItemsSource = null;

        // opened once the press is over, so focus moving off a grid cell cannot close it again
        Map.Focus();
        Dispatcher.UIThread.Post(() => menu.Open(Map), DispatcherPriority.Input);
    }

    private static MenuItem Item(string header, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    private static MenuItem Sub(string header, params Control[] children) =>
        new() { Header = header, ItemsSource = children };
}

internal static class DataGridExtensions
{
    /// <summary>True while a cell is being edited (its TextBox owns the keys).</summary>
    public static bool IsEditing(this DataGrid grid) =>
        TopLevel.GetTopLevel(grid)?.FocusManager?.GetFocusedElement() is TextBox;
}
