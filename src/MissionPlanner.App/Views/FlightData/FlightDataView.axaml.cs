using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using GMap.NET;
using MissionPlanner.App.Controls;
using MissionPlanner.App.Controls.FlightData;
using MissionPlanner.App.Controls.Video;
using MissionPlanner.App.Core.ViewModels.FlightData;

namespace MissionPlanner.App.Views.FlightData;

public sealed partial class FlightDataView : UserControl
{
    /// <summary>The tabs keep at least this much height when the column is wide.</summary>
    private const double MinTabsHeight = 240;

    private FlightDataViewModel? _vm;
    private long _mapVersion = -1;
    private PointLatLng _menuAt;
    private readonly MenuFlyout _menu;
    private readonly MenuItem _flyTo;
    private readonly MenuItem _flyToAlt;
    private readonly MenuItem _setHome;
    private readonly MenuItem _pointCamera;
    private readonly MenuItem[] _vehicleItems;
    private readonly MenuItem _poiEdit;
    private readonly MenuItem _poiDelete;
    private Core.Mission.Poi? _menuPoi;

    public FlightDataView()
    {
        InitializeComponent();

        _flyTo = Item("Fly To Here", () => _vm?.FlyToHereAsync(_menuAt.Lat, _menuAt.Lng));
        _flyToAlt = Item("Fly To Here Alt", () => _vm?.FlyToHereAltAsync(_menuAt.Lat, _menuAt.Lng));
        _setHome = Item("Set Home Here", () => _vm?.SetHomeHereAsync(_menuAt.Lat, _menuAt.Lng));
        _pointCamera = Item("Point Camera Here", () => _vm?.PointCameraHereAsync(_menuAt.Lat, _menuAt.Lng));
        var flyToCoords = Item("Fly To Coords", () => _vm?.FlyToCoordsAsync());
        var pointCoords = Item("Point Camera Coords", () => _vm?.PointCameraCoordsAsync());
        var trigger = Item("Trigger Camera Now", () => _vm?.TriggerCameraAsync());
        var ekf = Item("Set EKF Origin Here", () => _vm?.SetEkfOriginHereAsync(_menuAt.Lat, _menuAt.Lng));
        var takeoff = Item("Takeoff", () => _vm?.TakeoffAsync());
        var jumpTag = Item("Jump to Tag", () => _vm?.JumpToTagAsync());
        _vehicleItems = [_flyTo, _flyToAlt, flyToCoords, _setHome, ekf, _pointCamera, pointCoords, trigger, takeoff, jumpTag];
        _poiEdit = Item("Edit POI", () =>
        {
            if (_vm != null && _menuPoi != null)
                _ = _vm.Poi.EditAsync(_menuPoi);
        });
        _poiDelete = Item("Delete POI", () =>
        {
            if (_vm != null && _menuPoi != null)
                _vm.Poi.Delete(_menuPoi);
        });
        var poiMenu = new MenuItem
        {
            Header = "POI",
            ItemsSource = new Control[]
            {
                Item("Add POI Here", () => _vm?.Poi.AddHereAsync(_menuAt.Lat, _menuAt.Lng)),
                Item("Add POI at Coords", () => _vm?.Poi.AddAtCoordsAsync()),
                _poiEdit,
                _poiDelete,
                new Separator(),
                Item("Save POIs", () => _vm?.Poi.SaveAsync()),
                Item("Load POIs", () => _vm?.Poi.LoadAsync()),
            },
        };
        _menu = new MenuFlyout
        {
            Items =
            {
                poiMenu,
                new Separator(),
                _flyTo,
                _flyToAlt,
                flyToCoords,
                new Separator(),
                _pointCamera,
                pointCoords,
                trigger,
                new Separator(),
                takeoff,
                jumpTag,
                _setHome,
                ekf,
                new Separator(),
                Item("Clear Track", () => _vm?.ClearTrack()),
                Item("Zoom to vehicle", () => _vm?.ZoomToVehicle()),
            },
        };

        HudControl.ItemClicked += (_, item) => _vm?.OpenHudItem(item);
        Map.UserPanned += (_, _) => _vm?.OnUserPanned();
        Map.PointerPositionChanged += (_, p) => _vm?.OnPointerMoved(p.Lat, p.Lng);
        Map.MapContextRequested += OnMapContext;
        Map.MapClick += OnMapClick;

        // video behind the HUD, and the HUD's right-click menu (Video, Detach HUD)
        HudVideoPresenter.Install(HudHost, HudControl, items =>
        {
            if (_vm == null)
                return;
            items.Add(new Separator());
            items.Add(HudVideoMenu.Item("HUD options…", () => _ = _vm.OpenHudOptionsAsync()));
            items.Add(HudVideoMenu.Item("Swap with map", () => _vm.SwapHudCommand.Execute(null)));
            items.Add(HudVideoMenu.Item("Detach HUD (own window)", () => _vm.IsHudDetached = true));
        });

        // keep the HUD at 4:3 of the column width, leaving room for the tabs
        LeftColumn.SizeChanged += (_, e) => SizeHud(e.NewSize);

        DataContextChanged += (_, _) => Attach(DataContext as FlightDataViewModel);

        // while a telemetry log plays: Space pauses, + / - change the speed (not while typing)
        AddHandler(KeyDownEvent, OnPlaybackKey, Avalonia.Interactivity.RoutingStrategies.Bubble);
    }

