using System.Buffers.Binary;
using System.Globalization;
using MissionPlanner.ArduPilot;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.Logs;

/// <summary>
/// DataFlash log (.bin binary or .log text) over <see cref="DFLogBuffer"/>: lists the message
/// types and their numeric fields (instances such as GPS[0] / GPS[1] included), reads a type's
/// columns against TimeUS in seconds, and collects mode changes, errors, messages, events and
/// the GPS track, as Mission Planner's log browser shows them.
/// </summary>
public sealed class LogSeriesExtractor : LogSource
{
    // format characters of numbers; n N Z a are text/arrays, M is a mode name (ModeNum has the number)
    private const string NumericFormats = "bBhHiIqQfdcCeELg";

    // index records that are not data
    private static readonly HashSet<string> MetaTypes = ["FMT", "FMTU", "UNIT", "MULT"];

    private readonly DFLogBuffer _log;
    private readonly bool _ownsLog;
    private readonly Dictionary<string, Layout> _layouts = new();
    private readonly List<LogMessageType> _types = new();
    private readonly List<LogEvent> _events = new();
    private readonly List<LogTrackPoint> _track = new();

    /// <summary>A binary log file: types are read by scanning the file directly (fast path).</summary>
    private readonly bool _binary;

    private sealed record Layout(int TimeColumn, double TimeScale, string[] Fields, int[] Columns, int InstanceColumn);

    /// <summary>Wraps an indexed log. The log is disposed with this object when <paramref name="ownsLog"/>.</summary>
    public LogSeriesExtractor(DFLogBuffer log, string path = "", bool ownsLog = true)
        : base(path)
    {
        _log = log;
        _ownsLog = ownsLog;
        _binary = path.Length > 0 && IsBinaryFile(path);
        BuildTypes();
        Firmware = DetectFirmware();
        ReadEvents();
        ReadTrack();
        ReadTimeRange();
        if (log.dflog.gpsstarttime != DateTime.MinValue)
            StartTime = log.dflog.gpsstarttime;
    }

    /// <summary>Indexes a log file. Slow on large logs: call off the UI thread.</summary>
    public static LogSeriesExtractor Open(string path, IProgress<LogLoadProgress>? progress = null)
    {
        progress?.Report(new LogLoadProgress(-1, "Indexing " + System.IO.Path.GetFileName(path)));
        var log = new DFLogBuffer(path);
        try
        {
            progress?.Report(new LogLoadProgress(-1, "Reading events and GPS track"));
            return new LogSeriesExtractor(log, path);
        }
        catch
        {
            log.Dispose();
            throw;
        }
    }

    public override string KindName => "DataFlash";

    public override IReadOnlyList<LogMessageType> MessageTypes => _types;

    public override IReadOnlyList<LogEvent> Events => _events;

    public override IReadOnlyList<LogTrackPoint> Track => _track;

    public override DateTime? StartTime { get; }

    /// <summary>Vehicle firmware named in the log's messages, when it says.</summary>
    public Firmwares? Firmware { get; }

    private void BuildTypes()
    {
        var seen = new HashSet<string>(_log.SeenMessageTypes);
        var dflog = _log.dflog;
        foreach (var label in dflog.logformat.Values.OrderBy(l => l.Name, StringComparer.Ordinal))
        {
            if (!seen.Contains(label.Name) || MetaTypes.Contains(label.Name) || label.FieldNames == null)
                continue;

            var timeColumn = dflog.FindMessageOffset(label.Name, "TimeUS");
            var timeScale = 1e-6;
            if (timeColumn < 0)
            {
                timeColumn = dflog.FindMessageOffset(label.Name, "TimeMS");
                timeScale = 1e-3;
            }

            if (timeColumn < 0)
            {
                timeColumn = dflog.FindMessageOffset(label.Name, "T");
                timeScale = 1e-3;
            }

            if (timeColumn < 0)
                continue;

            var format = label.Format?.Trim() ?? "";
            var fields = new List<string>();
            var columns = new List<int>();
            for (var i = 0; i < label.FieldNames.Count; i++)
            {
                var name = label.FieldNames[i].Trim();
                if (name.Length == 0)
                    continue;
                // a format that does not match the names is trusted to be numbers; a text field reads as NaN
                if (format.Length == label.FieldNames.Count && !NumericFormats.Contains(format[i]))
                    continue;
                fields.Add(name);
                columns.Add(i + 1);
            }

            // nothing to graph but the clock (MSG, text-only records)
            if (columns.All(c => c == timeColumn))
                continue;

            var instanceColumn = -1;
            var instances = new List<string>();
            if (_log.InstanceType.TryGetValue(label.Id, out var inst))
            {
                instanceColumn = inst.index + 1;
                instances = inst.value.Select(v => v.Trim()).Distinct()
                    .OrderBy(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.MaxValue)
                    .ThenBy(v => v, StringComparer.Ordinal)
                    .ToList();
            }

            _layouts[label.Name] = new Layout(timeColumn, timeScale, fields.ToArray(), columns.ToArray(), instanceColumn);
            _types.Add(new LogMessageType(label.Name, fields, instances));
        }
    }

