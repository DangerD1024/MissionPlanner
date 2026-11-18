using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Services;

public sealed class AvaloniaDispatcher : IUiDispatcher
{
    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    public Task InvokeAsync(Action action) => Dispatcher.UIThread.InvokeAsync(action).GetTask();

    public Task<T> InvokeAsync<T>(Func<Task<T>> action) => Dispatcher.UIThread.InvokeAsync(action);
}

/// <summary>Small modal dialogs styled like the rest of the app, plus the platform file pickers.</summary>
public sealed class DialogService : IDialogService
{
    private static Window? Owner =>
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public async Task ShowMessageAsync(string title, string message, MessageKind kind = MessageKind.Info)
    {
        await ShowAsync(title, message, kind, input: null, ok: "OK", cancel: null);
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK")
    {
        var (ok, _) = await ShowAsync(title, message, MessageKind.Info, input: null, ok: confirmText, cancel: "Cancel");
        return ok;
    }

    public async Task<string?> PromptAsync(string title, string message, string initialValue = "")
    {
        var (ok, text) = await ShowAsync(title, message, MessageKind.Info, input: initialValue, ok: "OK", cancel: "Cancel");
        return ok ? text : null;
    }

    public async Task<string?> OpenFileAsync(string title, params FileFilter[] filters)
    {
        var storage = Owner?.StorageProvider;
        if (storage == null)
            return null;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = ToFilters(filters),
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> SaveFileAsync(string title, string suggestedName, params FileFilter[] filters)
    {
        var storage = Owner?.StorageProvider;
        if (storage == null)
            return null;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = ToFilters(filters),
            DefaultExtension = filters.FirstOrDefault()?.Extensions.FirstOrDefault(),
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var storage = Owner?.StorageProvider;
        if (storage == null)
            return null;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    private static List<FilePickerFileType>? ToFilters(FileFilter[] filters) =>
        filters.Length == 0
            ? null
            : filters.Select(f => new FilePickerFileType(f.Name) { Patterns = f.Extensions.Select(e => "*." + e).ToList() }).ToList();

    private static async Task<(bool Ok, string? Text)> ShowAsync(string title, string message, MessageKind kind, string? input,
        string ok, string? cancel)
    {
        var accent = kind switch
        {
            MessageKind.Error => (IBrush)Avalonia.Application.Current!.FindResource("Danger")!,
            MessageKind.Warning => (IBrush)Avalonia.Application.Current!.FindResource("Warn")!,
            _ => (IBrush)Avalonia.Application.Current!.FindResource("Accent")!,
        };

        TextBox? box = input == null ? null : new TextBox { Text = input, MinWidth = 320 };
        var okButton = new Button { Content = ok, IsDefault = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        okButton.Classes.Add("accent");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        Button? cancelButton = null;
        if (cancel != null)
        {
            cancelButton = new Button { Content = cancel, IsCancel = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
            buttons.Children.Add(cancelButton);
        }

        buttons.Children.Add(okButton);

        var body = new StackPanel { Spacing = 14, Margin = new Avalonia.Thickness(20) };
        body.Children.Add(new Border { Height = 3, CornerRadius = new Avalonia.CornerRadius(2), Background = accent, Width = 40, HorizontalAlignment = HorizontalAlignment.Left });
        body.Children.Add(new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 });
        if (box != null)
            body.Children.Add(box);
        body.Children.Add(buttons);

        var window = new Window
        {
            Title = title,
            Content = body,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            MinWidth = 340,
        };
        window.Background = (IBrush)Avalonia.Application.Current!.FindResource("Bg2")!;

        var result = false;
        okButton.Click += (_, _) =>
        {
            result = true;
            window.Close();
        };
        if (cancelButton != null)
            cancelButton.Click += (_, _) => window.Close();
        window.Opened += (_, _) => (box as Control ?? okButton).Focus();

        var owner = Owner;
        if (owner is { IsVisible: true })
            await window.ShowDialog(owner);
        else
        {
            var closed = new TaskCompletionSource();
            window.Closed += (_, _) => closed.TrySetResult();
            window.Show();
            await closed.Task;
        }

        return (result, box?.Text);
    }
}
