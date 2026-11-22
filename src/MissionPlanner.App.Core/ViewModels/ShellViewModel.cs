using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.Session;

namespace MissionPlanner.App.Core.ViewModels;

/// <summary>A top-level or menu screen.</summary>
public abstract class PageViewModel : ObservableObject
{
    protected PageViewModel(string title) => Title = title;

    public string Title { get; }

    /// <summary>Called about 20 times a second on the UI thread while the page is on screen.</summary>
    public virtual void Tick()
    {
    }

    /// <summary>Called on the UI thread when the page comes on screen.</summary>
    public virtual void OnShown()
    {
    }

    /// <summary>Called on the UI thread when another page replaces it.</summary>
    public virtual void OnHidden()
    {
    }
}

/// <summary>A screen that is not built yet; says what it will hold.</summary>
public sealed class PlaceholderPageViewModel(string title, string arrives) : PageViewModel(title)
{
    public string Arrives { get; } = arrives;
}

public enum Screen
{
    Data,
    Plan,
    Setup,
    Config,
    Simulation,
    Settings,

    /// <summary>Mission Planner's Ctrl+F screen; not on the top bar.</summary>
    Tools,

    /// <summary>Version, updates, documentation and forum links.</summary>
    Help,
}

/// <summary>Main window: top navigation, connect controls and the visible screen.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Dictionary<Screen, PageViewModel> _pages = new();
    private readonly Dictionary<Screen, Func<PageViewModel>> _factories;
    private int _tick;

    public ShellViewModel(AppServices services)
    {
        _services = services;
        Connection = new ConnectionViewModel(services);
        _factories = new()
        {
            [Screen.Data] = () => new FlightData.FlightDataViewModel(services),
            [Screen.Plan] = () => new FlightPlan.FlightPlanViewModel(services),
            [Screen.Setup] = () => new SetupScreens.SetupViewModel(services),
            [Screen.Config] = () => new ConfigScreens.ConfigViewModel(services),
            [Screen.Simulation] = () => new Simulation.SimulationViewModel(services),
            [Screen.Settings] = () => new ConfigScreens.PlannerSettingsViewModel(services),
            [Screen.Tools] = () => new Tools.ToolsViewModel(services),
            [Screen.Help] = () => new HelpViewModel { Updates = services.Updates },
        };
        _currentPage = GetPage(Screen.Data);
        _currentPage.OnShown();
        services.Session.StateChanged += (_, _) => services.Dispatcher.Post(() => OnPropertyChanged(nameof(Title)));
        services.ScreenRequested += (_, screen) => services.Dispatcher.Post(() => Navigate(screen));
        services.MenuPageRequested += (_, r) => services.Dispatcher.Post(() => ShowMenuPage(r.Screen, r.Title));
        services.Notified += (_, n) => services.Dispatcher.Post(() => ShowToast(n));
    }

    /// <summary>The update banner at the top of the window.</summary>
    public Updates.UpdateViewModel Updates => _services.Updates;

    // ---- toast (AppServices.Notify) -----------------------------------------------------------

    [ObservableProperty]
    private string _toastText = "";

    [ObservableProperty]
    private bool _isToastVisible;

    [ObservableProperty]
    private bool _toastIsError;

    [ObservableProperty]
    private bool _toastIsWarning;

    private int _toastId;

    private async void ShowToast(Notification n)
    {
        ToastText = n.Text;
        ToastIsError = n.Kind == MessageKind.Error;
        ToastIsWarning = n.Kind == MessageKind.Warning;
        IsToastVisible = true;
        var id = ++_toastId;
        await Task.Delay(n.Kind == MessageKind.Info ? 3500 : 7000);
        if (id == _toastId)
            IsToastVisible = false;
    }

    [RelayCommand]
    private void DismissToast() => IsToastVisible = false;

    public ConnectionViewModel Connection { get; }

    public string Title
    {
        get
        {
            var s = _services.Session;
            if (!s.IsConnected)
                return AppVersion.ProductName;
            var cs = s.Cs;
            return $"{AppVersion.ProductName} — {cs.firmware} V{cs.version?.ToString(3)}";
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsData), nameof(IsPlan), nameof(IsSetup), nameof(IsConfig), nameof(IsSimulation), nameof(IsSettings), nameof(IsTools), nameof(IsHelp))]
    private Screen _currentScreen;

    [ObservableProperty]
    private PageViewModel _currentPage;

    public bool IsData => CurrentScreen == Screen.Data;
    public bool IsPlan => CurrentScreen == Screen.Plan;
    public bool IsSetup => CurrentScreen == Screen.Setup;
    public bool IsConfig => CurrentScreen == Screen.Config;
    public bool IsSimulation => CurrentScreen == Screen.Simulation;
    public bool IsSettings => CurrentScreen == Screen.Settings;
    public bool IsTools => CurrentScreen == Screen.Tools;
    public bool IsHelp => CurrentScreen == Screen.Help;

    // ---- Mission Planner's hotkeys (MainV2.ProcessCmdKey) ---------------------------------------

    /// <summary>F12: connect or disconnect.</summary>
    [RelayCommand]
    private Task ConnectHotkey() =>
        Connection.ToggleConnectCommand.CanExecute(null) ? Connection.ToggleConnectCommand.ExecuteAsync(null) : Task.CompletedTask;

    /// <summary>Shows a screen and the page with this title in its left menu.</summary>
    public void ShowMenuPage(Screen screen, string title)
    {
        Navigate(screen);
        if (GetPage(screen) is MenuScreenViewModel menu && !menu.Show(title))
            _services.Notify(L.Format("{0} is not available for this vehicle.", title), MessageKind.Warning);
    }

    /// <summary>Ctrl+G (Mission Planner's NMEA output): TOOLS > Outputs.</summary>
    [RelayCommand]
    private void OutputsHotkey() => ShowMenuPage(Screen.Tools, Tools.ToolsViewModel.OutputsTitle);

    /// <summary>Ctrl+X (Mission Planner's map cache window): TOOLS > Map cache.</summary>
    [RelayCommand]
    private void MapCacheHotkey() => ShowMenuPage(Screen.Tools, Tools.ToolsViewModel.MapCacheTitle);

    /// <summary>Ctrl+J (Mission Planner's devops window): TOOLS > Bus registers.</summary>
    [RelayCommand]
    private void DeviceOpHotkey() => ShowMenuPage(Screen.Tools, Tools.ToolsViewModel.DeviceOpTitle);

    /// <summary>F5: download the parameters again.</summary>
    [RelayCommand]
    private async Task RefreshParamsHotkey()
    {
        if (!_services.Session.IsConnected)
            return;
        _services.Notify("Getting parameters…");
        try
        {
            await _services.Params.RefreshAsync();
            _services.Notify($"{_services.Params.Count} parameters loaded.");
        }
        catch (Exception ex)
        {
            _services.Notify("Could not get the parameters: " + ex.Message, MessageKind.Error);
        }
    }

    /// <summary>Ctrl+Y: write the parameters to the autopilot's flash (PREFLIGHT_STORAGE).</summary>
    [RelayCommand]
    private async Task CommitParamsHotkey()
    {
        if (!_services.Session.IsConnected)
            return;
        bool ok;
        try
        {
            ok = await _services.Params.CommitToFlashAsync();
        }
        catch (Exception)
        {
            ok = false;
        }

        _services.Notify(ok ? "Parameters written to flash." : "The autopilot did not confirm writing the parameters to flash.",
            ok ? MessageKind.Info : MessageKind.Error);
    }

    [RelayCommand]
    private void Navigate(Screen screen)
    {
        if (screen == CurrentScreen)
            return;
        CurrentPage.OnHidden();
        CurrentScreen = screen;
        CurrentPage = GetPage(screen);
        CurrentPage.OnShown();
    }

    public T GetPage<T>(Screen screen) where T : PageViewModel => (T)GetPage(screen);

    private PageViewModel GetPage(Screen screen)
    {
        if (!_pages.TryGetValue(screen, out var page))
        {
            page = _factories[screen]();
            _pages[screen] = page;
        }

        return page;
    }

    /// <summary>Driven by a UI timer at about 20 Hz.</summary>
    public void Tick()
    {
        CurrentPage.Tick();
        // the HUD window, 3D view and stream keep the HUD going while another screen is shown
        if (CurrentPage is not FlightData.FlightDataViewModel && _pages.TryGetValue(Screen.Data, out var data) &&
            data is FlightData.FlightDataViewModel { NeedsBackgroundTick: true } flight)
            flight.BackgroundTick();
        Connection.Tick();
        if (++_tick % 20 == 0)
        {
            Connection.RefreshVehicles();
            OnPropertyChanged(nameof(Title));
        }
    }
}

