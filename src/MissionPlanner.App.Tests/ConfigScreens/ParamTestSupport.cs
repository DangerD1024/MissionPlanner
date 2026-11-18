using MissionPlanner.App.Core.Params;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.ViewModels;

namespace MissionPlanner.App.Tests.ConfigScreens;

/// <summary>Runs everything inline: the tests play the UI thread.</summary>
internal sealed class InlineDispatcher : IUiDispatcher
{
    public bool CheckAccess() => true;

    public void Post(Action action) => action();

    public Task InvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();
}

/// <summary>Records messages and answers confirmations and pickers as scripted.</summary>
internal sealed class FakeDialogs : IDialogService
{
    public List<(string Title, string Message, MessageKind Kind)> Messages { get; } = new();

    public List<(string Title, string Message)> Confirms { get; } = new();

    public bool ConfirmAnswer { get; set; } = true;

    public string? OpenPath { get; set; }

    public string? SavePath { get; set; }

    public Task ShowMessageAsync(string title, string message, MessageKind kind = MessageKind.Info)
    {
        Messages.Add((title, message, kind));
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK")
    {
        Confirms.Add((title, message));
        return Task.FromResult(ConfirmAnswer);
    }

    public Task<string?> PromptAsync(string title, string message, string initialValue = "") => Task.FromResult<string?>(null);

    public Task<string?> OpenFileAsync(string title, params FileFilter[] filters) => Task.FromResult(OpenPath);

    public Task<string?> SaveFileAsync(string title, string suggestedName, params FileFilter[] filters) => Task.FromResult(SavePath);

    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
}

/// <summary>Hands a modal page to the test, which decides the dialog result.</summary>
internal sealed class FakeWindows : IWindowService
{
    public Func<PageViewModel, bool>? OnDialog { get; set; }

    public List<PageViewModel> Shown { get; } = new();

    public void Show(PageViewModel page, double width = 1100, double height = 750) => Shown.Add(page);

    public Task<bool> ShowDialogAsync(PageViewModel page, double width = 1000, double height = 700)
    {
        Shown.Add(page);
        return Task.FromResult(OnDialog?.Invoke(page) ?? false);
    }
}

internal static class ParamTestSupport
{
    public static (AppServices Services, FakeDialogs Dialogs, FakeWindows Windows) CreateServices()
    {
        var dialogs = new FakeDialogs();
        var windows = new FakeWindows();
        return (new AppServices(new InlineDispatcher(), dialogs, windows), dialogs, windows);
    }

    /// <summary>Puts parameters into the selected vehicle's list, as a download would.</summary>
    public static void AddParams(AppServices services, params (string Name, double Value, double? Default)[] values)
    {
        foreach (var (name, value, def) in values)
            services.Session.Vehicle.param.Add(new MAVLink.MAVLinkParam(name, value, MAVLink.MAV_PARAM_TYPE.REAL32, def));
    }

    public static ParamMeta Meta(string name, string display = "", string description = "", string units = "",
        double? min = null, double? max = null, double? increment = null, string user = "Standard",
        bool readOnly = false, bool reboot = false,
        IReadOnlyList<KeyValuePair<double, string>>? values = null, IReadOnlyList<KeyValuePair<int, string>>? bits = null) =>
        new(name, display, description, units, min, max, increment, values ?? [], bits ?? [], user, reboot, readOnly);

    public static string TempFile(string contents)
    {
        var path = Path.Combine(TestEnvironment.DataRoot, "p-" + Guid.NewGuid().ToString("N") + ".param");
        File.WriteAllText(path, contents);
        return path;
    }
}
