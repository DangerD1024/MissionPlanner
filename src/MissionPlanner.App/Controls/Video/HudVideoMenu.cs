using Avalonia.Controls;
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

    /// <summary>A menu entry with its English text translated (the menu is rebuilt each time it opens).</summary>
    internal static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = L.Tr(header) };
        item.Click += (_, _) => action();
        return item;
    }
}