    private void Attach(FlightDataViewModel? vm)
    {
        if (_vm != null)
        {
            _vm.TelemetryUpdated -= OnTelemetry;
            _vm.PropertyChanged -= OnVmChanged;
        }

        _vm = vm;
        Map.Layers.Clear();
        _mapVersion = -1;
        if (vm == null)
            return;

        vm.TelemetryUpdated += OnTelemetry;
        vm.PropertyChanged += OnVmChanged;
        // opens the HUD window now if it was detached when the app last closed
        HudWindowHost.Track(vm);
        HudStreamPump.Track(vm);
        SizeHud(LeftColumn.Bounds.Size);
        LayoutMapArea();
        ApplySwap();
        Map.Layers.Add(new MissionLayer(vm.MapModel));
        Map.Layers.Add(new TrackLayer(vm.MapModel));
        Map.Layers.Add(new AdsbLayer(vm.MapModel));
        Map.Layers.Add(new VehicleLayer(vm.MapModel));
        Map.Layers.Add(new PoiLayer(vm.Services.Pois));
        vm.Services.Pois.Changed -= OnPoisChanged;
        vm.Services.Pois.Changed += OnPoisChanged;
    }

    private void OnPoisChanged(object? sender, EventArgs e) => Map.InvalidateVisual();

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FlightDataViewModel.IsHudDetached))
            SizeHud(LeftColumn.Bounds.Size);
        else if (e.PropertyName == nameof(FlightDataViewModel.IsHudSwapped))
            ApplySwap();
        else if (e.PropertyName == nameof(FlightDataViewModel.MapView))
            LayoutMapArea();
    }

    /// <summary>Moves the HUD onto the map side and the map into the HUD's corner, or back.</summary>
    private void ApplySwap()
    {
        var swapped = _vm?.IsHudSwapped == true;
        var hudSwapped = HudControl.Parent == SwapHost;
        if (swapped == hudSwapped)
            return;
        if (swapped)
        {
            HudPanel.Children.Remove(HudControl);
            Map2DPanel.Children.Remove(Map);
            SwapHost.Children.Insert(0, HudControl);
            HudPanel.Children.Insert(0, Map);
        }
        else
        {
            SwapHost.Children.Remove(HudControl);
            HudPanel.Children.Remove(Map);
            HudPanel.Children.Insert(0, HudControl);
            Map2DPanel.Children.Insert(0, Map);
        }

        SwapHost.IsVisible = swapped;
        HudControl.Refresh();
        Map.InvalidateVisual();
    }

    /// <summary>Rows of the map side: 2D only, 3D only, or 2D on top of 3D with a splitter.</summary>
    private void LayoutMapArea()
    {
        var rows = MapArea.RowDefinitions;
        var mode = _vm?.MapView ?? MapViewMode.Map2D;
        rows[0].Height = mode == MapViewMode.Map3D ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        rows[1].Height = mode == MapViewMode.Split ? new GridLength(4) : new GridLength(0);
        rows[2].Height = mode == MapViewMode.Map2D ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
    }

    private void SizeHud(Size column)
    {
        if (column.Width <= 0)
            return;
        // detached: just a bar with Attach, the tabs get the room
        if (_vm?.IsHudDetached == true)
        {
            HudHost.Height = 56;
            return;
        }

        var height = Math.Round(column.Width * 0.75);
        height = Math.Min(height, Math.Max(160, column.Height - MinTabsHeight));
        if (Math.Abs(HudHost.Height - height) > 0.5)
            HudHost.Height = height;
    }

    private void OnTelemetry(object? sender, EventArgs e)
    {
        if (HudControl.IsVisible)
            HudControl.Refresh();

        // the view-model moves the version at most 10 times a second
        var version = _vm?.MapModel.Version ?? -1;
        if (version != _mapVersion)
        {
            _mapVersion = version;
            Map.InvalidateVisual();
        }
    }

    private void OnMapContext(object? sender, MapPointerEventArgs e)
    {
        if (_vm == null)
            return;
        _menuAt = e.Position;
        var connected = _vm.CanCommand;
        foreach (var item in _vehicleItems)
            item.IsEnabled = connected;
        _menuPoi = e.Hit as Core.Mission.Poi;
        _poiEdit.IsEnabled = _poiDelete.IsEnabled = _menuPoi != null;
        _menu.ShowAt(Map, showAtPointer: true);
    }

    private void OnMapClick(object? sender, MapPointerEventArgs e)
    {
        // a click on an ADS-B aircraft shows what it is
        if (_vm != null && e.Hit is AdsbContact contact)
        {
            _vm.ShowAdsbDetails(contact);
            return;
        }

        // Ctrl+click flies there, as in Mission Planner
        if (_vm != null && e.Modifiers.HasFlag(KeyModifiers.Control) && _vm.CanCommand)
            _ = _vm.FlyToHereAsync(e.Position.Lat, e.Position.Lng);
    }

    private void OnPlaybackKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _vm == null || e.KeyModifiers != KeyModifiers.None || e.Source is TextBox ||
            !_vm.Tlogs.PlayPauseCommand.CanExecute(null))
            return;
        var command = e.Key switch
        {
            Key.Space => _vm.Tlogs.PlayPauseCommand,
            Key.OemPlus or Key.Add => _vm.Tlogs.FasterCommand,
            Key.OemMinus or Key.Subtract => _vm.Tlogs.SlowerCommand,
            _ => null,
        };
        if (command == null)
            return;
        command.Execute(null);
        e.Handled = true;
    }

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }
}
