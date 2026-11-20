using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MissionPlanner.App.Core;
using MissionPlanner.App.Core.Maps;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.Session;
using MissionPlanner.App.Core.ViewModels;
using MissionPlanner.App.Localization;
using MissionPlanner.App.Services;
using MissionPlanner.App.Views;

namespace MissionPlanner.App;

public sealed class App : Application
{
    private AppServices? _services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var dispatcher = new AvaloniaDispatcher();
        var dialogs = new DialogService();

        // settings, library hooks and the map cache, before anything reads them
        LocalizationSetup.AddBuiltInTables();
        AppStartup.Initialize(dispatcher, dialogs);
        LocalizationSetup.Attach(this);

        if (Environment.GetEnvironmentVariable("MP_LOG_TILES") == "1")
        {
            MapTiles.TileFailed += (provider, zoom, error) =>
                Console.WriteLine($"[tiles] {provider.Name} z{zoom}: {error.GetType().Name}: {error.Message}");
        }

        var windows = new WindowService();
        var themes = new ThemeService();
        themes.ApplySaved();
        _services = new AppServices(dispatcher, dialogs, windows) { Themes = themes };

        // Opt-in bench/SITL convenience (MP_AUTO_STREAM=1): start the HUD stream as soon
        // as the first vehicle link comes up, so a headless run needs no menu click.
        if (Environment.GetEnvironmentVariable("MP_AUTO_STREAM") == "1")
        {
            var services = _services;
            EventHandler<LinkState>? onLink = null;
            onLink = (_, state) =>
            {
                if (state != LinkState.Connected) return;
                services.Session.StateChanged -= onLink;
                dispatcher.Post(() => Core.ViewModels.Video.StreamingViewModel.For(services).Start());
            };
            services.Session.StateChanged += onLink;
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var shell = new ShellViewModel(_services);
            var window = new MainWindow { DataContext = shell };
            window.Opened += (_, _) =>
                Console.WriteLine($"[startup] first window shown {Program.SinceProcessStart.TotalMilliseconds:F0} ms after process start");
            desktop.MainWindow = window;

            // one UI tick drives every on-screen telemetry readout (20 Hz)
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) =>
            {
                shell.Tick();
                windows.Tick();
            });
            timer.Start();

            desktop.ShutdownRequested += (_, _) =>
            {
                shell.Shutdown();
                timer.Stop();
                _services.Session.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
                AppStartup.Shutdown();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
