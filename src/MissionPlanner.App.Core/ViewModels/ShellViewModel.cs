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
            [Screen.Help] = () => new HelpViewModel(),
        };
        _currentPage = GetPage(Screen.Data);
        _currentPage.OnShown();
        services.Session.StateChanged += (_, _) => services.Dispatcher.Post(() => OnPropertyChanged(nameof(Title)));
    }

    public ConnectionViewModel Connection { get; }

    public string Title
    {
        get
        {
            var s = _services.Session;
            if (!s.IsConnected)
                return "Mission Planner";
            var cs = s.Cs;
            return $"Mission Planner — {cs.firmware} V{cs.version?.ToString(3)}";
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsData), nameof(IsPlan), nameof(IsSetup), nameof(IsConfig), nameof(IsSimulation), nameof(IsHelp))]
    private Screen _currentScreen;

    [ObservableProperty]
    private PageViewModel _currentPage;

    public bool IsData => CurrentScreen == Screen.Data;
    public bool IsPlan => CurrentScreen == Screen.Plan;
    public bool IsSetup => CurrentScreen == Screen.Setup;
    public bool IsConfig => CurrentScreen == Screen.Config;
    public bool IsSimulation => CurrentScreen == Screen.Simulation;
    public bool IsHelp => CurrentScreen == Screen.Help;

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
        if (++_tick % 20 == 0)
        {
            Connection.RefreshVehicles();
            OnPropertyChanged(nameof(Title));
        }
    }
}

public sealed class HelpViewModel() : PageViewModel("Help")
{
    public string Version { get; } = typeof(HelpViewModel).Assembly.GetName().Version?.ToString() ?? "";

    public string DataDirectory { get; } = MissionPlanner.Utilities.Settings.GetUserDataDirectory();

    public string LogDirectory { get; } = MissionPlanner.Utilities.Settings.Instance.LogDir;
}
