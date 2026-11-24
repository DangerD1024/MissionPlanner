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

    /// <summary>Raised once the shell exists (the Android head applies the orientation setting from it).</summary>
    public static event Action<MobileShellViewModel>? ShellCreated;

    /// <summary>The desktop preview starts in landscape (--landscape).</summary>
    public static bool StartLandscape { get; set; }

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
        // development and store screenshots: MP_START_TAB=Plan opens that tab (the simulator has no taps from scripts)
        if (Enum.TryParse<MobileTab>(Environment.GetEnvironmentVariable("MP_START_TAB"), true, out var startTab))
            shell.SelectTabCommand.Execute(startTab);
        ShellCreated?.Invoke(shell);
        var view = new MobileShellView { DataContext = shell, Sheets = sheets };

        switch (ApplicationLifetime)
        {
            case ISingleViewApplicationLifetime single:
                single.MainView = view;
                break;
            case IClassicDesktopStyleApplicationLifetime desktop:
                var landscape = StartLandscape || shell.Orientation == ScreenOrientationMode.Landscape;
                var window = new Window
                {
                    Title = "Mission Planner (phone preview)",
                    Width = landscape ? 892 : 412,
                    Height = landscape ? 412 : 892,
                    CanResize = true,
                    Content = view,
                    Background = Avalonia.Media.Brushes.Black,
                };
                desktop.MainWindow = window;
                // the preview turns like a phone when the orientation is set
                shell.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName != nameof(MobileShellViewModel.Orientation) || shell.Orientation == ScreenOrientationMode.Auto)
                        return;
                    var wide = shell.Orientation == ScreenOrientationMode.Landscape;
                    if (wide != window.Width > window.Height)
                        (window.Width, window.Height) = (window.Height, window.Width);
                };
                desktop.ShutdownRequested += (_, _) => Stop(services);
                break;
        }

        // MP_CONNECT works on the phones too (simulator: SIMCTL_CHILD_MP_CONNECT)
        StartLink ??= Environment.GetEnvironmentVariable("MP_CONNECT");
        view.AttachedToVisualTree += (_, _) =>
        {
            top = TopLevel.GetTopLevel(view);
            if (Environment.GetEnvironmentVariable("MP_DEMO_TOUR") == "1" && StartLink is { Length: > 0 } tourLink)
                _ = DemoTourAsync(shell, tourLink);
            else if (StartLink is { Length: > 0 } link)
                _ = shell.Link.ConnectAsync(MissionPlanner.App.Core.Links.LinkSpec.Parse(link));
            else
                _ = shell.Link.ConnectAtStartAsync();
        };

        // one UI tick drives every readout (20 Hz), as on the desktop
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) => shell.Tick());
        _timer.Start();

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// MP_DEMO_TOUR=1 with MP_CONNECT: a hands-free walk through the app for a screen recording (the iOS
    /// simulator takes no taps from scripts): the Link tab, connecting, then Fly, Plan, Setup and Messages.
    /// </summary>
    private static async Task DemoTourAsync(MobileShellViewModel shell, string link)
    {
        async Task Show(MobileTab tab, int seconds)
        {
            shell.SelectTabCommand.Execute(tab);
            await Task.Delay(TimeSpan.FromSeconds(seconds));
        }

        await Show(MobileTab.Link, 4);
        var connect = shell.Link.ConnectAsync(MissionPlanner.App.Core.Links.LinkSpec.Parse(link));
        await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(40)));
        await Task.Delay(TimeSpan.FromSeconds(3));
        shell.SelectTabCommand.Execute(MobileTab.Fly);
        await Task.Delay(TimeSpan.FromSeconds(6));
        // take off as the slide-to-takeoff control does (Guided, arm, NAV_TAKEOFF), without its prompts
        var session = AppServices!.Session;
        if (session.IsConnected)
        {
            await Task.Run(() =>
            {
                var mav = session.Mav;
                var v = session.Vehicle;
                for (var i = 0; i < 15 && !mav.MAV.cs.armed; i++)
                {
                    mav.setMode(v.sysid, v.compid, "GUIDED");
                    if (!mav.doARM(v.sysid, v.compid, true))
                        Thread.Sleep(2000);
                }

                mav.doCommand(v.sysid, v.compid, MAVLink.MAV_CMD.TAKEOFF, 0, 0, 0, 0, 0, 0, 20);
            });
        }

        await Task.Delay(TimeSpan.FromSeconds(25));
        await Show(MobileTab.Plan, 12);
        await Show(MobileTab.Setup, 12);
        await Show(MobileTab.Messages, 8);
        await Show(MobileTab.Link, 6);
        await Show(MobileTab.Fly, 10);
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
