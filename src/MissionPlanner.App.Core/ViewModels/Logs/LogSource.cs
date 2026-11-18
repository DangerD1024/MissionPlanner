using System.Globalization;

namespace MissionPlanner.App.Core.ViewModels.Logs;

/// <summary>A message type in a log, with the fields that can be graphed.</summary>
public sealed class LogMessageType(string name, IReadOnlyList<string> fields, IReadOnlyList<string> instances, int count = -1)
{
    /// <summary>ATT, GPS (DataFlash) or ATTITUDE, GLOBAL_POSITION_INT (telemetry log).</summary>
    public string Name { get; } = name;

    /// <summary>Numeric fields, in log order.</summary>
    public IReadOnlyList<string> Fields { get; } = fields;

    /// <summary>Instances (GPS[0], GPS[1]...; vehicles in a telemetry log); empty when the type has none.</summary>
    public IReadOnlyList<string> Instances { get; } = instances;

    /// <summary>Number of messages, -1 when not known without reading them.</summary>
    public int Count { get; } = count;

    public override string ToString() => Name;
}

/// <summary>One graphable line: values of a field against time in seconds.</summary>
public sealed class LogSeries(string type, string? instance, string field, double[] times, double[] values)
{
    public string Type { get; } = type;

    public string? Instance { get; } = instance;

    public string Field { get; } = field;

    /// <summary>Seconds: since boot for DataFlash logs, since the first packet for telemetry logs. Ascending.</summary>
    public double[] Times { get; } = times;

    public double[] Values { get; } = values;

    public int Count => Times.Length;

    public string Name => SeriesName(Type, Instance, Field);

    public static string SeriesName(string type, string? instance, string field) =>
        string.IsNullOrEmpty(instance) ? $"{type}.{field}" : $"{type}[{instance}].{field}";
}

public enum LogEventKind
{
    /// <summary>Flight mode change.</summary>
    Mode,

    /// <summary>ERR message, or a STATUSTEXT of error severity.</summary>
    Error,

    /// <summary>MSG / STATUSTEXT text.</summary>
    Message,

    /// <summary>EV event (armed, disarmed, land complete...).</summary>
    Event,
}

/// <summary>Something that happened at a point in the log, drawn as a vertical marker.</summary>
public sealed record LogEvent(double Time, LogEventKind Kind, string Text)
{
    public string TimeText => FormatTime(Time);

    internal static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds))
            return "";
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1
            ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : t.ToString(@"m\:ss\.f", CultureInfo.InvariantCulture);
    }
}

/// <summary>A GPS fix of the vehicle's track.</summary>
public readonly record struct LogTrackPoint(double Time, double Lat, double Lng, double Alt);

/// <summary>All numeric columns of one message type (and instance), read in one pass.</summary>
public sealed class LogTable(double[] times, IReadOnlyDictionary<string, double[]> columns)
{
    public double[] Times { get; } = times;

    public IReadOnlyDictionary<string, double[]> Columns { get; } = columns;

    public int Count => Times.Length;

    /// <summary>Doubles held, for the cache budget.</summary>
    internal long Cells => (long)Times.Length * (Columns.Count + 1);
}

/// <summary>Progress of opening a log: a fraction 0..1, or negative when unknown.</summary>
public readonly record struct LogLoadProgress(double Fraction, string Text);

/// <summary>
/// An opened log: its message types, events and track, and series read on demand. Reading a
/// type takes one pass over its messages; the tables of recently used types are kept, so the
/// other fields of a type come at no cost. Thread-safe: reads are serialised.
/// </summary>
public abstract class LogSource : IDisposable
{
    /// <summary>Doubles of recently read tables kept in memory (about 160 MB).</summary>
    internal const long CacheBudgetCells = 20_000_000;

    private readonly object _gate = new();
    private readonly LinkedList<(string Key, LogTable Table)> _cache = new();
    private long _cachedCells;
    private bool _disposed;
    private volatile bool _abandoned;

    protected LogSource(string path) => Path = path;

    public string Path { get; }

    public abstract string KindName { get; }

    public abstract IReadOnlyList<LogMessageType> MessageTypes { get; }

    public abstract IReadOnlyList<LogEvent> Events { get; }

    public abstract IReadOnlyList<LogTrackPoint> Track { get; }

    /// <summary>Wall-clock time of the start of the log, when known (GPS time / tlog timestamps).</summary>
    public abstract DateTime? StartTime { get; }

    /// <summary>First and last time (seconds) seen while indexing; NaN when unknown.</summary>
    public double FirstTime { get; protected set; } = double.NaN;

    public double LastTime { get; protected set; } = double.NaN;

    public LogMessageType? FindType(string name) => MessageTypes.FirstOrDefault(t => t.Name == name);

