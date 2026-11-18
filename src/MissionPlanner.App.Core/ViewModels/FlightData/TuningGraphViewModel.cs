using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.FlightData;

/// <summary>A CurrentState value drawn on the tuning graph.</summary>
public sealed class TuningSeries
{
    internal TuningSeries(QuickField field, string color, Func<CurrentState, double> getter)
    {
        Field = field;
        Color = color;
        Getter = getter;
    }

    public QuickField Field { get; }

    public string Name => Field.Name;

    public string LegendText => Field.Display;

    /// <summary>Line colour, "#rrggbb".</summary>
    public string Color { get; }

    internal Func<CurrentState, double> Getter { get; }
}

/// <summary>One row of the field picker's checklist.</summary>
public sealed partial class TuningFieldOption(QuickField field) : ObservableObject
{
    public QuickField Field { get; } = field;

    public string Name => Field.Name;

    public string Display => Field.Display;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// Mission Planner's tuning graph over the map: selected numeric CurrentState values sampled on
/// every page tick (20 Hz) into a ring buffer covering the last 30 s, drawn as a scrolling chart.
/// Values are read with getters built once (<see cref="QuickValueCatalog"/>); a tick allocates nothing.
/// Pause freezes the chart (sampling goes on, so Resume shows the latest 30 s); Clear empties it.
/// </summary>
public sealed partial class TuningGraphViewModel : PageViewModel
{
    public const string SettingsKey = "next_tuning_fields";
    public const int MaxSeries = 20;
    public const double WindowSeconds = 30;

    // 30 s at 20 Hz is 600 samples; the window, not the capacity, limits it in practice
    private const int BufferCapacity = 2048;

    // Mission Planner's curve colours, lightened for the dark background
    private static readonly string[] Palette =
    [
        "#ff5c57", "#8fe388", "#6cb8f0", "#ffa8c5", "#f2e05c", "#ffa34d", "#c08cf5", "#f0d9b0",
        "#e0505a", "#d29455", "#4fd1c5", "#d0d5dc", "#9fb4ff", "#b8e05c", "#ff7fd1", "#7fe0ff",
        "#e8b04a", "#a0a0ff", "#5fe0a0", "#ff9f80",
    ];

    private readonly Func<double> _seconds;
    private readonly double[] _sample = new double[MaxSeries];
    private double _origin;
    private List<TuningFieldOption>? _options;
    private bool _syncingOptions;

    public TuningGraphViewModel(AppServices services)
        : this(services, null)
    {
    }

    internal TuningGraphViewModel(AppServices services, Func<double>? seconds)
        : base("Tuning")
    {
        Services = services;
        var watch = Stopwatch.StartNew();
        _seconds = seconds ?? (() => watch.Elapsed.TotalSeconds);
        _origin = _seconds();
        Buffer = new TuningSampleBuffer(BufferCapacity, WindowSeconds);
        ApplySelection(LoadSelection(), save: false);
    }

    public AppServices Services { get; }

    public static IReadOnlyList<string> DefaultFields { get; } = ["roll", "pitch", "nav_roll", "nav_pitch"];

    public TuningSampleBuffer Buffer { get; }

    /// <summary>The graphed values, in legend order.</summary>
    public IReadOnlyList<TuningSeries> Series { get; private set; } = [];

    /// <summary>Raised after each sample (UI thread).</summary>
    public event EventHandler? Sampled;

