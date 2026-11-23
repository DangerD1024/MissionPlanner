using Avalonia.Controls;
using Avalonia.Input.Platform;
using MissionPlanner.App.Core.Localization;
using MissionPlanner.App.Core.ViewModels.Video;

namespace MissionPlanner.App.Controls.Video;

/// <summary>
/// The HUD's right-click menu, Mission Planner style: a Video submenu with start/stop, the saved
/// sources, the settings dialog, a raw GStreamer pipeline prompt, aspect and auto-start. Rebuilt
/// each time it opens so it always shows the current state.
/// </summary>
public static class HudVideoMenu
{
    /// <summary>
    /// The menu; <paramref name="extra"/> appends the host's own entries (detach / attach, fullscreen)
    /// after the Video submenu each time it opens.
    /// </summary>
    public static ContextMenu Create(Func<HudVideoViewModel?> video, Action<ItemCollection>? extra = null)
    {
        var menu = new ContextMenu();
        menu.Opening += (_, e) =>
        {
            var vm = video();
            if (vm == null)
            {
                e.Cancel = true;
                return;
            }

            menu.Items.Clear();
            menu.Items.Add(BuildVideoMenu(vm));
            menu.Items.Add(Build3DMenu(vm));
            menu.Items.Add(BuildStreamingMenu(StreamingViewModel.For(vm.Services)));
            extra?.Invoke(menu.Items);
        };
        return menu;
    }

    private static MenuItem BuildVideoMenu(HudVideoViewModel vm)
    {
        var root = new MenuItem { Header = L.Tr("Video") };
        var selected = vm.Selected;

        var start = Item(selected == null ? "Start video" : $"Start video ({selected.DisplayName})", () => vm.Start());
        start.IsEnabled = selected != null && !vm.IsActive;
        root.Items.Add(start);
        var stop = Item("Stop video", vm.Stop);
        stop.IsEnabled = vm.IsActive;
        root.Items.Add(stop);
        root.Items.Add(new Separator());

        // saved sources: picking one selects and starts it
        foreach (var source in vm.Sources)
        {
            var id = source.Id;
            var item = Item(source.DisplayName, () => vm.Select(id));
            item.ToggleType = MenuItemToggleType.Radio;
            item.GroupName = "video-source";
            item.IsChecked = source.Id == selected?.Id;
            ToolTip.SetTip(item, source.Summary);
            root.Items.Add(item);
        }

        if (vm.Sources.Count > 0)
            root.Items.Add(new Separator());

        root.Items.Add(Item("Video settings…", () => _ = vm.OpenSettingsAsync()));
        root.Items.Add(Item("Set GStreamer source…", () => _ = vm.SetGStreamerSourceAsync()));
        root.Items.Add(new Separator());

        var fill = Item("Fill the HUD (crop edges)", () => vm.Aspect = VideoAspect.Fill);
        fill.ToggleType = MenuItemToggleType.Radio;
        fill.GroupName = "video-aspect";
        fill.IsChecked = vm.Aspect == VideoAspect.Fill;
        var fit = Item("Fit the whole frame", () => vm.Aspect = VideoAspect.Fit);
        fit.ToggleType = MenuItemToggleType.Radio;
        fit.GroupName = "video-aspect";
        fit.IsChecked = vm.Aspect == VideoAspect.Fit;
        root.Items.Add(fill);
        root.Items.Add(fit);
        root.Items.Add(new Separator());

        var auto = Item("Start video when the app starts", () => vm.AutoStart = !vm.AutoStart);
        auto.ToggleType = MenuItemToggleType.CheckBox;
        auto.IsChecked = vm.AutoStart;
        root.Items.Add(auto);
        return root;
    }

    /// <summary>3D view behind the HUD: start/stop, the automatic fallback, the renderer or why it cannot run.</summary>
    private static MenuItem Build3DMenu(HudVideoViewModel vm)
    {
        var root = new MenuItem { Header = L.Tr("3D view") };
        if (vm.ThreeD)
            root.Items.Add(Item("Stop 3D", vm.Stop3D));
        else
            root.Items.Add(Item("Start 3D", vm.Start3D));

        var fallback = Item("Show 3D when there is no video", () => vm.ThreeDFallback = !vm.ThreeDFallback);
        fallback.ToggleType = MenuItemToggleType.CheckBox;
        fallback.IsChecked = vm.ThreeDFallback;
        root.Items.Add(fallback);
        root.Items.Add(Item("3D view settings…", () => _ = vm.OpenCameraSettingsAsync()));

        var scene = MissionPlanner.App.Core.Synthetic.SyntheticScene.For(vm.Services);
        if ((scene.Error ?? scene.Renderer) is { } state)
        {
            root.Items.Add(new Separator());
            root.Items.Add(new MenuItem { Header = state, IsEnabled = false });
            if (scene.Error == null && scene.IsActive)
                root.Items.Add(new MenuItem
                {
                    Header = L.Format("{0:0} fps, attitude {1:0} Hz", scene.RenderFps, scene.AttitudeRate),
                    IsEnabled = false,
                });
        }

        return root;
    }

    /// <summary>Streaming: start/stop, the OSD in the stream, the address to give other apps, settings.</summary>
    private static MenuItem BuildStreamingMenu(StreamingViewModel vm)
    {
        var root = new MenuItem { Header = L.Tr("Streaming") };
        if (vm.IsStreaming)
            root.Items.Add(Item("Stop streaming", vm.Stop));
        else
            root.Items.Add(Item("Start streaming", () => vm.Start()));

        var osd = Item("Show OSD in stream", () => vm.ShowOsd = !vm.ShowOsd);
        osd.ToggleType = MenuItemToggleType.CheckBox;
        osd.IsChecked = vm.ShowOsd;
        root.Items.Add(osd);

        if (vm.Urls.Count > 0)
        {
            var copy = new MenuItem { Header = L.Tr("Copy stream address") };
            foreach (var url in vm.Urls)
                copy.Items.Add(new MenuItem { Header = url, Command = new CopyCommand(url) });
            root.Items.Add(copy);
        }

        root.Items.Add(new Separator());
        root.Items.Add(vm.IsRecording
            ? Item("Stop recording", vm.StopRecording)
            : Item("Record HUD to file", () => vm.StartRecording()));
        root.Items.Add(Item("Streaming settings…", () => _ = vm.OpenSettingsAsync()));
        if (!string.IsNullOrEmpty(vm.StatusText))
            root.Items.Add(new MenuItem { Header = vm.StatusText, IsEnabled = false });
        return root;
    }

    /// <summary>Puts text on the clipboard (through the main window).</summary>
    private sealed class CopyCommand(string text) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime
                { MainWindow.Clipboard: { } clipboard })
                _ = clipboard.SetTextAsync(text.Split(' ')[0]);
        }
    }

    /// <summary>A menu entry with its English text translated (the menu is rebuilt each time it opens).</summary>
    internal static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = L.Tr(header) };
        item.Click += (_, _) => action();
        return item;
    }
}
