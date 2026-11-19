using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using MissionPlanner.App.Core.Params;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>
/// Full Parameter Tree (Mission Planner's ConfigRawParamsTree): every parameter in an
/// expandable tree grouped by prefix and sub-prefix, with the Full Parameter List's columns and
/// editors, live search (matching groups open by themselves), and edits written in one go.
/// </summary>
public sealed partial class FullParameterTreeViewModel : ParamPageViewModel
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(FullParameterTreeViewModel));

    private List<ParamRow> _rows = [];
    private List<ParamTreeItem> _roots = [];

    // groups the user opened (by prefix), kept across rebuilds; while searching, the ones closed
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _closedInSearch = new(StringComparer.Ordinal);
    private SearchMatcher? _matcher;
    private string _lastSearch = "";

    private bool _shown;
    private bool _dirty = true;
    private int _generation;
    private CancellationTokenSource? _searchDelay;

    public FullParameterTreeViewModel(AppServices services)
        : base("Full Parameter Tree", services)
    {
        services.Params.ParamsChanged += (_, _) => Invalidate();
        services.Params.MetadataChanged += (_, _) => Invalidate();
    }

    /// <summary>
    /// The visible lines: groups and the parameters of open groups, in tree order. Replaced as a
    /// whole when the search changes; opening and closing a group inserts and removes lines.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoMatches))]
    private ObservableCollection<ParamTreeItem> _items = new();

    /// <summary>Top-level groups and parameters.</summary>
    public IReadOnlyList<ParamTreeItem> Roots => _roots;

    public IReadOnlyList<ParamRow> AllRows => _rows;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private int _matchCount;

    /// <summary>True when the vehicle reported defaults (MAVFTP), so the Default column means something.</summary>
    [ObservableProperty]
    private bool _hasDefaults;

    public bool IsEmpty => _rows.Count == 0;

    public bool NoMatches => _rows.Count > 0 && Items.Count == 0;

    public string EmptyText => "No parameters yet. Connect to a vehicle to browse its parameters.";

    public string Summary
    {
        get
        {
            var s = _matcher == null || MatchCount == _rows.Count
                ? $"{_rows.Count} parameters"
                : $"{MatchCount} of {_rows.Count} parameters";
            if (Edits.Count > 0)
                s += $" · {Edits.Count} unsaved";
            return s;
        }
    }

    public override void OnShown()
    {
        _shown = true;
        IsConnected = Services.Session.IsConnected;
        if (_dirty)
        {
            _ = RebuildAsync();
        }
        else
        {
            var snapshot = Services.Params.Snapshot();
            if (snapshot.Count != _rows.Count || !SyncRowValues(snapshot))
                _ = RebuildAsync();
        }
    }

    public override void OnHidden() => _shown = false;

    protected override void OnEditsChanged() => OnPropertyChanged(nameof(Summary));

    private void Invalidate()
    {
        _dirty = true;
        if (_shown)
            _ = RebuildAsync();
    }

    /// <summary>Rebuilds rows and tree from the vehicle's list off the UI thread; open groups stay open.</summary>
    internal async Task RebuildAsync()
    {
        var gen = ++_generation;
        _dirty = false;
        IsLoading = true;

        (List<ParamRow> Rows, List<ParamTreeNode> Tree) built;
        try
        {
            built = await Task.Run(() =>
            {
                var rows = Services.Params.Snapshot().Select(e => new ParamRow(e.Name, e.Value, e.Default, e.Meta)).ToList();
                return (rows, ParamTree.Build(rows.Select(r => r.Name)));
            });
        }
        catch (Exception ex)
        {
            Log.Error("build tree", ex);
            built = ([], []);
        }

        if (gen != _generation)
            return;
        IsLoading = false;

        AttachRows(built.Rows);
        _rows = built.Rows;
        HasDefaults = _rows.Any(r => r.Default.HasValue);
        var byName = built.Rows.ToDictionary(r => r.Name, StringComparer.Ordinal);
        _roots = built.Tree.Select(n => ToItem(n, 0, byName)).ToList();
        OnPropertyChanged(nameof(Roots));
        OnPropertyChanged(nameof(AllRows));
        OnPropertyChanged(nameof(IsEmpty));
        ApplyFilter();
    }

    private ParamTreeItem ToItem(ParamTreeNode node, int depth, Dictionary<string, ParamRow> rows)
    {
        if (!node.IsGroup)
            return ParamTreeItem.Param(rows[node.ParamName!], depth);

        var item = ParamTreeItem.Group(node.Label, depth, node.Children.Select(c => ToItem(c, depth + 1, rows)).ToList());
        item.Toggled = Toggle;
        return item;
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchDelay?.Cancel();
        var cts = new CancellationTokenSource();
        _searchDelay = cts;
        _ = FilterAfterTypingAsync(cts.Token);
    }

    private async Task FilterAfterTypingAsync(CancellationToken cancel)
    {
        try
        {
            await Task.Delay(150, cancel);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        ApplyFilter();
    }

    /// <summary>
    /// Applies the search and lists the visible lines again: without a search, groups the user
    /// opened; with one, every group holding a match (unless closed during this search).
    /// </summary>
    internal void ApplyFilter()
    {
        // groups closed during a search open again for the next one
        var search = SearchText.Trim();
        if (!string.Equals(search, _lastSearch, StringComparison.Ordinal))
            _closedInSearch.Clear();
        _lastSearch = search;
        _matcher = SearchMatcher.Create(search);

        var matches = 0;
        foreach (var root in _roots)
            matches += CountMatches(root);
        MatchCount = matches;

        var lines = new List<ParamTreeItem>();
        foreach (var root in _roots)
            AddVisible(root, lines);

        Items = new ObservableCollection<ParamTreeItem>(lines);
        OnPropertyChanged(nameof(Summary));
    }

    private bool Matches(ParamRow row) => _matcher == null || _matcher.Matches(row.Name, row.DisplayName, row.Description);

    private int CountMatches(ParamTreeItem item)
    {
        if (item.Row != null)
            return Matches(item.Row) ? 1 : 0;

        var n = 0;
        foreach (var child in item.Children)
            n += CountMatches(child);
        item.MatchCount = n;
        item.IsSearching = _matcher != null;
        item.IsExpanded = IsOpen(item);
        return n;
    }

    private bool IsOpen(ParamTreeItem group) =>
        _matcher != null ? !_closedInSearch.Contains(group.Label) : _expanded.Contains(group.Label);

    private bool IsShown(ParamTreeItem item) => item.Row != null ? Matches(item.Row) : item.MatchCount > 0;

    private void AddVisible(ParamTreeItem item, List<ParamTreeItem> lines)
    {
        if (!IsShown(item))
            return;
        lines.Add(item);
        if (item.IsGroup && item.IsExpanded)
        {
            foreach (var child in item.Children)
                AddVisible(child, lines);
        }
    }

    /// <summary>Opens or closes a group, inserting or removing only its lines (the list keeps its scroll position).</summary>
    public void Toggle(ParamTreeItem group)
    {
        if (!group.IsGroup)
            return;
        SetOpen(group, !group.IsExpanded);
        group.IsExpanded = !group.IsExpanded;

        var index = Items.IndexOf(group);
        if (index < 0)
            return;

        if (group.IsExpanded)
        {
            var lines = new List<ParamTreeItem>();
            foreach (var child in group.Children)
                AddVisible(child, lines);
            for (var i = 0; i < lines.Count; i++)
                Items.Insert(index + 1 + i, lines[i]);
        }
        else
        {
            while (index + 1 < Items.Count && Items[index + 1].Depth > group.Depth)
                Items.RemoveAt(index + 1);
        }
    }

    private void SetOpen(ParamTreeItem group, bool open)
    {
        if (_matcher != null)
        {
            if (open)
                _closedInSearch.Remove(group.Label);
            else
                _closedInSearch.Add(group.Label);
        }
        else if (open)
        {
            _expanded.Add(group.Label);
        }
        else
        {
            _expanded.Remove(group.Label);
        }
    }

    private static IEnumerable<ParamTreeItem> Groups(IEnumerable<ParamTreeItem> items) =>
        items.Where(i => i.IsGroup).SelectMany(g => Groups(g.Children).Prepend(g));

    [RelayCommand]
    private void ExpandAll()
    {
        foreach (var g in Groups(_roots))
            SetOpen(g, true);
        ApplyFilter();
    }

    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var g in Groups(_roots))
            SetOpen(g, false);
        ApplyFilter();
    }
}
