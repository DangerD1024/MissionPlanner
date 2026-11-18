using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.Logs;

/// <summary>A message type, instance or field in the log browser's tree.</summary>
public sealed partial class LogTreeNode : ObservableObject
{
    internal LogTreeNode(string name, string type, string? instance, string? field, string? detail = null)
    {
        Name = name;
        Type = type;
        Instance = instance;
        Field = field;
        Detail = detail;
    }

    public string Name { get; }

    /// <summary>Message count or field count, shown dimmed.</summary>
    public string? Detail { get; }

    public string Type { get; }

    public string? Instance { get; }

    public string? Field { get; }

    public bool IsField => Field != null;

    public bool IsGroup => Field == null;

    public string Key => LogSeries.SeriesName(Type, Instance, Field ?? "");

    internal List<LogTreeNode> AllChildren { get; } = new();

    /// <summary>The children that pass the filter.</summary>
    public ObservableCollection<LogTreeNode> Children { get; } = new();

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>Graphed (field nodes).</summary>
    [ObservableProperty]
    private bool _isChecked;

    /// <summary>The series is being read.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>The series colour (#rrggbb) while graphed.</summary>
    [ObservableProperty]
    private string? _color;

    internal Action<LogTreeNode, bool>? CheckedChanged { get; set; }

    partial void OnIsCheckedChanged(bool value) => CheckedChanged?.Invoke(this, value);

    public override string ToString() => Key;
}

/// <summary>A line on the graph.</summary>
public sealed partial class GraphSeries : ObservableObject
{
    internal GraphSeries(LogTreeNode node, string color, double[] x, double[] y, int sourceCount, double min, double max)
    {
        Node = node;
        Key = node.Key;
        Color = color;
        X = x;
        Y = y;
        SourceCount = sourceCount;
        Min = min;
        Max = max;
    }

    internal LogTreeNode Node { get; }

    public string Key { get; }

    public string Name => Key;

    /// <summary>#rrggbb</summary>
    public string Color { get; }

    /// <summary>Seconds, ascending. At most <see cref="SeriesDownsampler.DefaultMaxPoints"/> points.</summary>
    public double[] X { get; }

    public double[] Y { get; }

    /// <summary>Samples in the log (more than drawn when thinned).</summary>
    public int SourceCount { get; }

    public double Min { get; }

    public double Max { get; }

    public bool IsThinned => X.Length < SourceCount;

    public string Detail =>
        string.Create(CultureInfo.InvariantCulture, $"{SourceCount:N0} pts  ·  {Min:G5} … {Max:G5}") +
        (IsThinned ? string.Create(CultureInfo.InvariantCulture, $"  ·  drawn {X.Length:N0}") : "");

    /// <summary>Drawn against the right-hand Y axis.</summary>
    [ObservableProperty]
    private bool _rightAxis;
}

/// <summary>
/// Log Browser: opens a DataFlash (.bin/.log) or telemetry (.tlog) log off the UI thread,
/// lists its message types and fields (with a filter), graphs ticked fields against time,
/// marks mode changes, errors and messages, and shows the GPS track.
/// </summary>
public sealed partial class LogBrowserViewModel : PageViewModel, IDisposable
{
    /// <summary>Track points handed to the map.</summary>
    internal const int MaxTrackPoints = 20_000;

    private static readonly string[] Palette =
    [
        "#4fa3e3", "#f0b429", "#e5534b", "#8cc63f", "#b07fe0", "#3fc6c6", "#f08c3f", "#e06fb0", "#a3b4ff", "#d7d75f",
    ];

    private readonly List<LogTreeNode> _roots = new();
    private readonly HashSet<LogTreeNode> _checked = new();
    private LogSource? _source;
    private int _loadVersion;
    private CancellationTokenSource? _loadCancel;
    private bool _disposed;

    public LogBrowserViewModel(AppServices services)
        : base("Log Browser")
    {
        Services = services;
    }

    public AppServices Services { get; }

    /// <summary>The open log; null before one is opened.</summary>
    public LogSource? Source => _source;

    /// <summary>Top-level tree nodes that pass the filter.</summary>
    public ObservableCollection<LogTreeNode> Nodes { get; } = new();

