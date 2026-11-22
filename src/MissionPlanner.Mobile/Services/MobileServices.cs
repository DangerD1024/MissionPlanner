using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.ViewModels;

namespace MissionPlanner.Mobile.Services;

public sealed class MobileDispatcher : IUiDispatcher
{
    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    public Task InvokeAsync(Action action) => Dispatcher.UIThread.InvokeAsync(action).GetTask();

    public Task<T> InvokeAsync<T>(Func<Task<T>> action) => Dispatcher.UIThread.InvokeAsync(action);
}

public enum SheetKind
{
    Confirm,
    Prompt,
    Message,
}

/// <summary>A bottom sheet: "slide to confirm", a question with a text box, or a message.</summary>
public sealed partial class Sheet : ObservableObject
{
    private readonly TaskCompletionSource<string?> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Sheet(SheetKind kind, string title, string message, string action = "OK", string initial = "", MessageKind level = MessageKind.Info)
    {
        Kind = kind;
        Title = title;
        Message = message;
        Action = action;
        _text = initial;
        Level = level;
    }

    public SheetKind Kind { get; }

    public string Title { get; }

    public string Message { get; }

    public string Action { get; }

    public MessageKind Level { get; }

    public bool IsConfirm => Kind == SheetKind.Confirm;

    public bool IsPrompt => Kind == SheetKind.Prompt;

    public bool IsMessage => Kind == SheetKind.Message;

    /// <summary>Red slider for actions that can hurt (disarm in flight, force arm, reboot).</summary>
    public bool IsDanger => Action.Contains("Force", StringComparison.OrdinalIgnoreCase) ||
                            Action.Contains("Disarm", StringComparison.OrdinalIgnoreCase) ||
                            Action.Contains("Reboot", StringComparison.OrdinalIgnoreCase) || Level == MessageKind.Error;

    public string SlideText => "Slide to " + Action.ToLowerInvariant();

    /// <summary>A number keyboard suits the question (its suggested answer is a number).</summary>
    public bool IsNumeric => double.TryParse(_text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);

    [ObservableProperty]
    private string _text;

    public Task<string?> Result => _done.Task;

    [RelayCommand]
    public void Accept() => _done.TrySetResult(IsPrompt ? Text : "ok");

    [RelayCommand]
    public void Cancel() => _done.TrySetResult(null);
}

/// <summary>Shows one sheet at a time over the app; others wait their turn.</summary>
public sealed partial class SheetHost : ObservableObject
{
    private readonly SemaphoreSlim _one = new(1, 1);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen))]
    private Sheet? _current;

    public bool IsOpen => Current != null;

    public async Task<string?> ShowAsync(Sheet sheet)
    {
        await _one.WaitAsync();
        try
        {
            Current = sheet;
            return await sheet.Result;
        }
        finally
        {
            Current = null;
            _one.Release();
        }
    }

    /// <summary>Back button / tap outside: the open sheet is cancelled.</summary>
    public bool CancelOpen()
    {
        if (Current == null)
            return false;
        Current.Cancel();
        return true;
    }
}

/// <summary>
/// The phone's dialogs: every confirmation is a slide-to-confirm sheet, prompts and messages are
/// sheets too. Files open through the system picker and are copied into the app's cache folder.
/// </summary>
public sealed class MobileDialogService(SheetHost sheets, Func<TopLevel?> topLevel) : IDialogService
{
    public Task ShowMessageAsync(string title, string message, MessageKind kind = MessageKind.Info) =>
        sheets.ShowAsync(new Sheet(SheetKind.Message, title, message, level: kind));

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK") =>
        await sheets.ShowAsync(new Sheet(SheetKind.Confirm, title, message, confirmText)) != null;

    public Task<string?> PromptAsync(string title, string message, string initialValue = "") =>
        sheets.ShowAsync(new Sheet(SheetKind.Prompt, title, message, "OK", initialValue));

    public async Task<string?> OpenFileAsync(string title, params FileFilter[] filters)
    {
        if (topLevel()?.StorageProvider is not { CanOpen: true } storage)
            return null;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = filters.Where(f => !f.Extensions.Contains("*"))
                .Select(f => new FilePickerFileType(f.Name) { Patterns = f.Extensions.Select(e => "*." + e).ToList() }).ToList(),
        });
        if (files.Count == 0)
            return null;
        var file = files[0];
        if (file.TryGetLocalPath() is { } local && File.Exists(local))
            return local;

        // a content:// document: copy it where the libraries can read it by path
        var dir = Path.Combine(MissionPlanner.Utilities.Settings.GetUserDataDirectory(), "imported");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, Path.GetFileName(file.Name));
        await using (var src = await file.OpenReadAsync())
        await using (var dst = File.Create(target))
            await src.CopyToAsync(dst);
        return target;
    }

    /// <summary>Saves into the app's own folder (shared storage needs the system's document picker).</summary>
    public Task<string?> SaveFileAsync(string title, string suggestedName, params FileFilter[] filters)
    {
        var dir = Path.Combine(MissionPlanner.Utilities.Settings.GetUserDataDirectory(), "exports");
        Directory.CreateDirectory(dir);
        return Task.FromResult<string?>(Path.Combine(dir, suggestedName));
    }

    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(MissionPlanner.Utilities.Settings.GetUserDataDirectory());
}

/// <summary>Desktop windows (log browser, inspector...) are not part of the phone app yet.</summary>
public sealed class MobileWindowService(IDialogService dialogs) : IWindowService
{
    public void Show(PageViewModel page, double width = 1100, double height = 750) =>
        _ = dialogs.ShowMessageAsync(page.Title, "This page is not available in the phone app yet.");

    public async Task<bool> ShowDialogAsync(PageViewModel page, double width = 1000, double height = 700)
    {
        await dialogs.ShowMessageAsync(page.Title, "This page is not available in the phone app yet.");
        return false;
    }
}
