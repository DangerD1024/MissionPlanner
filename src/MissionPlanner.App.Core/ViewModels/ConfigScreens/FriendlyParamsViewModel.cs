using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using MissionPlanner.App.Core.Params;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

public enum ParamEditorKind
{
    Number,
    Options,
    Bitmask,
}

/// <summary>A documented value of an enumerated parameter.</summary>
public sealed record ParamOption(double Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One bit of a bitmask parameter.</summary>
public sealed partial class BitOption(int bit, string label) : ObservableObject
{
    public int Bit { get; } = bit;

    public string Label { get; } = label;

    internal Action<BitOption>? Toggled { get; set; }

    internal bool Quiet { get; set; }

    [ObservableProperty]
    private bool _isChecked;

    partial void OnIsCheckedChanged(bool value)
    {
        if (!Quiet)
            Toggled?.Invoke(this);
    }
}

/// <summary>
/// One parameter on the Standard / Advanced pages with the editor its documentation calls for:
/// a list for enumerated values, tick boxes for bitmasks, a number box with range otherwise.
/// </summary>
public sealed partial class FriendlyParamItem : ObservableObject
{
    private readonly Action<FriendlyParamItem, double> _edit;
    private bool _updating;
    private decimal? _numericValue;
    private ParamOption? _selectedOption;

    public FriendlyParamItem(string name, ParamMeta meta, double value, Action<FriendlyParamItem, double> edit)
    {
        Name = name;
        Meta = meta;
        _edit = edit;
        _value = value;
        Kind = meta.Values.Count > 0 ? ParamEditorKind.Options
            : meta.Bitmask.Count > 0 ? ParamEditorKind.Bitmask
            : ParamEditorKind.Number;

        Bits = meta.Bitmask.Select(b => new BitOption(b.Key, b.Value) { Toggled = OnBitToggled }).ToList();
        _options = BuildOptions(value);

        var min = meta.Min ?? -1e9;
        var max = meta.Max ?? 1e9;
        Minimum = ToDecimal(Math.Min(min, value));
        Maximum = ToDecimal(Math.Max(max, value));
        var whole = value == Math.Floor(value) && (meta.Min ?? 0) == Math.Floor(meta.Min ?? 0) && (meta.Max ?? 0) == Math.Floor(meta.Max ?? 0);
        Increment = ToDecimal(meta.Increment is > 0 ? meta.Increment.Value : whole ? 1 : 0.1);
        ShowDisplay();
    }

    public string Name { get; }

    public ParamMeta Meta { get; }

    public ParamEditorKind Kind { get; }

    public bool IsNumber => Kind == ParamEditorKind.Number;

    public bool IsOptions => Kind == ParamEditorKind.Options;

    public bool IsBitmask => Kind == ParamEditorKind.Bitmask;

    public string Title => string.IsNullOrEmpty(Meta.DisplayName) ? Name : Meta.DisplayName;

    public string Description => Meta.Description;

    public string Units => Meta.Units;

    public bool HasUnits => Meta.Units.Length > 0;

    public bool IsReadOnly => Meta.ReadOnly;

    public bool IsEditable => !Meta.ReadOnly;

    public bool RebootRequired => Meta.RebootRequired;

    /// <summary>"0 to 100, step 0.5" for number boxes.</summary>
    public string RangeText
    {
        get
        {
            var parts = new List<string>();
            if (Meta.Min is { } a && Meta.Max is { } b)
                parts.Add($"{ParamValues.Format(a)} to {ParamValues.Format(b)}");
            if (Meta.Increment is { } inc)
                parts.Add("step " + ParamValues.Format(inc));
            return string.Join(", ", parts);
        }
    }

    public bool IsOutOfRange => Meta is { Min: { } a, Max: { } b } && (DisplayValue < a || DisplayValue > b);

    public decimal Minimum { get; }

    public decimal Maximum { get; }

    public decimal Increment { get; }

    public IReadOnlyList<BitOption> Bits { get; }

    [ObservableProperty]
    private IReadOnlyList<ParamOption> _options;

    /// <summary>The vehicle's value.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayValue), nameof(ValueText), nameof(IsOutOfRange))]
    private double _value;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayValue), nameof(ValueText), nameof(IsModified), nameof(IsOutOfRange))]
    private double? _pendingValue;

    public double DisplayValue => PendingValue ?? Value;

    public string ValueText => ParamValues.Format(DisplayValue);

    public bool IsModified => PendingValue.HasValue;

    /// <summary>Number box value. Null pushes (from control recycling) are ignored.</summary>
    public decimal? NumericValue
    {
        get => _numericValue;
        set
        {
            if (value == null || value == _numericValue)
                return;
            _numericValue = value;
            OnPropertyChanged();
            if (!_updating)
                _edit(this, (double)value.Value);
        }
    }

    /// <summary>Selected list entry. Null pushes (from control recycling) are ignored.</summary>
    public ParamOption? SelectedOption
    {
        get => _selectedOption;
        set
        {
            if (value == null || ReferenceEquals(value, _selectedOption))
                return;
            _selectedOption = value;
            OnPropertyChanged();
            if (!_updating)
                _edit(this, value.Value);
        }
    }

    /// <summary>Sets the vehicle value and pending edit, updating the editor without reporting an edit.</summary>
    internal void SetValues(double value, double? pending)
    {
        Value = value;
        PendingValue = pending;
        ShowDisplay();
    }

    /// <summary>Marks the result of an edit the page has queued (or dropped).</summary>
    internal void SetPending(double? pending)
    {
        PendingValue = pending;
        if (Kind == ParamEditorKind.Bitmask)
            ShowDisplay();
    }

    private void ShowDisplay()
    {
        _updating = true;
        try
        {
            var v = DisplayValue;
            switch (Kind)
            {
                case ParamEditorKind.Options:
                    var option = Options.FirstOrDefault(o => ParamValues.Same(o.Value, v));
                    if (option == null)
                    {
                        Options = BuildOptions(v);
                        option = Options.First(o => ParamValues.Same(o.Value, v));
                    }

                    _selectedOption = option;
                    OnPropertyChanged(nameof(SelectedOption));
                    break;
                case ParamEditorKind.Bitmask:
                    var bits = ToBits(v);
                    foreach (var b in Bits)
                    {
                        b.Quiet = true;
                        b.IsChecked = b.Bit is >= 0 and < 63 && (bits & (1L << b.Bit)) != 0;
                        b.Quiet = false;
                    }

                    break;
                default:
                    _numericValue = ToDecimal(v);
                    OnPropertyChanged(nameof(NumericValue));
                    break;
            }
        }
        finally
        {
            _updating = false;
        }
    }

    private List<ParamOption> BuildOptions(double current)
    {
        var list = Meta.Values.Select(v => new ParamOption(v.Key, v.Value)).ToList();
        if (Kind == ParamEditorKind.Options && !list.Any(o => ParamValues.Same(o.Value, current)))
            list.Add(new ParamOption(current, $"{ParamValues.Format(current)} (not in the documented list)"));
        return list;
    }

    private void OnBitToggled(BitOption bit)
    {
        if (_updating || bit.Bit is < 0 or >= 63)
            return;
        var bits = ToBits(DisplayValue);
        bits = bit.IsChecked ? bits | (1L << bit.Bit) : bits & ~(1L << bit.Bit);
        _edit(this, bits);
    }

    private static long ToBits(double v) => (long)Math.Round(v);

    /// <summary>A double as the decimal a number box shows, without float noise.</summary>
    internal static decimal ToDecimal(double v)
    {
        if (double.IsNaN(v))
            return 0;
        if (Math.Abs(v) >= 7.9e27)
            return v > 0 ? decimal.MaxValue : decimal.MinValue;
        return v == Math.Floor(v) ? (decimal)v : (decimal)(float)v;
    }
}