    protected override LogTable? ReadTable(LogMessageType type, string? instance)
    {
        if (!_layouts.TryGetValue(type.Name, out var layout))
            return null;

        if (_binary && ReadBinary(type.Name, layout, instance) is { } fast)
            return fast;

        var times = new List<double>();
        var columns = new List<double>[layout.Fields.Length];
        for (var i = 0; i < columns.Length; i++)
            columns[i] = new List<double>();

        var filter = instance != null && layout.InstanceColumn > 0 ? instance.Trim() : null;
        var n = 0;
        foreach (var item in _log.GetEnumeratorType(type.Name))
        {
            if ((++n & 0xfff) == 0 && Abandoned)
                return null;

            var raw = item.raw;
            if (raw == null || raw.Length <= layout.TimeColumn || item.msgtype != type.Name)
                continue;

            if (filter != null)
            {
                if (raw.Length <= layout.InstanceColumn || !InstanceMatches(raw[layout.InstanceColumn], filter))
                    continue;
            }

            var t = ToDouble(raw[layout.TimeColumn]);
            if (double.IsNaN(t))
                continue;

            times.Add(t * layout.TimeScale);
            for (var i = 0; i < columns.Length; i++)
            {
                var c = layout.Columns[i];
                columns[i].Add(c < raw.Length ? ToDouble(raw[c]) : double.NaN);
            }
        }

        var dict = new Dictionary<string, double[]>(layout.Fields.Length);
        for (var i = 0; i < layout.Fields.Length; i++)
            dict[layout.Fields[i]] = columns[i].ToArray();
        return new LogTable(times.ToArray(), dict);
    }

