using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MissionPlanner.App.Core;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.ViewModels.Mobile;
using MissionPlanner.App.Localization;
using MissionPlanner.Mobile.Services;
using MissionPlanner.Mobile.Views;

namespace MissionPlanner.Mobile;

/// <summary>
/// The phone app. On Android it is one view (MobileShellView); the desktop preview puts the same
/// view in a phone-sized window. Platform heads call <see cref="BeforeStart"/> first when they
/// need to place the data folder.
/// </summary>
public sealed partial class MobileApp : Application
{
    private DispatcherTimer? _timer;

    /// <summary>Set by the Android head: where settings, logs and map tiles live.</summary>
    public static string? DataDirectory { get; set; }

    public static MobileShellViewModel? Shell { get; private set; }

    /// <summary>A link to open at start (the desktop preview's MP_CONNECT, e.g. tcp:127.0.0.1:5760).</summary>
    public static string? StartLink { get; set; }

    public static AppServices? AppServices { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (DataDirectory != null)
            MissionPlanner.Utilities.Settings.CustomUserDataDirectory = DataDirectory;

        var sheets = new SheetHost();
        TopLevel? top = null;
        var dispatcher = new MobileDispatcher();
        var dialogs = new MobileDialogService(sheets, () => top);

        LocalizationSetup.AddBuiltInTables();
        AppStartup.Initialize(dispatcher, dialogs);
        LocalizationSetup.Attach(this);

        var services = new AppServices(dispatcher, dialogs, new MobileWindowService(dialogs));
        AppServices = services;
        var shell = new MobileShellViewModel(services);
        Shell = shell;
        var view = new MobileShellView { DataContext = shell, Sheets = sheets };

        switch (ApplicationLifetime)
        {
            case ISingleViewApplicationLifetime single:
                single.MainView = view;
                break;
            case IClassicDesktopStyleApplicationLifetime desktop:
                var window = new Window
                {
                    Title = "Mission Planner (phone preview)",
                    Width = 412,
                    Height = 892,
                    CanResize = true,
                    Content = view,
                    Background = Avalonia.Media.Brushes.Black,
                };
                desktop.MainWindow = window;
                desktop.ShutdownRequested += (_, _) => Stop(services);
                break;
        }

        view.AttachedToVisualTree += (_, _) =>
        {
            top = TopLevel.GetTopLevel(view);
            if (StartLink is { Length: > 0 } link)
                _ = shell.Link.ConnectAsync(MissionPlanner.App.Core.Links.LinkSpec.Parse(link));
            else
                _ = shell.Link.ConnectAtStartAsync();
        };

        // one UI tick drives every readout (20 Hz), as on the desktop
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) => shell.Tick());
        _timer.Start();

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Saves the settings and closes the link (app closing or sent to the background for good).</summary>
    public static void Stop(AppServices services)
    {
        try
        {
            services.Session.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
            // closing anyway
        }

        AppStartup.Shutdown();
    }
}