    /// <summary>Raised when the series change or the samples are cleared; the chart redraws even when paused.</summary>
    public event EventHandler? Reset;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseText))]
    private bool _isPaused;

    [ObservableProperty]
    private bool _isPickerOpen;

    [ObservableProperty]
    private string _fieldFilter = "";

    [ObservableProperty]
    private string _pickerMessage = "";

    public string PauseText => IsPaused ? "Resume" : "Pause";

    public string SelectionSummary => $"{Series.Count} of {MaxSeries} selected";

    /// <summary>The picker's rows matching <see cref="FieldFilter"/>.</summary>
    public ObservableCollection<TuningFieldOption> FieldOptions { get; } = new();

    /// <summary>Seconds on the graph's time axis: since the trace started (shown or cleared), like Mission Planner's.</summary>
    public double Now => _seconds() - _origin;

    public override void Tick()
    {
        var series = Series;
        if (series.Count == 0)
            return;

        var cs = Services.Session.Cs;
        for (var i = 0; i < series.Count; i++)
        {
            double v;
            try
            {
                v = series[i].Getter(cs);
            }
            catch (Exception)
            {
                // some derived values throw before the vehicle has sent what they need
                v = 0;
            }

            _sample[i] = double.IsFinite(v) ? v : 0;
        }

        Buffer.Add(_seconds() - _origin, _sample.AsSpan(0, series.Count));
        Sampled?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The panel was hidden and nothing was sampled meanwhile: start a fresh trace.</summary>
    public override void OnShown() => Restart();

    public override void OnHidden() => IsPickerOpen = false;

    [RelayCommand]
    private void TogglePause() => IsPaused = !IsPaused;

    [RelayCommand]
    private void Clear() => Restart();

    private void Restart()
    {
        _origin = _seconds();
        Buffer.Clear();
        Reset?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void OpenPicker()
    {
        _options ??= QuickValueCatalog.ListFields().Select(CreateOption).ToList();
        SyncOptions();
        PickerMessage = "";
        FieldFilter = "";
        ApplyFilter();
        IsPickerOpen = true;
    }

    [RelayCommand]
    private void ClosePicker() => IsPickerOpen = false;

    [RelayCommand]
    private void ResetFields()
    {
        ApplySelection(DefaultFields);
        SyncOptions();
        PickerMessage = "";
    }

    [RelayCommand]
    private void ClearFields()
    {
        ApplySelection([]);
        SyncOptions();
        PickerMessage = "";
    }

    partial void OnFieldFilterChanged(string value) => ApplyFilter();

    /// <summary>Selects or deselects one value; selection order is legend order. False if the limit was reached.</summary>
    public bool SetFieldSelected(string name, bool selected)
    {
        var names = Series.Select(s => s.Name).ToList();
        if (selected)
        {
            if (names.Contains(name))
                return true;
            if (names.Count >= MaxSeries)
            {
                PickerMessage = $"At most {MaxSeries} values at a time.";
                // untick after the check box has finished setting it
                Services.Dispatcher.Post(SyncOptions);
                return false;
            }

            names.Add(name);
        }
        else if (!names.Remove(name))
        {
            return true;
        }

        PickerMessage = "";
        ApplySelection(names);
        return true;
    }

    private TuningFieldOption CreateOption(QuickField field)
    {
        var option = new TuningFieldOption(field);
        option.PropertyChanged += OnOptionChanged;
        return option;
    }

    private void OnOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncingOptions || e.PropertyName != nameof(TuningFieldOption.IsSelected) || sender is not TuningFieldOption option)
            return;
        SetFieldSelected(option.Name, option.IsSelected);
    }

    /// <summary>Ticks the picker rows that match the graphed series.</summary>
    private void SyncOptions()
    {
        if (_options == null)
            return;

        _syncingOptions = true;
        try
        {
            var names = Series.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var option in _options)
                option.IsSelected = names.Contains(option.Name);
        }
        finally
        {
            _syncingOptions = false;
        }
    }

    private void ApplyFilter()
    {
        if (_options == null)
            return;

        var filter = FieldFilter.Trim();
        FieldOptions.Clear();
        foreach (var option in _options)
        {
            if (filter.Length == 0 ||
                option.Field.Label.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                option.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                FieldOptions.Add(option);
        }
    }

    /// <summary>Rebuilds the series for these property names (unknown ones are skipped). Kept series keep their colour and samples.</summary>
    private void ApplySelection(IEnumerable<string> names, bool save = true)
    {
        var old = Series.ToDictionary(s => s.Name, StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var picked = new List<(QuickField Field, Func<CurrentState, double> Getter, string? Color)>();
        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            if (picked.Count == MaxSeries)
                break;
            if (QuickValueCatalog.GetGetter(name) is not { } getter)
                continue;
            var color = old.TryGetValue(name, out var kept) ? kept.Color : null;
            if (color != null)
                used.Add(color);
            picked.Add((QuickValueCatalog.Describe(name), getter, color));
        }

        var list = new List<TuningSeries>(picked.Count);
        foreach (var (field, getter, color) in picked)
        {
            var c = color ?? NextColor(used);
            used.Add(c);
            list.Add(new TuningSeries(field, c, getter));
        }

        Series = list;
        Buffer.SetSeries(list.Select(s => s.Name).ToArray());
        OnPropertyChanged(nameof(Series));
        OnPropertyChanged(nameof(SelectionSummary));
        Reset?.Invoke(this, EventArgs.Empty);

        if (save)
            Settings.Instance[SettingsKey] = string.Join("|", list.Select(s => s.Name));
    }

    private static string NextColor(HashSet<string> used) =>
        Palette.FirstOrDefault(c => !used.Contains(c)) ?? Palette[used.Count % Palette.Length];

    /// <summary>The saved selection; Mission Planner's defaults the first time.</summary>
    internal static IReadOnlyList<string> LoadSelection()
    {
        var saved = Settings.Instance[SettingsKey];
        if (saved == null)
            return DefaultFields;
        return saved.Split(['|', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