    private static bool IsBinaryFile(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            return f.ReadByte() == BinaryLog.HEAD_BYTE1 && f.ReadByte() == BinaryLog.HEAD_BYTE2;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads one type of a binary log by scanning the file and decoding only that type's records
    /// from their bytes. DFLogBuffer decodes every record into boxed objects, which takes tens of
    /// seconds for a type with millions of records (IMU); this takes about a second per 100 MB.
    /// Null when the type cannot be read this way (the caller falls back to DFLogBuffer).
    /// </summary>
    private LogTable? ReadBinary(string typeName, Layout layout, string? instance)
    {
        // record lengths and this type's format, from the FMT records DFLogBuffer collected
        var lengths = new int[256];
        int typeId = -1;
        string? format = null;
        foreach (var (id, fmt) in _log.FMT)
        {
            if (id is < 0 or > 255)
                continue;
            lengths[id] = fmt.length;
            if (fmt.name == typeName)
            {
                typeId = id;
                format = fmt.format;
            }
        }

        lengths[0x80] = 89;
        if (typeId < 0 || format == null || lengths[typeId] < 3)
            return null;

        // byte offset of each field in the record (after the 3 header bytes)
        var offsets = new int[format.Length + 1];
        for (var i = 0; i < format.Length; i++)
        {
            var size = FieldSize(format[i]);
            if (size < 0)
                return null;
            offsets[i + 1] = offsets[i] + size;
        }

        if (offsets[^1] + 3 > lengths[typeId])
            return null;

        // raw column c (c = 0 is the type name) is format character c - 1
        bool Readable(int column) => column >= 1 && column <= format.Length && NumericFormats.Contains(format[column - 1]);
        if (!Readable(layout.TimeColumn) || layout.Columns.Any(c => !Readable(c)))
            return null;

        double instanceValue = double.NaN;
        var instanceColumn = -1;
        if (instance != null && layout.InstanceColumn > 0)
        {
            if (!Readable(layout.InstanceColumn) ||
                !double.TryParse(instance, NumberStyles.Float, CultureInfo.InvariantCulture, out instanceValue))
                return null;
            instanceColumn = layout.InstanceColumn;
        }

        var times = new List<double>();
        var columns = new List<double>[layout.Fields.Length];
        for (var i = 0; i < columns.Length; i++)
            columns[i] = new List<double>();

        var length = lengths[typeId];
        var buffer = new byte[4 << 20];
        int have = 0, pos = 0, records = 0;
        var eof = false;
        using var file = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan);

        while (true)
        {
            // keep at least one whole record of any size in the buffer
            if (have - pos < 256 && !eof)
            {
                Buffer.BlockCopy(buffer, pos, buffer, 0, have - pos);
                have -= pos;
                pos = 0;
                while (have < buffer.Length)
                {
                    var read = file.Read(buffer, have, buffer.Length - have);
                    if (read <= 0)
                    {
                        eof = true;
                        break;
                    }

                    have += read;
                }

                if ((++records & 0xf) == 0 && Abandoned)
                    return null;
            }

            if (have - pos < 3)
                break;

            if (buffer[pos] != BinaryLog.HEAD_BYTE1 || buffer[pos + 1] != BinaryLog.HEAD_BYTE2)
            {
                pos++;
                continue;
            }

            var type = buffer[pos + 2];
            var len = lengths[type];
            if (len < 3)
            {
                pos++;
                continue;
            }

            if (have - pos < len)
            {
                if (eof)
                    break;
                continue;
            }

            if (type == typeId)
            {
                var body = buffer.AsSpan(pos + 3, len - 3);
                if (instanceColumn < 0 || Decode(body, format, offsets, instanceColumn) == instanceValue)
                {
                    var t = Decode(body, format, offsets, layout.TimeColumn);
                    if (!double.IsNaN(t))
                    {
                        times.Add(t * layout.TimeScale);
                        for (var i = 0; i < columns.Length; i++)
                            columns[i].Add(Decode(body, format, offsets, layout.Columns[i]));
                    }
                }
            }

            pos += len;
        }

        var dict = new Dictionary<string, double[]>(layout.Fields.Length);
        for (var i = 0; i < layout.Fields.Length; i++)
            dict[layout.Fields[i]] = columns[i].ToArray();
        return new LogTable(times.ToArray(), dict);
    }

    /// <summary>Bytes of a field of format character <paramref name="f"/>; -1 when unknown.</summary>
    private static int FieldSize(char f) => f switch
    {
        'b' or 'B' or 'M' => 1,
        'h' or 'H' or 'c' or 'C' or 'g' => 2,
        'i' or 'I' or 'f' or 'e' or 'E' or 'L' or 'n' => 4,
        'q' or 'Q' or 'd' => 8,
        'N' => 16,
        'Z' or 'a' => 64,
        _ => -1,
    };

    /// <summary>The value of raw column <paramref name="column"/> (format character column - 1) of a record body.</summary>
    private static double Decode(ReadOnlySpan<byte> body, string format, int[] offsets, int column)
    {
        var s = body[offsets[column - 1]..];
        return format[column - 1] switch
        {
            'b' => (sbyte)s[0],
            'B' or 'M' => s[0],
            'h' => BinaryPrimitives.ReadInt16LittleEndian(s),
            'H' => BinaryPrimitives.ReadUInt16LittleEndian(s),
            'i' => BinaryPrimitives.ReadInt32LittleEndian(s),
            'I' => BinaryPrimitives.ReadUInt32LittleEndian(s),
            'q' => BinaryPrimitives.ReadInt64LittleEndian(s),
            'Q' => BinaryPrimitives.ReadUInt64LittleEndian(s),
            'f' => BinaryPrimitives.ReadSingleLittleEndian(s),
            'd' => BinaryPrimitives.ReadDoubleLittleEndian(s),
            'g' => (double)BinaryPrimitives.ReadHalfLittleEndian(s),
            'c' => BinaryPrimitives.ReadInt16LittleEndian(s) / 100.0,
            'C' => BinaryPrimitives.ReadUInt16LittleEndian(s) / 100.0,
            'e' => BinaryPrimitives.ReadInt32LittleEndian(s) / 100.0,
            'E' => BinaryPrimitives.ReadUInt32LittleEndian(s) / 100.0,
            'L' => BinaryPrimitives.ReadInt32LittleEndian(s) / 10000000.0,
            _ => double.NaN,
        };
    }