    public ObservableCollection<GraphSeries> Series { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWelcome))]
    private bool _isLoading;

    [ObservableProperty]
    private string _loadingText = "";

    /// <summary>0..1, or negative while the length of the work is not known.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoadingIndeterminate), nameof(LoadingPercent))]
    private double _loadingFraction = -1;

    public bool LoadingIndeterminate => LoadingFraction < 0;

    public double LoadingPercent => Math.Max(0, LoadingFraction) * 100;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWelcome))]
    private bool _hasLog;

    public bool ShowWelcome => !HasLog && !IsLoading;

    [ObservableProperty]
    private string? _fileName;

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private string _xAxisLabel = "Time (s)";

    [ObservableProperty]
    private string _filter = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGraph))]
    private int _seriesCount;

    public bool HasGraph => SeriesCount > 0;

    /// <summary>Every event in the log.</summary>
    [ObservableProperty]
    private IReadOnlyList<LogEvent> _events = [];

    /// <summary>The events that pass the toggles below; markers are drawn for these.</summary>
    [ObservableProperty]
    private IReadOnlyList<LogEvent> _visibleEvents = [];

    [ObservableProperty]
    private bool _showModes = true;

    [ObservableProperty]
    private bool _showErrors = true;

    [ObservableProperty]
    private bool _showMessages = true;

    [ObservableProperty]
    private bool _showEvents;

    /// <summary>GPS track, thinned for drawing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTrack), nameof(MapVisible))]
    private IReadOnlyList<LogTrackPoint> _track = [];

    public bool HasTrack => Track.Count > 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MapVisible))]
    private bool _showMap = true;

    public bool MapVisible => ShowMap && HasTrack;

    /// <summary>Asks the view to bring a time (seconds) into view on the graph.</summary>
    public event EventHandler<double>? FocusTimeRequested;

    partial void OnFilterChanged(string value) => ApplyFilter();

    partial void OnShowModesChanged(bool value) => UpdateVisibleEvents();

    partial void OnShowErrorsChanged(bool value) => UpdateVisibleEvents();

    partial void OnShowMessagesChanged(bool value) => UpdateVisibleEvents();

    partial void OnShowEventsChanged(bool value) => UpdateVisibleEvents();

    [RelayCommand]
    private async Task Open()
    {
        var path = await Services.Dialogs.OpenFileAsync("Open a log",
            new FileFilter("Logs", "bin", "log", "tlog"),
            new FileFilter("DataFlash logs", "bin", "log"),
            new FileFilter("Telemetry logs", "tlog"));
        if (!string.IsNullOrEmpty(path))
            await OpenFileAsync(path);
    }

    /// <summary>Opens a log; the previous one is closed. Indexing runs off the UI thread.</summary>
    public async Task OpenFileAsync(string path)
    {
        if (_disposed)
            return;

        var version = ++_loadVersion;
        _loadCancel?.Cancel();
        var cancel = new CancellationTokenSource();
        _loadCancel = cancel;

        CloseLog();
        FileName = System.IO.Path.GetFileName(path);
        Summary = "";
        Status = null;
        LoadingFraction = -1;
        LoadingText = "Opening " + FileName;
        IsLoading = true;

        var progress = new Progress<LogLoadProgress>(p =>
        {
            if (version != _loadVersion)
                return;
            LoadingFraction = p.Fraction;
            LoadingText = p.Text;
        });

        LogSource source;
        IReadOnlyList<LogTrackPoint> track;
        try
        {
            (source, track) = await Task.Run(() =>
            {
                var s = LogSource.Open(path, progress, cancel.Token);
                return (s, SeriesDownsampler.Stride(s.Track, MaxTrackPoints));
            }, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (version != _loadVersion)
                return;
            IsLoading = false;
            Status = "Could not open the log: " + ex.Message;
            await Services.Dialogs.ShowMessageAsync("Log Browser", $"Could not open {FileName}:\n{ex.Message}", MessageKind.Error);
            return;
        }

        if (version != _loadVersion || _disposed)
        {
            source.Dispose();
            return;
        }

        _source = source;
        BuildTree(source);
        Events = source.Events;
        UpdateVisibleEvents();
        Track = track;
        XAxisLabel = source is TlogReader ? "Time since start of log (s)" : "Time since boot (s)";
        Summary = Describe(source, path);
        HasLog = true;
        IsLoading = false;
        OnPropertyChanged(nameof(Source));
    }

    private static string Describe(LogSource source, string path)
    {
        var parts = new List<string> { source.KindName + " log" };
        try
        {
            parts.Add(LogEntryRow.FormatBytes(new FileInfo(path).Length));
        }
        catch (IOException)
        {
        }

        parts.Add($"{source.MessageTypes.Count} message types");
        if (!double.IsNaN(source.FirstTime) && !double.IsNaN(source.LastTime) && source.LastTime > source.FirstTime)
            parts.Add(LogEvent.FormatTime(source.LastTime - source.FirstTime) + " long");
        if (source.StartTime is { } start)
            parts.Add("started " + start.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        return string.Join("  ·  ", parts);
    }

    private void BuildTree(LogSource source)
    {
        _roots.Clear();
        foreach (var type in source.MessageTypes)
        {
            var detail = type.Count >= 0
                ? type.Count.ToString("N0", CultureInfo.InvariantCulture)
                : null;
            var root = new LogTreeNode(type.Name, type.Name, null, null, detail);
            if (type.Instances.Count > 0)
            {
                foreach (var inst in type.Instances)
                {
                    var instNode = new LogTreeNode($"[{inst}]", type.Name, inst, null);
                    foreach (var field in type.Fields)
                        instNode.AllChildren.Add(FieldNode(type.Name, inst, field));
                    root.AllChildren.Add(instNode);
                }
            }
            else
            {
                foreach (var field in type.Fields)
                    root.AllChildren.Add(FieldNode(type.Name, null, field));
            }

            _roots.Add(root);
        }

        ApplyFilter();
    }

    private LogTreeNode FieldNode(string type, string? instance, string field) =>
        new(field, type, instance, field) { CheckedChanged = OnNodeChecked };

    /// <summary>
    /// Shows the types, instances and fields whose name contains the filter (case-insensitive). A
    /// matching type shows all its fields; "ATT.Ro" matches the Ro… fields of ATT… types.
    /// </summary>
    private void ApplyFilter()
    {
        var f = Filter.Trim();
        string? typePart = null, fieldPart = null;
        var dot = f.IndexOf('.');
        if (dot > 0)
        {
            typePart = f[..dot];
            fieldPart = f[(dot + 1)..];
        }

        var visible = new List<LogTreeNode>();
        foreach (var root in _roots)
        {
            bool show;
            if (f.Length == 0)
                show = Refill(root, null);
            else if (typePart != null)
                show = Contains(root.Name, typePart) && Refill(root, fieldPart!.Length == 0 ? null : fieldPart, expand: true);
            else if (Contains(root.Name, f))
                show = Refill(root, null);
            else
                show = Refill(root, f, expand: true);

            if (show)
                visible.Add(root);
        }

        Replace(Nodes, visible);
    }

    /// <summary>Fills <paramref name="node"/>'s visible children; returns whether anything is left to show.</summary>
    private static bool Refill(LogTreeNode node, string? filter, bool expand = false)
    {
        var shown = new List<LogTreeNode>();
        foreach (var child in node.AllChildren)
        {
            if (child.IsField)
            {
                if (filter == null || Contains(child.Name, filter))
                    shown.Add(child);
            }
            else if (Refill(child, filter, expand))
            {
                shown.Add(child);
            }
        }

        Replace(node.Children, shown);
        if (filter != null && expand && shown.Count > 0)
            node.IsExpanded = true;
        return filter == null || shown.Count > 0;
    }

    private static bool Contains(string text, string part) => text.Contains(part, StringComparison.OrdinalIgnoreCase);

    private static void Replace(ObservableCollection<LogTreeNode> target, List<LogTreeNode> items)
    {
        if (target.Count == items.Count && target.SequenceEqual(items))
            return;
        target.Clear();
        foreach (var item in items)
            target.Add(item);
    }

    /// <summary>All field nodes, filter or not.</summary>
    public IEnumerable<LogTreeNode> AllFields() => _roots.SelectMany(Fields);

    private static IEnumerable<LogTreeNode> Fields(LogTreeNode node) =>
        node.IsField ? [node] : node.AllChildren.SelectMany(Fields);

    /// <summary>Finds a field node, e.g. ("GPS", "0", "Alt").</summary>
    public LogTreeNode? FindField(string type, string? instance, string field) =>
        AllFields().FirstOrDefault(n => n.Type == type && n.Field == field && (n.Instance ?? "") == (instance ?? ""));

    private async void OnNodeChecked(LogTreeNode node, bool isChecked)
    {
        if (!isChecked)
        {
            _checked.Remove(node);
            var existing = Series.FirstOrDefault(s => s.Node == node);
            if (existing != null)
                Series.Remove(existing);
            node.Color = null;
            SeriesCount = Series.Count;
            return;
        }

        var source = _source;
        if (source == null || !_checked.Add(node))
            return;

        node.Color = NextColor();
        node.IsBusy = true;
        try
        {
            var result = await Task.Run(() => Read(source, node));
            if (!node.IsChecked || source != _source || _disposed)
                return;

            if (result == null)
            {
                Status = $"{node.Key} has no numeric values.";
                node.IsChecked = false;
                return;
            }

            var (x, y, count, min, max) = result.Value;
            Series.Add(new GraphSeries(node, node.Color ?? Palette[0], x, y, count, min, max));
            SeriesCount = Series.Count;
            Status = null;
        }
        catch (Exception ex)
        {
            Status = $"Could not read {node.Key}: {ex.Message}";
            node.IsChecked = false;
        }
        finally
        {
            node.IsBusy = false;
        }
    }

    /// <summary>Reads and thins one field. Runs off the UI thread.</summary>
    internal static (double[] X, double[] Y, int Count, double Min, double Max)? Read(LogSource source, LogTreeNode node)
    {
        var series = source.GetSeries(node.Type, node.Instance, node.Field!);
        if (series == null || series.Count == 0)
            return null;

        double min = double.MaxValue, max = double.MinValue;
        foreach (var v in series.Values)
        {
            if (v < min)
                min = v;
            if (v > max)
                max = v;
        }

        var (x, y) = SeriesDownsampler.MinMax(series.Times, series.Values);
        return (x, y, series.Count, min, max);
    }

    private string NextColor()
    {
        var used = _checked.Select(n => n.Color).Where(c => c != null).ToHashSet();
        foreach (var c in Palette)
        {
            if (!used.Contains(c))
                return c;
        }

        return Palette[_checked.Count % Palette.Length];
    }

    [RelayCommand]
    private void RemoveSeries(GraphSeries? series)
    {
        if (series != null)
            series.Node.IsChecked = false;
    }

    [RelayCommand]
    private void ClearGraph()
    {
        foreach (var node in _checked.ToList())
            node.IsChecked = false;
        Series.Clear();
        SeriesCount = 0;
    }

    [RelayCommand]
    private void FocusEvent(LogEvent? e)
    {
        if (e != null)
            FocusTimeRequested?.Invoke(this, e.Time);
    }

    [RelayCommand]
    private void ClearFilter() => Filter = "";

    private void UpdateVisibleEvents()
    {
        VisibleEvents = Events.Where(e => e.Kind switch
        {
            LogEventKind.Mode => ShowModes,
            LogEventKind.Error => ShowErrors,
            LogEventKind.Message => ShowMessages,
            LogEventKind.Event => ShowEvents,
            _ => false,
        }).ToList();
    }

    /// <summary>The track point closest in time (for the map cursor); null without a track.</summary>
    public LogTrackPoint? TrackPointAt(double time)
    {
        var track = Track;
        if (track.Count == 0)
            return null;

        int lo = 0, hi = track.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (track[mid].Time < time)
                lo = mid + 1;
            else
                hi = mid;
        }

        if (lo > 0 && Math.Abs(track[lo - 1].Time - time) < Math.Abs(track[lo].Time - time))
            lo--;
        return track[lo];
    }

    private void CloseLog()
    {
        foreach (var node in _checked)
            node.CheckedChanged = null;
        _checked.Clear();
        Series.Clear();
        SeriesCount = 0;
        _roots.Clear();
        Nodes.Clear();
        Events = [];
        VisibleEvents = [];
        Track = [];
        HasLog = false;
        var old = _source;
        _source = null;
        old?.Dispose();
        OnPropertyChanged(nameof(Source));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _loadCancel?.Cancel();
        CloseLog();
    }
}
