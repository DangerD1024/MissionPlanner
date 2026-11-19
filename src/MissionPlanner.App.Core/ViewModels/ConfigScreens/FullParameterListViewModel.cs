using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using MissionPlanner.App.Core.Params;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>
/// Full Parameter List (Mission Planner's ConfigRawParams): every parameter in a grid with a
/// prefix list, live search, filters, the editor each parameter calls for (number, documented
/// values, bitmask), queued edits written in one go, .param load/save, compare and reboot.
/// Without a vehicle it shows a loaded file instead.
/// </summary>
public sealed partial class FullParameterListViewModel : ParamPageViewModel
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(FullParameterListViewModel));

    internal static readonly FileFilter[] ParamFileFilters =
    [
        new("Parameter file", "param", "parm"),
        new("All files", "*"),
    ];

    private List<ParamRow> _all = [];
    private HashSet<string> _favourites = new(StringComparer.Ordinal);

    // offline: the rows come from this file instead of the vehicle
    private Dictionary<string, double>? _offlineFile;
    private string? _offlineFileName;
    private IReadOnlyDictionary<string, ParamMeta>? _offlineMeta;

    private bool _shown;
    private bool _dirty = true;
    private int _generation;
    private CancellationTokenSource? _searchDelay;

    public FullParameterListViewModel(AppServices services)
        : base("Full Parameter List", services)
    {
        services.Params.ParamsChanged += (_, _) => Invalidate();
        services.Params.MetadataChanged += (_, _) => Invalidate();
    }

    /// <summary>Every row, favourites first then in natural name order.</summary>
    public IReadOnlyList<ParamRow> AllRows => _all;

    [ObservableProperty]
    private IReadOnlyList<ParamGroup> _groups = [];

    [ObservableProperty]
    private ParamGroup? _selectedGroup;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(IsEmpty), nameof(NoMatches), nameof(NoMatchesText))]
    private IReadOnlyList<ParamRow> _visibleRows = [];

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private bool _showModifiedOnly;

    [ObservableProperty]
    private bool _showNonDefaultOnly;

    /// <summary>True when the vehicle reported defaults (MAVFTP), so the Default column means something.</summary>
    [ObservableProperty]
    private bool _hasDefaults;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOffline))]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand))]
    private string _sourceText = "";

    public bool IsOffline => _offlineFile != null;

    public bool IsEmpty => _all.Count == 0;

    public bool NoMatches => _all.Count > 0 && VisibleRows.Count == 0;

    public string NoMatchesText => SelectedGroup is { IsAll: false } g
        ? $"No parameter in {g.Name} matches the filters. Select All to search every group."
        : "No parameter matches the filters.";

    public string EmptyText => "No parameters yet. Connect to a vehicle, or use Load from file to view a .param file.";

    public string Summary
    {
        get
        {
            var s = VisibleRows.Count == _all.Count
                ? $"{_all.Count} parameters"
                : $"{VisibleRows.Count} of {_all.Count} parameters";
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
            _ = RebuildAsync();
        else
            SyncValues();
    }

    public override void OnHidden() => _shown = false;

    protected override void OnEditsChanged() => OnPropertyChanged(nameof(Summary));

    protected override void OnBusyChanged()
    {
        LoadFromFileCommand.NotifyCanExecuteChanged();
        SaveToFileCommand.NotifyCanExecuteChanged();
        CompareCommand.NotifyCanExecuteChanged();
    }

    // the "Modified" filter follows edits made, written or discarded
    protected override void OnRowsEdited()
    {
        if (ShowModifiedOnly)
            ApplyFilter();
    }

    protected override bool CanWriteNow => base.CanWriteNow && _offlineFile == null;

    private void Invalidate()
    {
        _dirty = true;
        if (_shown)
            _ = RebuildAsync();
    }

    /// <summary>Rebuilds the rows from the vehicle's list (or the offline file) off the UI thread.</summary>
    internal async Task RebuildAsync()
    {
        var gen = ++_generation;
        _dirty = false;
        var offline = _offlineFile;
        var offlineMeta = _offlineMeta;
        var favourites = ParamFavourites.Load();
        IsLoading = true;

        (List<ParamRow> Rows, bool FromVehicle) built;
        try
        {
            built = await Task.Run(() => BuildRows(offline, offlineMeta, favourites));
        }
        catch (Exception ex)
        {
            Log.Error("build rows", ex);
            built = ([], false);
        }

        if (gen != _generation)
            return;
        IsLoading = false;

        if (built.FromVehicle && _offlineFile != null)
        {
            // a vehicle's list replaces the file shown offline; its edits were for the file
            _offlineFile = null;
            _offlineFileName = null;
            _offlineMeta = null;
            Edits.Clear();
        }

        foreach (var row in built.Rows)
            row.FavouriteToggled = OnFavouriteToggled;
        AttachRows(built.Rows);

        _all = built.Rows;
        _favourites = favourites;
        HasDefaults = _all.Any(r => r.Default.HasValue);
        if (!HasDefaults)
            ShowNonDefaultOnly = false;

        var selected = SelectedGroup?.Name;
        Groups = ParamGroups.Build(_all.Select(r => r.Name));
        SelectedGroup = Groups.FirstOrDefault(g => g.Name == selected) ?? Groups.FirstOrDefault();

        SourceText = _offlineFile != null
            ? $"File {_offlineFileName} (not connected)"
            : _all.Count > 0
                ? $"{Services.Session.Cs.firmware} · system {Services.Session.Vehicle.sysid}"
                : "";
        OnPropertyChanged(nameof(AllRows));
        OnPropertyChanged(nameof(IsEmpty));
        ApplyFilter();
    }

    private (List<ParamRow>, bool) BuildRows(Dictionary<string, double>? offline,
        IReadOnlyDictionary<string, ParamMeta>? offlineMeta, HashSet<string> favourites)
    {
        var entries = Services.Params.Snapshot();
        var fromVehicle = entries.Count > 0;
        if (!fromVehicle && offline != null)
        {
            entries = offline
                .Select(kv => new ParamEntry(kv.Key, kv.Value, null, Services.Params.Meta(kv.Key) ?? offlineMeta?.GetValueOrDefault(kv.Key)))
                .ToList();
        }

        var rows = entries.Select(e => new ParamRow(e.Name, e.Value, e.Default, e.Meta, favourites.Contains(e.Name))).ToList();
        var order = ParamFavourites.Order(favourites);
        rows.Sort((a, b) => order(a.Name, b.Name));
        return (rows, fromVehicle);
    }

    /// <summary>Takes in values the vehicle reported since the rows were built (writes from other pages).</summary>
    private void SyncValues()
    {
        if (_offlineFile != null)
            return;
        var snapshot = Services.Params.Snapshot();
        if (snapshot.Count != _all.Count || !SyncRowValues(snapshot))
            _ = RebuildAsync();
    }

    partial void OnSelectedGroupChanged(ParamGroup? value) => ApplyFilter();

    partial void OnShowModifiedOnlyChanged(bool value) => ApplyFilter();

    partial void OnShowNonDefaultOnlyChanged(bool value) => ApplyFilter();

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
            await Task.Delay(120, cancel);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        ApplyFilter();
    }

    /// <summary>Applies the tree selection, search and filter boxes.</summary>
    internal void ApplyFilter()
    {
        VisibleRows = ParamFilter.Apply(_all, new ParamFilterOptions(SelectedGroup, SearchText, ShowModifiedOnly, ShowNonDefaultOnly));
    }

    private void OnFavouriteToggled(ParamRow row)
    {
        ParamFavourites.Set(row.Name, row.IsFavourite);
        SaveSettings();
        if (row.IsFavourite)
            _favourites.Add(row.Name);
        else
            _favourites.Remove(row.Name);

        // re-sort after the click that changed it has finished
        Services.Dispatcher.Post(() =>
        {
            var order = ParamFavourites.Order(_favourites);
            _all.Sort((a, b) => order(a.Name, b.Name));
            ApplyFilter();
        });
    }

    private bool NotBusy => !IsBusy;

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task LoadFromFileAsync()
    {
        var path = await Services.Dialogs.OpenFileAsync("Load parameters from file", ParamFileFilters);
        if (path != null)
            await LoadFileAsync(path);
    }

    /// <summary>
    /// Loads a .param file: with a vehicle list, values that differ become edits (nothing is
    /// written); without one, the file itself is shown.
    /// </summary>
    internal async Task LoadFileAsync(string path)
    {
        var name = Path.GetFileName(path);
        Dictionary<string, double> file;
        try
        {
            file = await Task.Run(() => ParamService.LoadFile(path));
        }
        catch (Exception ex)
        {
            Log.Warn("load " + path, ex);
            await Services.Dialogs.ShowMessageAsync("Load failed", L.Format("Could not read {0}:\n{1}", name, ex.Message), MessageKind.Error);
            return;
        }

        if (_all.Count == 0 || _offlineFile != null)
        {
            _offlineFile = file;
            _offlineFileName = name;
            Edits.Clear();
            try
            {
                _offlineMeta = await ParamMetadata.LoadAsync(FirmwareGuess.FromParamNames(file.Keys), null, download: false);
            }
            catch (Exception ex)
            {
                Log.Warn("offline metadata", ex);
                _offlineMeta = null;
            }

            await RebuildAsync();
            StatusText = $"Showing {file.Count} parameters from {name}.";
            return;
        }

        var result = ParamFileLoad.Compute(file,
            n => RowsByName.TryGetValue(n, out var r) ? r.Value : null,
            n => RowsByName.TryGetValue(n, out var r) ? r.Meta : null);

        foreach (var same in result.Unchanged)
        {
            if (RowsByName.TryGetValue(same, out var r) && r.IsModified)
                QueueEdit(r, r.Value);
        }

        foreach (var (param, value) in result.Edits)
            QueueEdit(RowsByName[param], value);

        var message = result.Edits.Count > 0
            ? $"{result.Edits.Count} value{(result.Edits.Count == 1 ? "" : "s")} from {name} differ from the vehicle and are now marked as edits. " +
              "Nothing has been written: review them, then press Write Params."
            : $"Every parameter in {name} already matches the vehicle.";
        if (result.Missing.Count > 0)
            message += $"\n\nMissing {result.Missing.Count} parameter{(result.Missing.Count == 1 ? "" : "s")} on this vehicle:\n{ListNames(result.Missing)}";
        if (result.ReadOnlySkipped.Count > 0)
            message += $"\n\nRead-only, not changed: {ListNames(result.ReadOnlySkipped)}";

        StatusText = $"Loaded {name}: {result.Edits.Count} edits.";
        if (result.Edits.Count > 0)
        {
            if (ShowModifiedOnly)
                ApplyFilter();
            else
                ShowModifiedOnly = true;
        }

        await Services.Dialogs.ShowMessageAsync("Loaded parameters", message,
            result.Missing.Count > 0 ? MessageKind.Warning : MessageKind.Info);
    }

    private static string ListNames(IReadOnlyList<string> names, int max = 40) =>
        string.Join(" ", names.Take(max)) + (names.Count > max ? $" ... (+{names.Count - max})" : "");

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task SaveToFileAsync()
    {
        if (_all.Count == 0)
        {
            await Services.Dialogs.ShowMessageAsync("Nothing to save", "There are no parameters to save.");
            return;
        }

        var prefix = _offlineFile != null ? "params" : Services.Session.Cs.firmware.ToString();
        var path = await Services.Dialogs.SaveFileAsync("Save parameters to file", $"{prefix} {DateTime.Now:yyyy-MM-dd HH-mm}.param",
            ParamFileFilters[0]);
        if (path == null)
            return;
        await SaveFileAsync(path);
    }

    /// <summary>Writes every row, with unsaved edits, as a .param file.</summary>
    internal async Task SaveFileAsync(string path)
    {
        var entries = _all.Select(r => r.ToEntry()).ToList();
        try
        {
            await Task.Run(() => Services.Params.SaveFile(path, entries));
            StatusText = $"Saved {entries.Count} parameters to {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            Log.Warn("save " + path, ex);
            await Services.Dialogs.ShowMessageAsync("Save failed", L.Format("Could not save {0}:\n{1}", path, ex.Message), MessageKind.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task CompareAsync()
    {
        if (_all.Count == 0)
        {
            await Services.Dialogs.ShowMessageAsync("Nothing to compare", "Connect to a vehicle or load a parameter file first.");
            return;
        }

        var path = await Services.Dialogs.OpenFileAsync("Compare with parameter file", ParamFileFilters);
        if (path != null)
            await CompareWithFileAsync(path);
    }

    /// <summary>Shows the differences with a file; the ticked ones become edits.</summary>
    internal async Task CompareWithFileAsync(string path)
    {
        var name = Path.GetFileName(path);
        Dictionary<string, double> file;
        try
        {
            file = await Task.Run(() => ParamService.LoadFile(path));
        }
        catch (Exception ex)
        {
            await Services.Dialogs.ShowMessageAsync("Load failed", L.Format("Could not read {0}:\n{1}", name, ex.Message), MessageKind.Error);
            return;
        }

        var current = _all.ToDictionary(r => r.Name, r => r.Value, StringComparer.Ordinal);
        var diffs = ParamCompare.Diff(current, file, out var onlyInFile);
        if (diffs.Count == 0)
        {
            await Services.Dialogs.ShowMessageAsync("Compare Params",
                $"No differences with {name}." + (onlyInFile > 0 ? $" ({onlyInFile} parameters in the file are not in the current list.)" : ""));
            return;
        }

        var page = new ParamCompareViewModel(name, diffs, onlyInFile, n => RowsByName.GetValueOrDefault(n)?.Meta);
        if (!await Services.Windows.ShowDialogAsync(page, 860, 640))
            return;

        var applied = 0;
        foreach (var (param, value) in page.Selected)
        {
            if (RowsByName.TryGetValue(param, out var row) && !row.IsReadOnly)
            {
                QueueEdit(row, value);
                applied++;
            }
        }

        StatusText = $"{applied} value{(applied == 1 ? "" : "s")} from {name} queued. Press Write Params to send them.";
        if (applied > 0)
        {
            if (ShowModifiedOnly)
                ApplyFilter();
            else
                ShowModifiedOnly = true;
        }
    }

    private static void SaveSettings()
    {
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
