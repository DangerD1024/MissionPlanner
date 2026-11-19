using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using MissionPlanner.App.Core.Params;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>A parameter offered by the User Params picker.</summary>
public sealed record ParamPickItem(string Name, string DisplayName)
{
    public bool HasDisplayName => DisplayName.Length > 0;

    public override string ToString() => Name;
}

/// <summary>The User Params list as kept in the settings ("next_user_params", comma separated).</summary>
public static class UserParamList
{
    public const string SettingsKey = "next_user_params";

    /// <summary>Mission Planner's ConfigUserDefined defaults: the RC switch options.</summary>
    public static IReadOnlyList<string> Defaults { get; } =
    [
        "CH6_OPT", "CH7_OPT", "CH8_OPT", "CH9_OPT", "CH10_OPT", "CH11_OPT", "CH12_OPT", "CH13_OPT", "CH14_OPT",
        "CH15_OPT", "CH16_OPT",
        "RC6_OPTION", "RC7_OPTION", "RC8_OPTION", "RC9_OPTION", "RC10_OPTION", "RC11_OPTION", "RC12_OPTION",
        "RC13_OPTION", "RC14_OPTION", "RC15_OPTION", "RC16_OPTION",
    ];

    /// <summary>Names from the stored text: separated by commas, semicolons, spaces or new lines; duplicates dropped.</summary>
    public static List<string> Parse(string? text) =>
        (text ?? "").Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(n => n.Trim().ToUpperInvariant())
        .Where(n => n.Length > 0)
        .Distinct(StringComparer.Ordinal)
        .ToList();

    public static string Format(IEnumerable<string> names) => string.Join(",", names);

    /// <summary>The saved list; the defaults until the user changes it (an emptied list stays empty).</summary>
    public static List<string> Load() =>
        Settings.Instance.ContainsKey(SettingsKey) ? Parse(Settings.Instance[SettingsKey]) : Defaults.ToList();

    public static void Save(IEnumerable<string> names) => Settings.Instance[SettingsKey] = Format(names);
}

