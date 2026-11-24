using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels;

/// <summary>An entry in a left-hand menu: either a section header or a page created on first use.</summary>
public sealed partial class MenuItemViewModel : ObservableObject
{
    private readonly Func<PageViewModel>? _factory;
    private PageViewModel? _page;

    private MenuItemViewModel(string title, Func<PageViewModel>? factory, Func<bool>? isAvailable)
    {
        Title = title;
        _factory = factory;
        IsAvailable = isAvailable ?? (() => true);
    }

    public static MenuItemViewModel Header(string title) => new(title, null, null);

    public static MenuItemViewModel Page(string title, Func<PageViewModel> factory, Func<bool>? isAvailable = null) =>
        new(title, factory, isAvailable);

    public string Title { get; }

    public bool IsHeader => _factory == null;

    public bool IsPage => _factory != null;

    /// <summary>Whether the page applies to the connected vehicle (e.g. frame type only on multirotors).</summary>
    public Func<bool> IsAvailable { get; }

    [ObservableProperty]
    private bool _isVisible = true;

    public PageViewModel GetPage() => _page ??= _factory!();
}

/// <summary>A screen with a left menu, like Mission Planner's Setup and Config screens.</summary>
public abstract partial class MenuScreenViewModel : PageViewModel
{
    protected MenuScreenViewModel(string title, AppServices services)
        : base(title)
    {
        Services = services;
        services.Session.StateChanged += (_, _) => services.Dispatcher.Post(UpdateVisibility);
        services.Params.ParamsChanged += (_, _) => UpdateVisibility();
    }

    protected bool Connected => Services.Session.IsConnected;

    protected bool HasParam(params string[] names) => names.Any(Services.Params.Has);

    protected MissionPlanner.ArduPilot.Firmwares Firmware => Services.Session.Cs.firmware;

    protected AppServices Services { get; }

    public ObservableCollection<MenuItemViewModel> Items { get; } = new();

    public IEnumerable<MenuItemViewModel> VisibleItems => Items.Where(i => i.IsVisible);

    [ObservableProperty]
    private MenuItemViewModel? _selectedItem;

    [ObservableProperty]
    private PageViewModel? _content;

    partial void OnSelectedItemChanged(MenuItemViewModel? oldValue, MenuItemViewModel? newValue)
    {
        if (newValue is { IsHeader: true })
        {
            // headers are labels; keep the previous page
            SelectedItem = oldValue;
            return;
        }

        Content = newValue?.GetPage();
        if (_visible)
            Activate(Content);
    }

    private bool _visible;
    private PageViewModel? _active;

    /// <summary>Shows/hides pages exactly once per change, however the selection arrived.</summary>
    private void Activate(PageViewModel? page)
    {
        if (ReferenceEquals(page, _active))
            return;
        _active?.OnHidden();
        _active = page;
        _active?.OnShown();
    }

    protected void Add(MenuItemViewModel item) => Items.Add(item);

    /// <summary>Whether the first page opens by itself when nothing is selected (not on a screen of buttons).</summary>
    protected virtual bool AutoSelectFirst => true;

    /// <summary>Opens a page by its menu title, when it applies to the vehicle; returns whether it did.</summary>
    public virtual bool Show(string title)
    {
        UpdateVisibility();
        if (Items.FirstOrDefault(i => i.IsPage && i.IsVisible && i.Title == title) is not { } item)
            return false;
        SelectedItem = item;
        return true;
    }

    protected void UpdateVisibility()
    {
        foreach (var item in Items.Where(i => i.IsPage))
            item.IsVisible = item.IsAvailable();

        // a header shows only while one of its pages does
        for (var i = 0; i < Items.Count; i++)
        {
            if (!Items[i].IsHeader)
                continue;
            var any = false;
            for (var j = i + 1; j < Items.Count && !Items[j].IsHeader; j++)
                any |= Items[j].IsVisible;
            Items[i].IsVisible = any;
        }

        OnPropertyChanged(nameof(VisibleItems));
        if (SelectedItem is { IsVisible: false })
            SelectedItem = AutoSelectFirst ? Items.FirstOrDefault(i => i.IsPage && i.IsVisible) : null;
    }

    public override void Tick() => Content?.Tick();

    public override void OnShown()
    {
        _visible = true;
        UpdateVisibility();
        if (AutoSelectFirst)
            SelectedItem ??= Items.FirstOrDefault(i => i.IsPage && i.IsVisible);
        Activate(Content);
    }

    public override void OnHidden()
    {
        _visible = false;
        Activate(null);
    }
}
