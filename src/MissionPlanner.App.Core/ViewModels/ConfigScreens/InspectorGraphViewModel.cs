using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>A fixed-size history of (x, y) points; the oldest drop off when it is full.</summary>
public sealed class InspectorSeries
{
    private double[] _x;
    private double[] _y;
    private int _start;

    public InspectorSeries(string name, int capacity)
    {
        Name = name;
        _x = new double[Math.Max(1, capacity)];
        _y = new double[_x.Length];
    }

    public string Name { get; }

    public int Capacity => _x.Length;

    public int Count { get; private set; }

    public void Add(double x, double y)
    {
        if (Count < _x.Length)
        {
            var i = (_start + Count) % _x.Length;
            _x[i] = x;
            _y[i] = y;
            Count++;
        }
        else
        {
            _x[_start] = x;
            _y[_start] = y;
            _start = (_start + 1) % _x.Length;
        }
    }

    /// <summary>Keeps the newest points that fit the new size.</summary>
    public void Resize(int capacity)
    {
        var (x, y) = ToArrays();
        capacity = Math.Max(1, capacity);
        _x = new double[capacity];
        _y = new double[capacity];
        _start = 0;
        Count = 0;
        for (var i = Math.Max(0, x.Length - capacity); i < x.Length; i++)
            Add(x[i], y[i]);
    }

    public void Clear()
    {
        _start = 0;
        Count = 0;
    }

    /// <summary>The points, oldest first.</summary>
    public (double[] X, double[] Y) ToArrays()
    {
        var x = new double[Count];
        var y = new double[Count];
        for (var i = 0; i < Count; i++)
        {
            var j = (_start + i) % _x.Length;
            x[i] = _x[j];
            y[i] = _y[j];
        }

        return (x, y);
    }
}

/// <summary>A copy of one series for drawing.</summary>
public sealed record InspectorSeriesData(string Name, double[] X, double[] Y);

/// <summary>
/// Live graph of one numeric field from the MAVLink inspector (one line per element for arrays),
/// shown in its own window. Listens to the link only while open.
/// </summary>
public sealed partial class InspectorGraphViewModel : PageViewModel
{
    private readonly object _lock = new();
    private readonly List<InspectorSeries> _series = new();
    private readonly List<double> _numbers = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _subscribed;
    private bool _dirty;
    private int _tick;

    public InspectorGraphViewModel(AppServices services, uint sysId, byte compId, uint msgId, string messageName, InspectorField field)
        : base($"{messageName}.{field.Name} — system {sysId}, component {compId}")
    {
        Services = services;
        SysId = sysId;
        CompId = compId;
        MsgId = msgId;
        MessageName = messageName;
        Field = field;
    }

    public AppServices Services { get; }

    public uint SysId { get; }

    public byte CompId { get; }

    public uint MsgId { get; }

    public string MessageName { get; }

    public InspectorField Field { get; }

    public string YLabel => Field.Units.Length > 0 ? $"{Field.Name} ({Field.Units})" : Field.Name;

    public IReadOnlyList<int> HistoryChoices { get; } = [100, 300, 1000, 3000, 10000];

    /// <summary>Points kept per line.</summary>
    [ObservableProperty]
    private int _history = 300;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private string _latest = "";

    /// <summary>Raised on the UI thread (about 10 times a second) when there are new points to draw.</summary>
    public event EventHandler? DataChanged;

    partial void OnHistoryChanged(int value)
    {
        lock (_lock)
        {
            foreach (var s in _series)
                s.Resize(value);
        }

        _dirty = true;
    }

    public override void OnShown()
    {
        if (_subscribed)
            return;
        Services.Session.Mav.OnPacketReceived += OnPacket;
        Services.Session.Mav.OnPacketSent += OnPacket;
        _subscribed = true;
    }

    public override void OnHidden()
    {
        if (!_subscribed)
            return;
        Services.Session.Mav.OnPacketReceived -= OnPacket;
        Services.Session.Mav.OnPacketSent -= OnPacket;
        _subscribed = false;
    }

    public override void Tick()
    {
        if (++_tick % 2 != 0 || !_dirty || IsPaused)
            return;
        _dirty = false;
        lock (_lock)
        {
            Latest = _series.Count == 0
                ? "waiting for " + MessageName + "…"
                : string.Join("   ", _series.Select(s =>
                {
                    var (_, y) = s.ToArrays();
                    return y.Length == 0 ? "" : $"{s.Name} = {InspectorFormat.FormatNumber(y[^1])}";
                }));
        }

        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Clear()
    {
        lock (_lock)
        {
            foreach (var s in _series)
                s.Clear();
        }

        _dirty = true;
    }

    /// <summary>Copies of the lines, for the view to draw.</summary>
    public IReadOnlyList<InspectorSeriesData> Snapshot()
    {
        lock (_lock)
        {
            return _series.Select(s =>
            {
                var (x, y) = s.ToArrays();
                return new InspectorSeriesData(s.Name, x, y);
            }).ToList();
        }
    }

    private void OnPacket(object? sender, MAVLink.MAVLinkMessage message) => Add(message, _clock.Elapsed.TotalSeconds);

    /// <summary>Adds a packet's value(s) at time <paramref name="seconds"/> if it is the graphed message.</summary>
    internal void Add(MAVLink.MAVLinkMessage message, double seconds)
    {
        if (message.msgid != MsgId || message.sysid != SysId || message.compid != CompId)
            return;
        lock (_lock)
        {
            if (!InspectorFormat.TryGetNumbers(Field, message.data, _numbers))
                return;
            while (_series.Count < _numbers.Count)
            {
                var name = Field.IsArray ? $"{Field.Name}[{_series.Count}]" : Field.Name;
                _series.Add(new InspectorSeries(name, History));
            }

            for (var i = 0; i < _numbers.Count; i++)
                _series[i].Add(seconds, _numbers[i]);
        }

        _dirty = true;
    }
}