/// <summary>
/// Shared body of the Standard Params and Advanced Params pages (Mission Planner's
/// ConfigFriendlyParams): documented parameters with friendly editors, edits queued for Write Params.
/// </summary>
public abstract partial class FriendlyParamsViewModel : PageViewModel
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(FriendlyParamsViewModel));

    private readonly ParamEditBuffer _edits = new();
    private List<FriendlyParamItem> _all = [];
    private Dictionary<string, FriendlyParamItem> _byName = new(StringComparer.Ordinal);
    private bool _shown;
    private bool _dirty = true;
    private int _generation;
    private CancellationTokenSource? _searchDelay;

    protected FriendlyParamsViewModel(string title, AppServices services, bool includeAdvanced)
        : base(title)
    {
        Services = services;
        IncludesAdvanced = includeAdvanced;
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

    /// <summary>Advanced Params also lists advanced and unclassified parameters.</summary>
    public bool IncludesAdvanced { get; }

    public string Intro => IncludesAdvanced
        ? "Every documented parameter: standard and advanced. Changes are written when you press Write Params."
        : "The documented parameters most users need. Changes are written when you press Write Params.";

    public ParamEditBuffer Edits => _edits;

    public IReadOnlyList<FriendlyParamItem> AllItems => _all;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(IsEmpty))]
    private IReadOnlyList<FriendlyParamItem> _visibleItems = [];

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand), nameof(RefreshCommand))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand), nameof(RefreshCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = "";

    public int PendingCount => _edits.Count;

    public bool HasPending => _edits.Count > 0;

    public bool IsEmpty => VisibleItems.Count == 0;

    public string EmptyText => _all.Count == 0
        ? Services.Params.MetadataLoaded || !Services.Params.HasParams
            ? "No documented parameters to show."
            : "Loading parameter documentation..."
        : "No parameter matches the search.";

    public string Summary
    {
        get
        {
            var s = VisibleItems.Count == _all.Count ? L.Format("{0} parameters", _all.Count) : L.Format("{0} of {1} parameters", VisibleItems.Count, _all.Count);
            if (_edits.Count > 0)
                s += $" · {_edits.Count} unsaved";
            return s;
        }
    }

    /// <summary>
    /// Whether a parameter belongs on the page: it needs a display name; Standard shows the
    /// standard level, Advanced everything documented (as Mission Planner, unlabelled ones too).
    /// </summary>
    public static bool Includes(ParamMeta? meta, bool includeAdvanced) =>
        meta != null && !string.IsNullOrEmpty(meta.DisplayName) && (includeAdvanced || meta.IsStandard);

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

    internal async Task RebuildAsync()
    {
        var gen = ++_generation;
        _dirty = false;
        IsLoading = true;
        var favourites = ParamFavourites.Load();
        List<(ParamEntry Entry, ParamMeta Meta)> entries;
        try
        {
            entries = await Task.Run(() => Services.Params.Snapshot()
                .Where(e => Includes(e.Meta, IncludesAdvanced))
                .Select(e => (e, e.Meta!))
                .ToList());
        }
        catch (Exception ex)
        {
            Log.Error("friendly params", ex);
            entries = [];
        }

        if (gen != _generation)
            return;
        IsLoading = false;

        var order = ParamFavourites.Order(favourites);
        entries.Sort((a, b) => order(a.Entry.Name, b.Entry.Name));

        var items = new List<FriendlyParamItem>(entries.Count);
        var byName = new Dictionary<string, FriendlyParamItem>(entries.Count, StringComparer.Ordinal);
        foreach (var (entry, meta) in entries)
        {
            var item = new FriendlyParamItem(entry.Name, meta, entry.Value, OnItemEdited);
            if (_edits.TryGet(entry.Name, out var pending))
                item.SetValues(entry.Value, _edits.Set(entry.Name, pending, entry.Value) ? pending : null);
            items.Add(item);
            byName[entry.Name] = item;
        }

        _edits.RemoveAll(_edits.Pending.Keys.Where(k => !byName.ContainsKey(k)).ToList());
        _all = items;
        _byName = byName;
        OnPropertyChanged(nameof(AllItems));
        OnPropertyChanged(nameof(EmptyText));
        ApplyFilter();
    }

    private void SyncValues()
    {
        foreach (var e in Services.Params.Snapshot())
        {
            if (!_byName.TryGetValue(e.Name, out var item) || ParamValues.Same(item.Value, e.Value))
                continue;
            var pending = item.PendingValue is { } p && _edits.Set(e.Name, p, e.Value) ? p : (double?)null;
            item.SetValues(e.Value, pending);
        }
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

    internal void ApplyFilter()
    {
        var matcher = SearchMatcher.Create(SearchText);
        VisibleItems = matcher == null
            ? _all
            : _all.Where(i => matcher.Matches(i.Name, i.Meta.DisplayName, i.Description)).ToList();
        OnPropertyChanged(nameof(EmptyText));
    }

    private void OnItemEdited(FriendlyParamItem item, double value)
    {
        if (item.IsReadOnly)
            return;
        item.SetPending(_edits.Set(item.Name, value, item.Value) ? value : null);
        StatusText = item.IsModified ? $"{item.Name} = {item.ValueText} (not written yet)" : "";
    }

    private bool CanWrite => IsConnected && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task WriteAsync()
    {
        IsBusy = true;
        try
        {
            var written = await ParamWriteFlow.WriteAsync(Services, _edits, s => StatusText = s);
            foreach (var name in written)
            {
                if (_byName.TryGetValue(name, out var item))
                    item.SetValues(Services.Params.Get(name) ?? item.DisplayValue, null);
            }

            if (written.Count > 0)
                StatusText = $"Wrote {written.Count} parameter{(written.Count == 1 ? "" : "s")}.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanWrite))]
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
            if (_byName.TryGetValue(name, out var item))
                item.SetValues(item.Value, null);
        }

        _edits.Clear();
        StatusText = "Edits discarded.";
    }
}