    private static bool InstanceMatches(object? value, string instance)
    {
        var text = value is string s ? s.Trim() : Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
        return text == instance;
    }

    private double ItemTime(DFLog.DFItem item)
    {
        if (!_layouts.TryGetValue(item.msgtype, out var layout) || item.raw.Length <= layout.TimeColumn)
            return double.NaN;
        return ToDouble(item.raw[layout.TimeColumn]) * layout.TimeScale;
    }

    private Firmwares? DetectFirmware()
    {
        var col = _log.dflog.FindMessageOffset("MSG", "Message");
        if (col < 0)
            return null;

        var n = 0;
        foreach (var item in _log.GetEnumeratorType("MSG"))
        {
            if (++n > 200)
                break;
            if (item.raw.Length <= col)
                continue;
            var text = item.GetItem(col)?.Trim() ?? "";
            if (FirmwareFromText(text) is { } fw)
                return fw;
        }

        return null;
    }

    internal static Firmwares? FirmwareFromText(string text)
    {
        if (text.StartsWith("ArduPlane", StringComparison.Ordinal))
            return Firmwares.ArduPlane;
        if (text.StartsWith("ArduCopter", StringComparison.Ordinal) || text.StartsWith("Blimp", StringComparison.Ordinal))
            return Firmwares.ArduCopter2;
        if (text.StartsWith("ArduRover", StringComparison.Ordinal) || text.StartsWith("Rover", StringComparison.Ordinal))
            return Firmwares.ArduRover;
        if (text.StartsWith("ArduSub", StringComparison.Ordinal))
            return Firmwares.ArduSub;
        if (text.StartsWith("AntennaTracker", StringComparison.Ordinal))
            return Firmwares.ArduTracker;
        return null;
    }

    /// <summary>The name of a flight mode number for a firmware; null when unknown.</summary>
    internal static string? ModeName(Firmwares? firmware, int number)
    {
        if (firmware == null)
            return null;
        try
        {
            foreach (var mode in Common.getModesList(firmware.Value))
            {
                if (mode.Key == number)
                    return mode.Value;
            }
        }
        catch (Exception)
        {
            // no mode table for this firmware
        }

        return null;
    }

