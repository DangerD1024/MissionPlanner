using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using MissionPlanner.App.Controls;
using MissionPlanner.App.Controls.FlightData;
using MissionPlanner.App.Core.ViewModels.FlightData;
using MissionPlanner.App.Core.ViewModels.Mobile;

namespace MissionPlanner.Mobile.Views;

/// <summary>
/// Portrait: HUD, telemetry strip, map, action bar from top to bottom. Landscape (as DJI Fly):
/// one view fills the screen and the other is a small picture in the corner (tap it to swap),
/// the action buttons are a column on the right and the telemetry runs along the bottom.
/// </summary>
public sealed partial class FlyView : UserControl
{
    private MobileShellViewModel? _vm;
    private long _mapVersion = -1;
    private readonly MapMotionPump _motionPump;
    private bool? _landscape;
    private bool? _mapMain;

    public FlyView()
    {
        InitializeComponent();
        _motionPump = new MapMotionPump(Map, () => _vm?.Fly, RedrawMap);
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
        switch (e.PropertyName)
        {
            case nameof(MobileShellViewModel.IsHudLarge):
            case nameof(MobileShellViewModel.IsMapMain):
                InvalidateMeasure();
                break;
            case nameof(MobileShellViewModel.GoHere):
                Map.InvalidateVisual();
                break;
        }
    }

    private void OnSwapClick(object? sender, RoutedEventArgs e)
    {
        if (_landscape == true)
            _vm?.SwapViewsCommand.Execute(null);
        else
            _vm?.ToggleHudSizeCommand.Execute(null);
    }

    private void OnMiniPressed(object? sender, PointerPressedEventArgs e)
    {
        _vm?.SwapViewsCommand.Execute(null);
        e.Handled = true;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = availableSize.Width;
        var h = availableSize.Height;
        if (w > 0 && h > 0 && !double.IsInfinity(w) && !double.IsInfinity(h))
            Layout(w > h * 1.05, w, h);
        return base.MeasureOverride(availableSize);
    }

    private void Layout(bool landscape, double w, double h)
    {
        var mapMain = _vm?.IsMapMain == true;
        if (landscape != _landscape || mapMain != _mapMain)
        {
            _landscape = landscape;
            _mapMain = mapMain;
            if (landscape)
                ToLandscape(mapMain);
            else
                ToPortrait();
            HudControl.Refresh();
            Map.InvalidateVisual();
        }

        if (landscape)
        {
            // the small picture: about a third of the width, 4:3, in the bottom-left corner
            var mini = mapMain ? (Control)HudHost : MapHost;
            var mw = Math.Round(Math.Min(w * 0.3, 300));
            var mh = Math.Round(mw * 0.72);
            if (Math.Abs(mini.Width - mw) > 0.5 || double.IsNaN(mini.Width))
            {
                mini.Width = mw;
                mini.Height = mh;
                mini.Clip = new Avalonia.Media.RectangleGeometry(new Rect(0, 0, mw, mh), 12, 12);
                MiniTap.Width = mw;
                MiniTap.Height = mh;
            }

            return;
        }

        // portrait: the HUD takes 2/3 of the width in height (large) or 0.4 (small); the map the rest
        var large = _vm?.IsHudLarge ?? true;
        var hud = Math.Round(w * (large ? 0.68 : 0.4));
        hud = Math.Min(hud, Math.Max(140, h * 0.45));
        if (Math.Abs(HudHost.Height - hud) > 0.5 || double.IsNaN(HudHost.Height))
            HudHost.Height = hud;
    }

    private void ToPortrait()
    {
        Root.RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto");
        Root.ColumnDefinitions = new ColumnDefinitions("*");
        Place(HudHost, 0, 0);
        Place(Tele, 1, 0);
        Place(MapHost, 2, 0);
        Place(ActionBar, 3, 0);
        Fill(HudHost);
        Fill(MapHost);
        HudHost.Height = double.NaN; // set again by Layout
        Actions.Columns = 5;
        Actions.Rows = 1;
        foreach (var b in Actions.Children)
            b.Height = 64;
        ActionBar.Padding = new Thickness(5, 5, 5, 3);
        ActionBar.Width = double.NaN;
        MiniTap.IsVisible = false;
        MapButtons.IsVisible = true;
        SwapButton.IsVisible = true;
    }

    private void ToLandscape(bool mapMain)
    {
        Root.RowDefinitions = new RowDefinitions("*,Auto");
        Root.ColumnDefinitions = new ColumnDefinitions("*,Auto");
        var main = mapMain ? (Control)MapHost : HudHost;
        var mini = mapMain ? (Control)HudHost : MapHost;
        Place(main, 0, 0);
        Place(mini, 0, 0);
        Place(MiniTap, 0, 0);
        Place(Tele, 1, 0);
        Place(ActionBar, 0, 1, rowSpan: 2);
        Fill(main);
        foreach (var c in new Control[] { mini, MiniTap })
        {
            c.HorizontalAlignment = HorizontalAlignment.Left;
            c.VerticalAlignment = VerticalAlignment.Bottom;
            c.Margin = new Thickness(10);
        }

        mini.ZIndex = 2;
        MiniTap.IsVisible = true;
        // the map's buttons only on the big map; the HUD's swap button only on the big HUD
        MapButtons.IsVisible = mapMain;
        SwapButton.IsVisible = !mapMain;
        Actions.Columns = 1;
        Actions.Rows = 5;
        // the five share the height of a short landscape screen
        foreach (var b in Actions.Children)
            b.Height = double.NaN;
        ActionBar.Padding = new Thickness(4, 6, 6, 6);
        ActionBar.Width = 92;
    }

    private static void Fill(Control c)
    {
        c.Clip = null;
        c.Width = double.NaN;
        c.Height = double.NaN;
        c.HorizontalAlignment = HorizontalAlignment.Stretch;
        c.VerticalAlignment = VerticalAlignment.Stretch;
        c.Margin = default;
        c.ZIndex = 0;
    }

    private static void Place(Control c, int row, int column, int rowSpan = 1)
    {
        Grid.SetRow(c, row);
        Grid.SetColumn(c, column);
        Grid.SetRowSpan(c, rowSpan);
        Grid.SetColumnSpan(c, 1);
    }

    private void OnTelemetry(object? sender, EventArgs e)
    {
        if (!IsEffectivelyVisible)
            return;
        HudControl.Refresh();
        RedrawMap();
        // a moving vehicle is moved on every display frame from here on, until it stops
        _motionPump.Kick();
    }

    private void RedrawMap()
    {
        var version = _vm?.Fly.MapModel.Version ?? -1;
        if (version != _mapVersion)
        {
            _mapVersion = version;
            Map.InvalidateVisual();
        }
    }
}