    /// <summary>The values of one field over time; null if the type or field is not in the log. Blocks: call off the UI thread.</summary>
    public LogSeries? GetSeries(string type, string? instance, string field)
    {
        var table = GetTable(type, instance);
        if (table == null || !table.Columns.TryGetValue(field, out var column))
            return null;

        // drop the samples that are not numbers (text in a text log, bad lines)
        var count = 0;
        foreach (var v in column)
        {
            if (!double.IsNaN(v))
                count++;
        }

        double[] times, values;
        if (count == column.Length)
        {
            times = table.Times;
            values = column;
        }
        else
        {
            times = new double[count];
            values = new double[count];
            var j = 0;
            for (var i = 0; i < column.Length; i++)
            {
                if (double.IsNaN(column[i]))
                    continue;
                times[j] = table.Times[i];
                values[j] = column[i];
                j++;
            }
        }

        return new LogSeries(type, string.IsNullOrEmpty(instance) ? null : instance, field, times, values);
    }

    /// <summary>All numeric columns of a type; null if the type is not in the log. Blocks: call off the UI thread.</summary>
    public LogTable? GetTable(string type, string? instance)
    {
        var key = type + "\u0001" + instance;
        lock (_gate)
        {
            if (_disposed)
                return null;

            try
            {
                for (var node = _cache.First; node != null; node = node.Next)
                {
                    if (node.Value.Key != key)
                        continue;
                    _cache.Remove(node);
                    _cache.AddFirst(node);
                    return node.Value.Table;
                }

                var msgType = FindType(type);
                if (msgType == null)
                    return null;

                var table = ReadTable(msgType, string.IsNullOrEmpty(instance) ? null : instance);
                if (table == null || Abandoned)
                    return null;

                SortByTime(table);

                _cache.AddFirst((key, table));
                _cachedCells += table.Cells;
                while (_cachedCells > CacheBudgetCells && _cache.Count > 1)
                {
                    var last = _cache.Last!;
                    _cachedCells -= last.Value.Table.Cells;
                    _cache.RemoveLast();
                }

                return table;
            }
            finally
            {
                // closed while this read ran: the read was the last user
                if (Abandoned)
                    DisposeCore();
            }
        }
    }

    /// <summary>Set once the log is closed; long reads check it and stop early.</summary>
    protected bool Abandoned => _abandoned;

    /// <summary>Reads the columns of a type. Called under the read lock.</summary>
    protected abstract LogTable? ReadTable(LogMessageType type, string? instance);

    /// <summary>Graphs need ascending time; a log whose clock steps back is put in order.</summary>
    private static void SortByTime(LogTable table)
    {
        var t = table.Times;
        var sorted = true;
        for (var i = 1; i < t.Length; i++)
        {
            if (t[i] < t[i - 1])
            {
                sorted = false;
                break;
            }
        }

        if (sorted)
            return;

        var order = new int[t.Length];
        for (var i = 0; i < order.Length; i++)
            order[i] = i;
        var keys = (double[])t.Clone();
        Array.Sort(keys, order);
        Array.Copy(keys, t, t.Length);
        foreach (var column in table.Columns.Values)
        {
            var copy = (double[])column.Clone();
            for (var i = 0; i < order.Length; i++)
                column[i] = copy[order[i]];
        }
    }

    /// <summary>Opens a DataFlash (.bin/.log) or telemetry (.tlog) log. Blocks while it indexes: call off the UI thread.</summary>
    public static LogSource Open(string path, IProgress<LogLoadProgress>? progress = null, CancellationToken cancel = default)
    {
        if (IsTlog(path))
            return TlogReader.Open(path, progress, cancel);
        return LogSeriesExtractor.Open(path, progress);
    }

    public static bool IsTlog(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".tlog" or ".rlog")
            return true;
        if (ext is ".bin" or ".log")
            return false;

        // by content: DataFlash binary logs start with A3 95, text logs with "FMT"; MAVLink tlogs
        // start with an 8-byte timestamp
        try
        {
            using var f = File.OpenRead(path);
            Span<byte> head = stackalloc byte[3];
            var n = f.Read(head);
            if (n >= 2 && head[0] == MissionPlanner.Utilities.BinaryLog.HEAD_BYTE1 &&
                head[1] == MissionPlanner.Utilities.BinaryLog.HEAD_BYTE2)
                return false;
            if (n == 3 && head[0] == 'F' && head[1] == 'M' && head[2] == 'T')
                return false;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>A value of a log field as a double; NaN when it is not a number.</summary>
    internal static double ToDouble(object? value) => value switch
    {
        double d => d,
        float f => f,
        int i => i,
        uint u => u,
        short s => s,
        ushort us => us,
        byte b => b,
        sbyte sb => sb,
        long l => l,
        ulong ul => ul,
        bool bo => bo ? 1 : 0,
        string str => double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN,
        _ => double.NaN,
    };

    protected virtual void Dispose(bool disposing)
    {
    }

    /// <summary>Closes the log. Does not wait for a read in progress: that read stops early and closes it.</summary>
    public void Dispose()
    {
        _abandoned = true;
        if (Monitor.TryEnter(_gate))
        {
            try
            {
                DisposeCore();
            }
            finally
            {
                Monitor.Exit(_gate);
            }
        }

        GC.SuppressFinalize(this);
    }

    private void DisposeCore()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cache.Clear();
        _cachedCells = 0;
        Dispose(true);
    }
}
