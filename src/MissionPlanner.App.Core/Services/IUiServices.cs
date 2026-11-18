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

    /// <summary>Raised when a page asks to switch the main screen; the shell handles it.</summary>
    public event EventHandler<ViewModels.Screen>? ScreenRequested;

    /// <summary>Switches the main window to a screen (call on the UI thread).</summary>
    public void ShowScreen(ViewModels.Screen screen) => ScreenRequested?.Invoke(this, screen);
}
