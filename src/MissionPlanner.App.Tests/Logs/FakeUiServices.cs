using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.ViewModels;

namespace MissionPlanner.App.Tests.Logs;

/// <summary>UI services for view-model tests: work runs inline, dialogs answer from a script.</summary>
internal sealed class FakeUiServices : IUiDispatcher, IDialogService, IWindowService
{
    public bool ConfirmAnswer { get; set; } = true;

    public List<string> Messages { get; } = new();

    public List<string> Confirms { get; } = new();

    public List<PageViewModel> Shown { get; } = new();

    public string? OpenFileAnswer { get; set; }

    public AppServices CreateServices() => new(this, this, this);

    public bool CheckAccess() => true;

    // one "UI thread" at a time, as in the app: posts from parallel loads must not interleave
    private readonly object _ui = new();

    public void Post(Action action)
    {
        lock (_ui)
            action();
    }

    public Task InvokeAsync(Action action)
    {
        lock (_ui)
            action();
        return Task.CompletedTask;
    }

    public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();

    public Task ShowMessageAsync(string title, string message, MessageKind kind = MessageKind.Info)
    {
        lock (Messages)
            Messages.Add(message);
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK")
    {
        lock (Confirms)
            Confirms.Add(message);
        return Task.FromResult(ConfirmAnswer);
    }

    public Task<string?> PromptAsync(string title, string message, string initialValue = "") => Task.FromResult<string?>(null);

    public Task<string?> OpenFileAsync(string title, params FileFilter[] filters) => Task.FromResult(OpenFileAnswer);

    public Task<string?> SaveFileAsync(string title, string suggestedName, params FileFilter[] filters) => Task.FromResult<string?>(null);

    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

    public void Show(PageViewModel page, double width = 1100, double height = 750)
    {
        lock (Shown)
            Shown.Add(page);
    }

    public Task<bool> ShowDialogAsync(PageViewModel page, double width = 1000, double height = 700)
    {
        Show(page, width, height);
        return Task.FromResult(false);
    }
}
