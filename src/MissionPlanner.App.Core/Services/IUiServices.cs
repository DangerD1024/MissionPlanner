namespace MissionPlanner.App.Core.Services;

/// <summary>Runs work on the UI thread. Implemented by the UI layer.</summary>
public interface IUiDispatcher
{
    bool CheckAccess();

    void Post(Action action);

    Task InvokeAsync(Action action);

    Task<T> InvokeAsync<T>(Func<Task<T>> action);
}

public enum MessageKind
{
    Info,
    Warning,
    Error,
}

public sealed record FileFilter(string Name, params string[] Extensions);

/// <summary>Dialogs and pickers. Implemented by the UI layer; every call is made on the UI thread.</summary>
public interface IDialogService
{
    Task ShowMessageAsync(string title, string message, MessageKind kind = MessageKind.Info);

    Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK");

    /// <summary>Returns null when cancelled.</summary>
    Task<string?> PromptAsync(string title, string message, string initialValue = "");

    Task<string?> OpenFileAsync(string title, params FileFilter[] filters);

    Task<string?> SaveFileAsync(string title, string suggestedName, params FileFilter[] filters);

    Task<string?> PickFolderAsync(string title);
}

/// <summary>A page shown in a dialog window decides when it closes and with what result.</summary>
public interface IDialogPage
{
    /// <summary>Raised by the page to close its window; the argument is the dialog result.</summary>
    event EventHandler<bool>? CloseRequested;
}

/// <summary>Opens pages in their own windows. Implemented by the UI layer; call on the UI thread.</summary>
public interface IWindowService
{
    /// <summary>Shows a page in a separate, non-modal window (log browser, inspector...).</summary>
    void Show(ViewModels.PageViewModel page, double width = 1100, double height = 750);

    /// <summary>Shows a page modally; returns true if it closed with a positive result (see <see cref="IDialogPage"/>).</summary>
    Task<bool> ShowDialogAsync(ViewModels.PageViewModel page, double width = 1000, double height = 700);
}

/// <summary>Everything a view-model may need from outside, created once by the UI layer.</summary>
public sealed class AppServices
{
    public AppServices(IUiDispatcher dispatcher, IDialogService dialogs, IWindowService windows)
    {
        Dispatcher = dispatcher;
        Dialogs = dialogs;
        Windows = windows;
        Session = new Session.VehicleSession();
        Params = new Params.ParamService(this);
        Mission = new Mission.MissionStore();
    }

    public IUiDispatcher Dispatcher { get; }

    public IDialogService Dialogs { get; }

    public IWindowService Windows { get; }

    /// <summary>The vehicle link (replaces MainV2.comPort).</summary>
    public Session.VehicleSession Session { get; }

    /// <summary>Parameters and their documentation for the selected vehicle.</summary>
    public Params.ParamService Params { get; }

    /// <summary>Planned mission, fence and rally points, shared by Flight Data and Flight Plan.</summary>
    public Mission.MissionStore Mission { get; }

    private Mission.PoiStore? _pois;

    /// <summary>Points of interest of the DATA and PLAN maps (poi.txt, shared with Mission Planner).</summary>
    public Mission.PoiStore Pois => _pois ??= new Mission.PoiStore();

    /// <summary>Replaces the POI store (tests).</summary>
    internal void UsePois(Mission.PoiStore store) => _pois = store;

    /// <summary>The ground station's own GPS position (TOOLS > Moving Base), drawn on the DATA and PLAN maps.</summary>
    public GcsLocationService GcsLocation { get; } = new();

    private Outputs.OutputManager? _outputs;

    /// <summary>The MAVLink mirror and position outputs (TOOLS > Outputs); created on first use.</summary>
    public Outputs.OutputManager Outputs => _outputs ??= new Outputs.OutputManager(this);

    private Updates.UpdateViewModel? _updates;

    /// <summary>The update check (startup banner, SETTINGS > About); created on first use.</summary>
    public Updates.UpdateViewModel Updates => _updates ??= new Updates.UpdateViewModel(this);

