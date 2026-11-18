using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.Video;
using MissionPlanner.App.Core.ViewModels;
using MissionPlanner.App.Core.ViewModels.Video;

namespace MissionPlanner.App.Tests.Video;

/// <summary>Settings kept in a dictionary; counts disk writes.</summary>
internal sealed class MemoryVideoStore : IVideoSettingsStore
{
    public Dictionary<string, string> Values { get; } = new();

    public int Saves { get; private set; }

    public string? Get(string key) => Values.GetValueOrDefault(key);

    public void Set(string key, string? value)
    {
        if (value == null)
            Values.Remove(key);
        else
            Values[key] = value;
    }

    public void Save() => Saves++;
}

/// <summary>A stream that records what it was asked to run; tests drive its state.</summary>
internal sealed class FakeVideoStream : IVideoStream
{
    public List<string> Started { get; } = new();

    public int Stops { get; private set; }

    public VideoStreamState State { get; private set; }

    public string? Error { get; private set; }

    public string? Pipeline => Started.LastOrDefault();

    public VideoFrameBuffer Frames { get; } = new();

    public event EventHandler? StateChanged;

    public event EventHandler? FrameArrived;

    public void Start(string pipeline)
    {
        Started.Add(pipeline);
        Set(VideoStreamState.Connecting);
    }

    public void Stop()
    {
        Stops++;
        Set(VideoStreamState.Stopped);
    }

    public void Set(VideoStreamState state, string? error = null)
    {
        State = state;
        Error = error;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RaiseFrame() => FrameArrived?.Invoke(this, EventArgs.Empty);

    public void Dispose() => Stop();
}

/// <summary>UI services for video view-model tests: inline dispatch, scripted prompt, recorded dialogs.</summary>
internal sealed class VideoTestUi : IUiDispatcher, IDialogService, IWindowService
{
    public string? PromptAnswer { get; set; }

    public string? LastPromptInitial { get; private set; }

    public List<PageViewModel> Dialogs { get; } = new();

    public AppServices CreateServices() => new(this, this, this);

    public bool CheckAccess() => true;

    public void Post(Action action) => action();

    public Task InvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();

    public Task ShowMessageAsync(string title, string message, MessageKind kind = MessageKind.Info) => Task.CompletedTask;

    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK") => Task.FromResult(true);

    public Task<string?> PromptAsync(string title, string message, string initialValue = "")
    {
        LastPromptInitial = initialValue;
        return Task.FromResult(PromptAnswer);
    }

    public Task<string?> OpenFileAsync(string title, params FileFilter[] filters) => Task.FromResult<string?>(null);

    public Task<string?> SaveFileAsync(string title, string suggestedName, params FileFilter[] filters) => Task.FromResult<string?>(null);

    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

    public void Show(PageViewModel page, double width = 1100, double height = 750) => Dialogs.Add(page);

    public Task<bool> ShowDialogAsync(PageViewModel page, double width = 1000, double height = 700)
    {
        Dialogs.Add(page);
        return Task.FromResult(false);
    }
}
