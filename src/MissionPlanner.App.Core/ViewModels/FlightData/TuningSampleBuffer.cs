namespace MissionPlanner.App.Core.ViewModels.FlightData;

/// <summary>
/// Fixed-size ring of samples for the tuning graph: one shared time column and one value column
/// per series. Samples older than the window are dropped as new ones arrive, so the memory is
/// allocated once and adding a sample allocates nothing. A series added later keeps its own count
/// of valid samples (the most recent ones); series that stay selected keep their history.
/// Not thread-safe: used on the UI thread.
/// </summary>
public sealed class TuningSampleBuffer
{
    private readonly double[] _times;
    private List<Column> _columns = new();
    private int _head; // oldest sample
    private int _count;

    public TuningSampleBuffer(int capacity, double windowSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowSeconds);
        _times = new double[capacity];
        WindowSeconds = windowSeconds;
    }

    public int Capacity => _times.Length;

    public double WindowSeconds { get; }

    /// <summary>Samples held, oldest to newest.</summary>
    public int Count => _count;

    public int SeriesCount => _columns.Count;

    public IReadOnlyList<string> SeriesKeys => _columns.Select(c => c.Key).ToArray();

    /// <summary>Time of the newest sample, or NaN when empty.</summary>
    public double LatestTime => _count == 0 ? double.NaN : _times[Index(_count - 1)];

    /// <summary>Time of the oldest sample, or NaN when empty.</summary>
    public double OldestTime => _count == 0 ? double.NaN : _times[_head];

    /// <summary>
    /// Sets the series, in order. A key that was already present keeps its samples; a new key
    /// starts with none.
    /// </summary>
    public void SetSeries(IReadOnlyList<string> keys)
    {
        var next = new List<Column>(keys.Count);
        foreach (var key in keys)
            next.Add(_columns.FirstOrDefault(c => c.Key == key) ?? new Column(key, new double[Capacity]));
        _columns = next;
    }

    /// <summary>
    /// Appends one sample (one value per series, in series order). Drops the samples that fell out
    /// of the window, or the oldest when full. A time earlier than the newest sample clears the buffer.
    /// </summary>
    public void Add(double time, ReadOnlySpan<double> values)
    {
        if (values.Length != _columns.Count)
            throw new ArgumentException($"expected {_columns.Count} values, got {values.Length}", nameof(values));

        if (_count > 0 && time < _times[Index(_count - 1)])
            Clear();

        var edge = time - WindowSeconds;
        while (_count > 0 && _times[_head] < edge)
            DropOldest();
        if (_count == Capacity)
            DropOldest();

        var i = Index(_count);
        _times[i] = time;
        for (var s = 0; s < _columns.Count; s++)
        {
            var column = _columns[s];
            column.Values[i] = values[s];
            column.Valid++;
        }

        _count++;
    }

    public void Clear()
    {
        _head = 0;
        _count = 0;
        foreach (var column in _columns)
            column.Valid = 0;
    }

    /// <summary>Number of the most recent samples that hold a value for the series.</summary>
    public int ValidCount(int series) => _columns[series].Valid;

    /// <summary>Copies the times, oldest first, into <paramref name="destination"/>; returns <see cref="Count"/>.</summary>
    public int CopyTimes(Span<double> destination) => CopyOrdered(_times, destination);

    /// <summary>
    /// Copies a series' values aligned with <see cref="CopyTimes"/> (index i belongs to time i).
    /// Only the last <see cref="ValidCount"/> entries are meaningful. Returns <see cref="Count"/>.
    /// </summary>
    public int CopyValues(int series, Span<double> destination) => CopyOrdered(_columns[series].Values, destination);

    /// <summary>The i-th sample time, 0 being the oldest.</summary>
    public double TimeAt(int i) => _times[Index(CheckIndex(i))];

    /// <summary>The i-th value of a series, 0 being the oldest sample.</summary>
    public double ValueAt(int series, int i) => _columns[series].Values[Index(CheckIndex(i))];

    private int CopyOrdered(double[] source, Span<double> destination)
    {
        if (destination.Length < _count)
            throw new ArgumentException("destination too small", nameof(destination));

        var first = Math.Min(_count, Capacity - _head);
        source.AsSpan(_head, first).CopyTo(destination);
        if (first < _count)
            source.AsSpan(0, _count - first).CopyTo(destination[first..]);
        return _count;
    }

    private void DropOldest()
    {
        _head = (_head + 1) % Capacity;
        _count--;
        foreach (var column in _columns)
        {
            if (column.Valid > _count)
                column.Valid = _count;
        }
    }

    private int Index(int i) => (_head + i) % Capacity;

    private int CheckIndex(int i)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(i);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(i, _count);
        return i;
    }

    private sealed class Column(string key, double[] values)
    {
        public string Key { get; } = key;

        public double[] Values { get; } = values;

        public int Valid { get; set; }
    }
}
