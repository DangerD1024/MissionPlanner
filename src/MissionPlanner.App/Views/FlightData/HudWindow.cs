using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using MissionPlanner.App.Controls;
using MissionPlanner.App.Controls.Video;
using MissionPlanner.App.Core.ViewModels.FlightData;
using MissionPlanner.App.Core.Localization;
using MissionPlanner.App.Localization;

namespace MissionPlanner.App.Views.FlightData;

/// <summary>
/// The HUD in a window of its own, to put on another monitor and go fullscreen (F11 or double-click;
/// Esc leaves fullscreen). Same HUD model, video and right-click menu as the DATA screen. Closing it
/// puts the HUD back above the tabs. Its place and fullscreen state are remembered.
/// </summary>
public sealed class HudWindow : Window
{
    private readonly FlightDataViewModel _vm;
    private readonly Hud _hud;

    public HudWindow(FlightDataViewModel vm)
    {
        _vm = vm;
        _hud = new Hud { Model = vm.Hud };
        var host = new Border { Background = new SolidColorBrush(Color.Parse("#0b0d10")), Child = _hud, DataContext = vm };
        Content = host;
        Width = 960;
        Height = 720;
        MinWidth = 240;
        MinHeight = 180;
        Background = Brushes.Black;
        TrBinding.Bind(this, TitleProperty, "HUD");
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            Icon = main.Icon;

        HudVideoPresenter.Install(host, _hud, AddWindowItems);
        vm.TelemetryUpdated += OnTelemetry;
        KeyDown += OnKey;
        host.DoubleTapped += (_, _) => ToggleFullScreen();
        RestorePlacement();
    }

    /// <summary>Set when the app is closing: the window closes but stays "detached" for the next start.</summary>
    public bool KeepDetachedOnClose { get; set; }

    public bool IsFullScreen => WindowState == WindowState.FullScreen;

    public void ToggleFullScreen() => WindowState = IsFullScreen ? WindowState.Normal : WindowState.FullScreen;

    /// <summary>Moves to a monitor and fills it.</summary>
    public void FullScreenOn(Screen screen)
    {
        WindowState = WindowState.Normal;
        var area = screen.WorkingArea;
        Position = new PixelPoint(area.X + 40, area.Y + 40);
        WindowState = WindowState.FullScreen;
    }

    private void AddWindowItems(ItemCollection items)
    {
        items.Add(new Separator());
        var full = HudVideoMenu.Item(IsFullScreen ? "Leave fullscreen (Esc)" : "Fullscreen (F11)", ToggleFullScreen);
        items.Add(full);

        var all = Screens.All;
        if (all.Count > 1)
        {
            var on = new MenuItem { Header = L.Tr("Fullscreen on") };
            for (var i = 0; i < all.Count; i++)
            {
                var screen = all[i];
                var name = string.IsNullOrWhiteSpace(screen.DisplayName) ? L.Format("Monitor {0}", i + 1) : screen.DisplayName;
                var size = $"{screen.Bounds.Width}×{screen.Bounds.Height}";
                on.Items.Add(HudVideoMenu.Item($"{name} ({size}){(screen.IsPrimary ? " ★" : "")}", () => FullScreenOn(screen)));
            }

            items.Add(on);
        }

        items.Add(HudVideoMenu.Item("Attach HUD (back to the DATA screen)", Close));
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleFullScreen();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && IsFullScreen)
        {
            WindowState = WindowState.Normal;
            e.Handled = true;
        }
    }

    private void OnTelemetry(object? sender, EventArgs e) => _hud.Refresh();

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        SavePlacement();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.TelemetryUpdated -= OnTelemetry;
        if (!KeepDetachedOnClose)
            _vm.IsHudDetached = false;
        base.OnClosed(e);
    }

    private void SavePlacement()
    {
        var full = IsFullScreen;
        // the size to come back to is the normal one, not the fullscreen one
        var w = full ? Width : Bounds.Width;
        var h = full ? Height : Bounds.Height;
        _vm.HudWindowPlacement = string.Join(",",
            Position.X.ToString(CultureInfo.InvariantCulture), Position.Y.ToString(CultureInfo.InvariantCulture),
            ((int)w).ToString(CultureInfo.InvariantCulture), ((int)h).ToString(CultureInfo.InvariantCulture),
            full ? "1" : "0");
    }

    private void RestorePlacement()
    {
        var parts = _vm.HudWindowPlacement.Split(',');
        var inv = CultureInfo.InvariantCulture;
        if (parts.Length != 5 || !int.TryParse(parts[0], NumberStyles.Integer, inv, out var x) ||
            !int.TryParse(parts[1], NumberStyles.Integer, inv, out var y) ||
            !int.TryParse(parts[2], NumberStyles.Integer, inv, out var w) ||
            !int.TryParse(parts[3], NumberStyles.Integer, inv, out var h))
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            return;
        }

        // only where a monitor still is (one may have been unplugged since)
        if (Screens.ScreenFromPoint(new PixelPoint(x + 20, y + 20)) == null)
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(x, y);
        Width = Math.Max(MinWidth, w);
        Height = Math.Max(MinHeight, h);
        if (parts[4] == "1")
            Opened += (_, _) => WindowState = WindowState.FullScreen;
    }
}

/// <summary>Opens and closes the <see cref="HudWindow"/> as <see cref="FlightDataViewModel.IsHudDetached"/> changes.</summary>
public static class HudWindowHost
{
    private static FlightDataViewModel? _tracked;
    private static HudWindow? _window;
    private static bool _shuttingDown;

    /// <summary>Starts following the view-model (once; later calls are ignored).</summary>
    public static void Track(FlightDataViewModel vm)
    {
        if (ReferenceEquals(_tracked, vm))
            return;
        _tracked = vm;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FlightDataViewModel.IsHudDetached))
                Sync(vm);
        };

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // closing the app closes the HUD window, but it opens again detached next time
            desktop.ShutdownRequested += (_, _) => CloseForShutdown();
            if (desktop.MainWindow is { } main)
                main.Closing += (_, _) => CloseForShutdown();
        }

        Sync(vm);
    }

    private static void CloseForShutdown()
    {
        _shuttingDown = true;
        if (_window != null)
        {
            _window.KeepDetachedOnClose = true;
            _window.Close();
        }
    }

    private static void Sync(FlightDataViewModel vm)
    {
        if (vm.IsHudDetached && _window == null && !_shuttingDown)
        {
            var window = new HudWindow(vm);
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_window, window))
                    _window = null;
            };
            _window = window;
            window.Show();
            window.Activate();
        }
        else if (!vm.IsHudDetached && _window != null)
        {
            var window = _window;
            _window = null;
            window.Close();
        }
    }
}
