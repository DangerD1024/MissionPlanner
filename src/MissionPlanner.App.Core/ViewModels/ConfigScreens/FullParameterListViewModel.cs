using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using MissionPlanner.App.Core.Params;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>
/// Full Parameter List (Mission Planner's ConfigRawParams): every parameter in a grid with a
/// prefix tree, live search, filters, queued edits written in one go, .param load/save and
/// compare. Without a vehicle it shows a loaded file instead.
/// </summary>
public sealed partial class FullParameterListViewModel : PageViewModel
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(FullParameterListViewModel));

    internal static readonly FileFilter[] ParamFileFilters =
    [
        new("Parameter file", "param", "parm"),
        new("All files", "*"),
    ];

    private readonly ParamEditBuffer _edits = new();
    private List<ParamRow> _all = [];
    private Dictionary<string, ParamRow> _byName = new(StringComparer.Ordinal);
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
        : base("Full Parameter List")
    {
        Services = services;
        services.Params.ParamsChanged += (_, _) => Invalidate();
        services.Params.MetadataChanged += (_, _) => Invalidate();
        _edits.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(PendingCount));
            OnPropertyChanged(nameof(HasPending));
            OnPropertyChanged(nameof(Summary));
        };
    }

    public AppServices Services { get; }

    /// <summary>Edits waiting for Write Params.</summary>
    public ParamEditBuffer Edits => _edits;

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
    [NotifyCanExecuteChangedFor(nameof(WriteCommand), nameof(RefreshCommand))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand), nameof(RefreshCommand), nameof(LoadFromFileCommand),
        nameof(SaveToFileCommand), nameof(CompareCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOffline))]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand))]
    private string _sourceText = "";

    public bool IsOffline => _offlineFile != null;

    public int PendingCount => _edits.Count;

    public bool HasPending => _edits.Count > 0;

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
            if (_edits.Count > 0)
                s += $" · {_edits.Count} unsaved";
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

    public override void Tick() => IsConnected = Services.Session.IsConnected;

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
            _edits.Clear();
        }

        var byName = new Dictionary<string, ParamRow>(built.Rows.Count, StringComparer.Ordinal);
        foreach (var row in built.Rows)
        {
            byName[row.Name] = row;
            row.FavouriteToggled = OnFavouriteToggled;
            if (_edits.TryGet(row.Name, out var pending))
                row.PendingValue = _edits.Set(row.Name, pending, row.Value) ? pending : null;
        }

        _edits.RemoveAll(_edits.Pending.Keys.Where(k => !byName.ContainsKey(k)).ToList());

        _all = built.Rows;
        _byName = byName;
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
        if (snapshot.Count != _all.Count)
        {
            _ = RebuildAsync();
            return;
        }

        foreach (var e in snapshot)
        {
            if (!_byName.TryGetValue(e.Name, out var row))
            {
                _ = RebuildAsync();
                return;
            }

            if (!ParamValues.Same(row.Value, e.Value))
            {
                row.Value = e.Value;
                if (row.PendingValue is { } p)
                    row.PendingValue = _edits.Set(row.Name, p, row.Value) ? p : null;
            }
        }
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

    /// <summary>
    /// Checks a value typed in the grid and queues it: numbers only, read-only parameters are
    /// refused, values outside the documented range need confirmation. Returns true if queued.
    /// </summary>
    public async Task<bool> CommitEditAsync(ParamRow row, string? text)
    {
        if (text == null || text.Trim() == row.ValueText)
        {
            row.IsInvalid = false;
            return false;
        }

        var check = ParamEditBuffer.Validate(row.Name, text, row.Meta);
        switch (check.Result)
        {
            case EditCheck.NotANumber:
                row.IsInvalid = true;
                StatusText = $"{row.Name}: {check.Message}";
                return false;
            case EditCheck.ReadOnly:
                row.IsInvalid = false;
                await Services.Dialogs.ShowMessageAsync("ReadOnly", check.Message, MessageKind.Warning);
                return false;
            case EditCheck.OutOfRange:
                if (!await Services.Dialogs.ConfirmAsync("Out of range", check.Message, "Use value"))
                    return false;
                break;
        }

        row.IsInvalid = false;
        QueueEdit(row, check.Value);
        StatusText = row.IsModified ? $"{row.Name} = {row.ValueText} (not written yet)" : $"{row.Name} unchanged";
        return row.IsModified;
    }

    private void QueueEdit(ParamRow row, double value) =>
        row.PendingValue = _edits.Set(row.Name, value, row.Value) ? value : null;

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

    private bool CanWrite => IsConnected && !IsBusy && _offlineFile == null;

    private bool CanRefresh => IsConnected && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task WriteAsync()
    {
        IsBusy = true;
        try
        {
            var written = await ParamWriteFlow.WriteAsync(Services, _edits, s => StatusText = s);
            foreach (var name in written)
            {
                if (_byName.TryGetValue(name, out var row))
                {
                    row.Value = Services.Params.Get(name) ?? row.DisplayValue;
                    row.PendingValue = null;
                }
            }

            if (written.Count > 0)
                StatusText = $"Wrote {written.Count} parameter{(written.Count == 1 ? "" : "s")}.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            if (await ParamWriteFlow.RefreshAsync(Services, s => StatusText = s))
                StatusText = "Parameters refreshed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void DiscardEdits()
    {
        foreach (var name in _edits.Pending.Keys.ToList())
        {
            if (_byName.TryGetValue(name, out var row))
                row.PendingValue = null;
        }

        _edits.Clear();
        StatusText = "Edits discarded.";
        if (ShowModifiedOnly)
            ApplyFilter();
    }

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
            await Services.Dialogs.ShowMessageAsync("Load failed", $"Could not read {name}:\n{ex.Message}", MessageKind.Error);
            return;
        }

        if (_all.Count == 0 || _offlineFile != null)
        {
            _offlineFile = file;
            _offlineFileName = name;
            _edits.Clear();
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
            n => _byName.TryGetValue(n, out var r) ? r.Value : null,
            n => _byName.TryGetValue(n, out var r) ? r.Meta : null);

        foreach (var same in result.Unchanged)
        {
            if (_byName.TryGetValue(same, out var r) && r.IsModified)
                QueueEdit(r, r.Value);
        }

        foreach (var (param, value) in result.Edits)
            QueueEdit(_byName[param], value);

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
            await Services.Dialogs.ShowMessageAsync("Save failed", $"Could not save {path}:\n{ex.Message}", MessageKind.Error);
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
            await Services.Dialogs.ShowMessageAsync("Load failed", $"Could not read {name}:\n{ex.Message}", MessageKind.Error);
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

        var page = new ParamCompareViewModel(name, diffs, onlyInFile, n => _byName.GetValueOrDefault(n)?.Meta);
        if (!await Services.Windows.ShowDialogAsync(page, 860, 640))
            return;

        var applied = 0;
        foreach (var (param, value) in page.Selected)
        {
            if (_byName.TryGetValue(param, out var row) && !row.IsReadOnly)
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
