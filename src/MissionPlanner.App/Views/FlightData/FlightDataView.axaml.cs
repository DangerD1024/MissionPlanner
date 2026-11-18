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

    public FlightDataView()
    {
        InitializeComponent();

        _flyTo = Item("Fly To Here", () => _vm?.FlyToHereAsync(_menuAt.Lat, _menuAt.Lng));
        _flyToAlt = Item("Fly To Here Alt", () => _vm?.FlyToHereAltAsync(_menuAt.Lat, _menuAt.Lng));
        _setHome = Item("Set Home Here", () => _vm?.SetHomeHereAsync(_menuAt.Lat, _menuAt.Lng));
        _menu = new MenuFlyout
        {
            Items =
            {
                _flyTo,
                _flyToAlt,
                _setHome,
                new Separator(),
                Item("Clear Track", () => _vm?.ClearTrack()),
                Item("Zoom to vehicle", () => _vm?.ZoomToVehicle()),
            },
        };

        Map.UserPanned += (_, _) => _vm?.OnUserPanned();
        Map.PointerPositionChanged += (_, p) => _vm?.OnPointerMoved(p.Lat, p.Lng);
        Map.MapContextRequested += OnMapContext;
        Map.MapClick += OnMapClick;

        // video behind the HUD, and the HUD's right-click Video menu
        HudVideoPresenter.Install(HudHost, HudControl);

        // keep the HUD at 4:3 of the column width, leaving room for the tabs
        LeftColumn.SizeChanged += (_, e) => SizeHud(e.NewSize);

        DataContextChanged += (_, _) => Attach(DataContext as FlightDataViewModel);
    }

    private void Attach(FlightDataViewModel? vm)
    {
        if (_vm != null)
            _vm.TelemetryUpdated -= OnTelemetry;
        _vm = vm;
        Map.Layers.Clear();
        _mapVersion = -1;
        if (vm == null)
            return;

        vm.TelemetryUpdated += OnTelemetry;
        Map.Layers.Add(new MissionLayer(vm.MapModel));
        Map.Layers.Add(new TrackLayer(vm.MapModel));
        Map.Layers.Add(new VehicleLayer(vm.MapModel));
    }

    private void SizeHud(Size column)
    {
        var height = Math.Round(column.Width * 0.75);
        height = Math.Min(height, Math.Max(160, column.Height - MinTabsHeight));
        if (Math.Abs(HudHost.Height - height) > 0.5)
            HudHost.Height = height;
    }

    private void OnTelemetry(object? sender, EventArgs e)
    {
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
        _flyTo.IsEnabled = connected;
        _flyToAlt.IsEnabled = connected;
        _setHome.IsEnabled = connected;
        _menu.ShowAt(Map, showAtPointer: true);
    }

    private void OnMapClick(object? sender, MapPointerEventArgs e)
    {
        // Ctrl+click flies there, as in Mission Planner
        if (_vm != null && e.Modifiers.HasFlag(KeyModifiers.Control) && _vm.CanCommand)
            _ = _vm.FlyToHereAsync(e.Position.Lat, e.Position.Lng);
    }

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }
}