    /// <summary>The outputs, when they have been created (null before first use).</summary>
    public Outputs.OutputManager? OutputsIfCreated => _outputs;

    /// <summary>The UI language: <c>Localizer.Tr("English text")</c>; see also <c>L.Tr</c>.</summary>
    public Localization.Localizer Localizer { get; } = Localization.Localizer.Current;

    /// <summary>Colour themes; the UI layer replaces the default.</summary>
    public IThemeService Themes { get; set; } = NullThemeService.Instance;

    /// <summary>Raised when a page asks to switch the main screen; the shell handles it.</summary>
    public event EventHandler<ViewModels.Screen>? ScreenRequested;

    /// <summary>Switches the main window to a screen (call on the UI thread).</summary>
    public void ShowScreen(ViewModels.Screen screen) => ScreenRequested?.Invoke(this, screen);

    /// <summary>Raised to switch the DATA map between 2D, 3D and split (TOOLS > 3D Map).</summary>
    public event EventHandler<ViewModels.FlightData.MapViewMode>? MapViewRequested;

    /// <summary>Shows the DATA map as 2D, 3D or split; the setting applies when DATA is first shown too.</summary>
    public void RequestMapView(ViewModels.FlightData.MapViewMode mode)
    {
        MissionPlanner.Utilities.Settings.Instance[ViewModels.FlightData.FlightDataViewModel.MapViewKey] = mode.ToString();
        MapViewRequested?.Invoke(this, mode);
    }

    /// <summary>Raised to open a page of a menu screen (SETUP > Joystick) by its title.</summary>
    public event EventHandler<(ViewModels.Screen Screen, string Title)>? MenuPageRequested;

    /// <summary>Shows the screen and opens the page with this menu title on it.</summary>
    public void RequestMenuPage(ViewModels.Screen screen, string title) => MenuPageRequested?.Invoke(this, (screen, title));

    /// <summary>
    /// Raised (on the caller's thread) when a telemetry stream rate was set through
    /// <see cref="ViewModels.ConfigScreens.PlannerSettings.SetRate"/>, so every place that shows it follows.
    /// </summary>
    public event EventHandler<ViewModels.ConfigScreens.PlannerSettings.StreamGroup>? StreamRateChanged;

    internal void OnStreamRateChanged(ViewModels.ConfigScreens.PlannerSettings.StreamGroup group) =>
        StreamRateChanged?.Invoke(this, group);

    /// <summary>
    /// Raised (on the caller's thread, the UI thread) when the speech settings changed: the switch on SETTINGS,
    /// the one on DATA > Actions, or the Speech dialog. Every place that shows them follows.
    /// </summary>
    public event EventHandler? SpeechSettingsChanged;

    internal void OnSpeechSettingsChanged() => SpeechSettingsChanged?.Invoke(this, EventArgs.Empty);

    private ViewModels.FlightData.MapSettingsViewModel? _mapSettings;

    /// <summary>The map settings page (gear button on the DATA and PLAN maps, SETTINGS > Map); one for the app.</summary>
    public ViewModels.FlightData.MapSettingsViewModel MapSettings => _mapSettings ??= new ViewModels.FlightData.MapSettingsViewModel(this);

    /// <summary>Raised on the UI thread when a map setting changed (<see cref="Maps.MapOptions"/>); the maps apply it at once.</summary>
    public event EventHandler? MapSettingsChanged;

    internal void OnMapSettingsChanged() => MapSettingsChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Raised by <see cref="Notify"/>; the shell shows it as a toast.</summary>
    public event EventHandler<Notification>? Notified;

    /// <summary>
    /// A short message that goes away by itself (bottom right of the window), for outcomes that need
    /// no answer: "12 parameters saved". Errors that need attention stay dialogs. Any thread.
    /// </summary>
    public void Notify(string text, MessageKind kind = MessageKind.Info) => Notified?.Invoke(this, new Notification(text, kind));
}

/// <summary>A toast: English text (translated when shown) and how it is coloured.</summary>
public sealed record Notification(string Text, MessageKind Kind);