/// <summary>
/// User Params (Mission Planner's ConfigUserDefined): a short list of parameters the user picks,
/// with the same editors as the Full Parameter List. The list is kept in the settings; edits are
/// written with Write Params.
/// </summary>
public sealed partial class UserParamsViewModel : ParamPageViewModel
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(UserParamsViewModel));

    /// <summary>Most picker matches listed at once.</summary>
    public const int MaxPickerResults = 200;

    private List<string> _names;
    private List<ParamEntry> _available = [];
    private bool _shown;
    private bool _dirty = true;
    private int _generation;
    private CancellationTokenSource? _searchDelay;

    public UserParamsViewModel(AppServices services)
        : base("User Params", services)
    {
        _names = UserParamList.Load();
        services.Params.ParamsChanged += (_, _) => Invalidate();
        services.Params.MetadataChanged += (_, _) => Invalidate();
    }

    /// <summary>The chosen names in the user's order, including ones this vehicle does not have.</summary>
    public IReadOnlyList<string> Names => _names;

    /// <summary>The chosen parameters this vehicle has, with editors.</summary>
    public ObservableCollection<ParamRow> Rows { get; } = new();

    [ObservableProperty]
    private ParamRow? _selectedRow;

    [ObservableProperty]
    private string _pickerSearch = "";

    [ObservableProperty]
    private IReadOnlyList<ParamPickItem> _pickerResults = [];

    [ObservableProperty]
    private ParamPickItem? _selectedPick;

    [ObservableProperty]
    private string _pickerSummary = "";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _missingText = "";

    public bool IsEmpty => Rows.Count == 0;

    public string EmptyText => _available.Count == 0
        ? "Connect to a vehicle to see its parameters."
        : "No parameter chosen yet. Search on the right and add the ones you want here.";

    public string Summary
    {
        get
        {
            var s = $"{Rows.Count} parameter{(Rows.Count == 1 ? "" : "s")}";
            if (Edits.Count > 0)
                s += $" · {Edits.Count} unsaved";
            return s;
        }
    }

    public override void OnShown()
    {
        _shown = true;
        IsConnected = Services.Session.IsConnected;
        if (_dirty || !SyncRowValues(Services.Params.Snapshot()))
            _ = RebuildAsync();
    }

    public override void OnHidden() => _shown = false;

    protected override void OnEditsChanged() => OnPropertyChanged(nameof(Summary));

    private void Invalidate()
    {
        _dirty = true;
        if (_shown)
            _ = RebuildAsync();
    }

    internal async Task RebuildAsync()
    {
        var gen = ++_generation;
        _dirty = false;
        IsLoading = true;
        List<ParamEntry> entries;
        try
        {
            entries = await Task.Run(() => Services.Params.Snapshot().ToList());
        }
        catch (Exception ex)
        {
            Log.Error("user params", ex);
            entries = [];
        }

        if (gen != _generation)
            return;
        IsLoading = false;
        _available = entries;
        ShowRows();
        ApplyPicker();
        OnPropertyChanged(nameof(EmptyText));
    }

    /// <summary>Lists the chosen parameters the vehicle has, in the user's order.</summary>
    private void ShowRows()
    {
        var byName = _available.ToDictionary(e => e.Name, StringComparer.Ordinal);
        var rows = new List<ParamRow>();
        var missing = new List<string>();
        foreach (var name in _names)
        {
            if (byName.TryGetValue(name, out var e))
                rows.Add(new ParamRow(e.Name, e.Value, e.Default, e.Meta));
            else
                missing.Add(name);
        }

        AttachRows(rows);
        Rows.Clear();
        foreach (var r in rows)
            Rows.Add(r);

        MissingText = missing.Count == 0 || _available.Count == 0
            ? ""
            : $"Not on this vehicle: {string.Join(", ", missing.Take(30))}{(missing.Count > 30 ? $" (+{missing.Count - 30})" : "")}";
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Summary));
    }

    partial void OnPickerSearchChanged(string value)
    {
        _searchDelay?.Cancel();
        var cts = new CancellationTokenSource();
        _searchDelay = cts;
        _ = PickAfterTypingAsync(cts.Token);
    }

    private async Task PickAfterTypingAsync(CancellationToken cancel)
    {
        try
        {
            await Task.Delay(120, cancel);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        ApplyPicker();
    }

    /// <summary>The picker's matches: parameters not chosen yet whose name or documentation matches.</summary>
    internal void ApplyPicker()
    {
        var chosen = new HashSet<string>(_names, StringComparer.Ordinal);
        var matcher = SearchMatcher.Create(PickerSearch);
        var all = _available
            .Where(e => !chosen.Contains(e.Name))
            .Where(e => matcher == null || matcher.Matches(e.Name, e.Meta?.DisplayName, e.Meta?.Description))
            .OrderBy(e => e.Name, NaturalStringComparer.Instance)
            .ToList();
        PickerResults = all.Take(MaxPickerResults).Select(e => new ParamPickItem(e.Name, e.Meta?.DisplayName ?? "")).ToList();
        PickerSummary = _available.Count == 0
            ? "No parameters: connect to a vehicle."
            : all.Count > MaxPickerResults
                ? $"{MaxPickerResults} of {all.Count} matches; type more to narrow."
                : $"{all.Count} match{(all.Count == 1 ? "" : "es")}";
    }

    /// <summary>Adds a parameter to the list (and the settings).</summary>
    [RelayCommand]
    private void Add(ParamPickItem? item)
    {
        item ??= SelectedPick;
        if (item == null)
            return;
        AddName(item.Name);
    }

    internal void AddName(string name)
    {
        if (_names.Contains(name, StringComparer.Ordinal))
            return;
        _names.Add(name);
        SaveNames();
        ShowRows();
        SelectedRow = Rows.FirstOrDefault(r => r.Name == name);
        ApplyPicker();
        StatusText = $"Added {name}.";
    }

    /// <summary>Removes a parameter from the list; its unsaved edit is dropped.</summary>
    [RelayCommand]
    private void Remove(ParamRow? row)
    {
        row ??= SelectedRow;
        if (row == null)
            return;
        _names.Remove(row.Name);
        SaveNames();
        ShowRows();
        ApplyPicker();
        StatusText = $"Removed {row.Name}.";
    }

    [RelayCommand]
    private void MoveUp(ParamRow? row) => Move(row ?? SelectedRow, -1);

    [RelayCommand]
    private void MoveDown(ParamRow? row) => Move(row ?? SelectedRow, 1);

    private void Move(ParamRow? row, int step)
    {
        if (row == null)
            return;
        var from = Rows.IndexOf(row);
        var to = from + step;
        if (from < 0 || to < 0 || to >= Rows.Count)
            return;

        // the stored order includes names this vehicle lacks: swap with the neighbour shown
        var other = Rows[to].Name;
        var a = _names.IndexOf(row.Name);
        var b = _names.IndexOf(other);
        (_names[a], _names[b]) = (_names[b], _names[a]);
        SaveNames();
        Rows.Move(from, to);
        SelectedRow = row;
    }

    /// <summary>Back to Mission Planner's list (the RC switch options).</summary>
    [RelayCommand]
    private async Task RestoreDefaultsAsync()
    {
        if (!await Services.Dialogs.ConfirmAsync("User Params", "Replace your list with the default one (the RC switch options)?", "Replace"))
            return;
        _names = UserParamList.Defaults.ToList();
        SaveNames();
        ShowRows();
        ApplyPicker();
        StatusText = "Default list restored.";
    }

    private void SaveNames()
    {
        UserParamList.Save(_names);
        OnPropertyChanged(nameof(Names));
        try
        {
            Settings.Instance.Save();
        }
        catch (Exception ex)
        {
            Log.Warn("settings save", ex);
        }
    }
}