/// <summary>HELP: version and updates, data and log folders, documentation and forum links, the Linux menu entry.</summary>
public sealed partial class HelpViewModel() : PageViewModel("Help")
{
    public bool IsLinux { get; } = OperatingSystem.IsLinux();

    public const string DiscussUrl = "https://discuss.ardupilot.org/t/mission-planner-3-0-rewritten-cross-platform-ui-windows-linux-native-net-10/";
    public const string DocsUrl = "https://ardupilot.org/planner/";
    public const string IssuesUrl = "https://github.com/" + MissionPlanner.App.Core.Updates.UpdateCheck.Repository + "/issues";
    public const string ReleasesUrl = "https://github.com/" + MissionPlanner.App.Core.Updates.UpdateCheck.Repository + "/releases";

    /// <summary>Opens a web page (or a folder) in the system's default app.</summary>
    [RelayCommand]
    private void Open(string? target)
    {
        if (string.IsNullOrEmpty(target))
            return;
        try
        {
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = target, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            DesktopEntryStatus = target;
        }
    }

    /// <summary>Check for updates (null in tests that build the page alone).</summary>
    public Updates.UpdateViewModel? Updates { get; init; }

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private string _desktopEntryStatus = "";

    /// <summary>Adds the app, with its icon, to the Linux applications menu.</summary>
    [RelayCommand]
    private void AddToApplicationsMenu()
    {
        try
        {
            DesktopEntryStatus = DesktopEntry.Install();
        }
        catch (Exception ex)
        {
            DesktopEntryStatus = "Could not add it: " + ex.Message;
        }
    }

    public string Version { get; } = AppVersion.Display;

    public string DataDirectory { get; } = MissionPlanner.Utilities.Settings.GetUserDataDirectory();

    public string LogDirectory { get; } = MissionPlanner.Utilities.Settings.Instance.LogDir;
}