    private void ReadEvents()
    {
        var dflog = _log.dflog;

        if (_layouts.ContainsKey("MODE"))
        {
            var modeCol = dflog.FindMessageOffset("MODE", "Mode");
            var numCol = dflog.FindMessageOffset("MODE", "ModeNum");
            foreach (var item in _log.GetEnumeratorType("MODE"))
            {
                var t = ItemTime(item);
                if (double.IsNaN(t))
                    continue;

                string? name = null;
                var number = numCol > 0 && numCol < item.raw.Length ? ToDouble(item.raw[numCol]) : double.NaN;
                if (modeCol > 0 && modeCol < item.raw.Length)
                {
                    var text = item.GetItem(modeCol)?.Trim() ?? "";
                    if (text.Length > 0 && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                        name = text;
                    else if (double.IsNaN(number) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                        number = n;
                }

                if (name == null && !double.IsNaN(number))
                    name = ModeName(Firmware, (int)number) ?? "Mode " + (int)number;
                if (name != null)
                    _events.Add(new LogEvent(t, LogEventKind.Mode, name));
            }
        }

        if (_layouts.ContainsKey("ERR"))
        {
            var subCol = dflog.FindMessageOffset("ERR", "Subsys");
            var codeCol = dflog.FindMessageOffset("ERR", "ECode");
            foreach (var item in _log.GetEnumeratorType("ERR"))
            {
                var t = ItemTime(item);
                if (double.IsNaN(t) || subCol < 0 || codeCol < 0 || item.raw.Length <= Math.Max(subCol, codeCol))
                    continue;
                var sub = ToDouble(item.raw[subCol]);
                var code = ToDouble(item.raw[codeCol]);
                var subName = double.IsNaN(sub) ? "?" : ((DFLog.LogErrorSubsystem)(int)sub).ToString();
                _events.Add(new LogEvent(t, LogEventKind.Error,
                    $"ERR {subName}-{(double.IsNaN(code) ? "?" : ((int)code).ToString(CultureInfo.InvariantCulture))}"));
            }
        }

        if (_layouts.ContainsKey("EV"))
        {
            var idCol = dflog.FindMessageOffset("EV", "Id");
            foreach (var item in _log.GetEnumeratorType("EV"))
            {
                var t = ItemTime(item);
                if (double.IsNaN(t) || idCol < 0 || item.raw.Length <= idCol)
                    continue;
                var id = ToDouble(item.raw[idCol]);
                if (double.IsNaN(id))
                    continue;
                _events.Add(new LogEvent(t, LogEventKind.Event, ((DFLog.Log_Event)(int)id).ToString()));
            }
        }

        // MSG has text only, so it is not in the graphable types; find its time column here
        var msgCol = dflog.FindMessageOffset("MSG", "Message");
        var msgTime = dflog.FindMessageOffset("MSG", "TimeUS");
        var msgScale = 1e-6;
        if (msgTime < 0)
        {
            msgTime = dflog.FindMessageOffset("MSG", "TimeMS");
            msgScale = 1e-3;
        }

        if (msgCol > 0 && msgTime > 0)
        {
            foreach (var item in _log.GetEnumeratorType("MSG"))
            {
                if (item.raw.Length <= Math.Max(msgCol, msgTime))
                    continue;
                var t = ToDouble(item.raw[msgTime]) * msgScale;
                var text = item.GetItem(msgCol)?.Trim() ?? "";
                if (!double.IsNaN(t) && text.Length > 0)
                    _events.Add(new LogEvent(t, LogEventKind.Message, text));
            }
        }

        _events.Sort((a, b) => a.Time.CompareTo(b.Time));
    }

    private void ReadTrack()
    {
        foreach (var name in new[] { "GPS", "POS" })
        {
            var type = FindType(name);
            if (type == null || !type.Fields.Contains("Lat") || !type.Fields.Contains("Lng"))
                continue;

            var instance = type.Instances.Count > 0 ? type.Instances[0] : null;
            var table = GetTable(name, instance);
            if (table == null || table.Count == 0)
                continue;

            var lat = table.Columns["Lat"];
            var lng = table.Columns["Lng"];
            table.Columns.TryGetValue("Alt", out var alt);
            table.Columns.TryGetValue("Status", out var status);

            for (var i = 0; i < table.Count; i++)
            {
                if (status != null && status[i] < 3)
                    continue;
                if (double.IsNaN(lat[i]) || double.IsNaN(lng[i]) || (lat[i] == 0 && lng[i] == 0) || Math.Abs(lat[i]) > 90 ||
                    Math.Abs(lng[i]) > 180)
                    continue;
                _track.Add(new LogTrackPoint(table.Times[i], lat[i], lng[i], alt?[i] ?? double.NaN));
            }

            if (_track.Count > 0)
                return;
        }
    }

    private void ReadTimeRange()
    {
        var count = _log.Count;
        for (var i = 0; i < Math.Min(count, 2000); i++)
        {
            var t = ItemTime(_log[(long)i]);
            if (!double.IsNaN(t) && t > 0)
            {
                FirstTime = t;
                break;
            }
        }

        for (var i = count - 1; i >= Math.Max(0, count - 2000); i--)
        {
            var t = ItemTime(_log[(long)i]);
            if (!double.IsNaN(t) && t > 0)
            {
                LastTime = t;
                break;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _ownsLog)
            _log.Dispose();
    }
}
