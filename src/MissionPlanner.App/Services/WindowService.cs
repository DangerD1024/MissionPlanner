using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.ViewModels;

namespace MissionPlanner.App.Services;

/// <summary>Hosts pages in their own windows; open pages are ticked with the main screen.</summary>
public sealed class WindowService : IWindowService
{
    private readonly List<PageViewModel> _open = new();

    private static Window? Owner =>
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public void Show(PageViewModel page, double width = 1100, double height = 750)
    {
        var window = Create(page, width, height);
        window.Show();
    }

    public async Task<bool> ShowDialogAsync(PageViewModel page, double width = 1000, double height = 700)
    {
        var window = Create(page, width, height);
        var result = false;
        if (page is IDialogPage dialog)
        {
            EventHandler<bool> handler = (_, r) =>
            {
                result = r;
                window.Close();
            };
            dialog.CloseRequested += handler;
            window.Closed += (_, _) => dialog.CloseRequested -= handler;
        }

        if (Owner is { IsVisible: true } owner)
        {
            await window.ShowDialog(owner);
        }
        else
        {
            var closed = new TaskCompletionSource();
            window.Closed += (_, _) => closed.TrySetResult();
            window.Show();
            await closed.Task;
        }

        return result;
    }

    /// <summary>Called by the app's UI tick.</summary>
    public void Tick()
    {
        foreach (var page in _open.ToArray())
            page.Tick();
    }

    private Window Create(PageViewModel page, double width, double height)
    {
        var window = new Window
        {
            Width = width,
            Height = height,
            Content = page,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (IBrush)Avalonia.Application.Current!.FindResource("Bg2")!,
        };
        Localization.TrBinding.Bind(window, Window.TitleProperty, page.Title);
        window.Opened += (_, _) =>
        {
            _open.Add(page);
            page.OnShown();
        };
        window.Closed += (_, _) =>
        {
            _open.Remove(page);
            page.OnHidden();
            (page as IDisposable)?.Dispose();
        };
        return window;
    }
}
