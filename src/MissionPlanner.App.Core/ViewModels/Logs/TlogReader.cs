using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using MissionPlanner.ArduPilot;

namespace MissionPlanner.App.Core.ViewModels.Logs;

/// <summary>
/// Telemetry log (.tlog: an 8-byte big-endian µs timestamp before each MAVLink packet). One pass
/// with a private <see cref="MAVLinkInterface"/> in log-read mode, as Mission Planner reads them,
/// indexes where each message type's packets are; a type's numeric fields (array elements
/// included) are decoded on demand from those offsets. Heartbeat mode changes and STATUSTEXT
/// become events; GLOBAL_POSITION_INT (or GPS_RAW_INT) the track.
/// </summary>
public sealed class TlogReader : LogSource
{
    private readonly FileStream _file;
    private readonly Dictionary<string, List<Source>> _byName;
    private readonly List<LogMessageType> _types;
    private readonly List<LogEvent> _events;
    private readonly List<LogTrackPoint> _track;
    private readonly Dictionary<int, byte[]> _buffers = new();

    /// <summary>The packets of one message type from one sender.</summary>
    private sealed class Source(uint msgId, string name, uint sysId, byte compId, FieldReader[] fields)
    {
        public uint MsgId { get; } = msgId;
        public string Name { get; } = name;
        public uint SysId { get; } = sysId;
        public byte CompId { get; } = compId;
        public FieldReader[] Fields { get; } = fields;
        public string Instance => $"{SysId}:{CompId}";
        public List<long> Offsets { get; } = new();
        public List<ushort> Lengths { get; } = new();
        public List<double> Times { get; } = new();
    }

    private sealed record FieldReader(string Name, Func<object, double> Read);

    private TlogReader(string path, FileStream file, Dictionary<string, List<Source>> byName, List<LogEvent> events,
        List<LogTrackPoint> track, DateTime? start, double last)
        : base(path)
    {
        _file = file;
        _byName = byName;
        _events = events;
        _track = track;
        StartTime = start;
        FirstTime = 0;
        LastTime = last;
        _types = byName
            .Select(kv => new LogMessageType(kv.Key, kv.Value[0].Fields.Select(f => f.Name).ToList(),
                kv.Value.Count > 1 ? kv.Value.Select(s => s.Instance).ToList() : [],
                kv.Value.Sum(s => s.Times.Count)))
            .Where(t => t.Fields.Count > 0)
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
    }

    public override string KindName => "Telemetry";

    public override IReadOnlyList<LogMessageType> MessageTypes => _types;

    public override IReadOnlyList<LogEvent> Events => _events;

    public override IReadOnlyList<LogTrackPoint> Track => _track;

    public override DateTime? StartTime { get; }

    /// <summary>Indexes a telemetry log. Reads the whole file: call off the UI thread.</summary>
    public static new TlogReader Open(string path, IProgress<LogLoadProgress>? progress = null, CancellationToken cancel = default)
    {
        var byKey = new Dictionary<(uint MsgId, uint SysId, byte CompId), Source>();
        var events = new List<LogEvent>();
        var globalPos = new List<LogTrackPoint>();
        var gpsRaw = new List<LogTrackPoint>();
        DateTime? t0 = null;
        double last = 0;

        // the vehicle: the first sender of an autopilot heartbeat
        (uint SysId, byte CompId)? vehicle = null;
        byte vehicleType = 0, vehicleAutopilot = 0;
        Firmwares? textFirmware = null;
        var modes = new List<(double Time, uint Mode)>();
        uint? lastMode = null;

        var name = System.IO.Path.GetFileName(path);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16))
        using (var mav = new MAVLinkInterface())
        {
            var reader = new BinaryReader(stream);
            mav.logplaybackfile = reader;
            mav.logreadmode = true;
            mav.speechenabled = false;

            var length = stream.Length;
            var lastReport = Environment.TickCount64;
            progress?.Report(new LogLoadProgress(0, "Reading " + name));

            while (stream.Position < length)
            {
                cancel.ThrowIfCancellationRequested();
                var before = stream.Position;

                MAVLink.MAVLinkMessage packet;
                try
                {
                    packet = mav.readPacketAsync().GetAwaiter().GetResult();
                }
                catch (EndOfStreamException)
                {
                    break;
                }
                catch (InvalidDataException)
                {
                    // a corrupt frame: carry on with the next one
                    if (stream.Position <= before)
                        break;
                    continue;
                }

                if (stream.Position <= before)
                    break;

                var now = Environment.TickCount64;
                if (progress != null && now - lastReport > 100)
                {
                    lastReport = now;
                    progress.Report(new LogLoadProgress((double)stream.Position / length, "Reading " + name));
                }

                if (packet == null || packet.Length == 0 || packet.buffer == null)
                    continue;

                var time = mav.lastlogread;
                t0 ??= time;
                var seconds = Math.Max(0, (time - t0.Value).TotalSeconds);
                last = Math.Max(last, seconds);

                var key = (packet.msgid, packet.sysid, packet.compid);
                if (!byKey.TryGetValue(key, out var source))
                {
                    var info = MAVLink.MAVLINK_MESSAGE_INFOS.GetMessageInfo(packet.msgid);
                    if (info.type == null || string.IsNullOrEmpty(info.name))
                        continue;
                    source = new Source(packet.msgid, info.name, packet.sysid, packet.compid, FieldsOf(info.type));
                    byKey[key] = source;
                }

                source.Offsets.Add(stream.Position - packet.Length);
                source.Lengths.Add((ushort)packet.Length);
                source.Times.Add(seconds);

                switch ((MAVLink.MAVLINK_MSG_ID)packet.msgid)
                {
                    case MAVLink.MAVLINK_MSG_ID.HEARTBEAT when packet.data is MAVLink.mavlink_heartbeat_t hb:
                        if (vehicle == null && hb.autopilot != (byte)MAVLink.MAV_AUTOPILOT.INVALID &&
                            hb.type != (byte)MAVLink.MAV_TYPE.GCS)
                        {
                            vehicle = (packet.sysid, packet.compid);
                            vehicleType = hb.type;
                            vehicleAutopilot = hb.autopilot;
                        }

                        if (vehicle == (packet.sysid, packet.compid) && hb.custom_mode != lastMode)
                        {
                            modes.Add((seconds, hb.custom_mode));
                            lastMode = hb.custom_mode;
                        }

                        break;

                    case MAVLink.MAVLINK_MSG_ID.STATUSTEXT when packet.data is MAVLink.mavlink_statustext_t st:
                        var text = Text(st.text);
                        if (text.Length == 0)
                            break;
                        textFirmware ??= LogSeriesExtractor.FirmwareFromText(text);
                        events.Add(new LogEvent(seconds,
                            st.severity <= (byte)MAVLink.MAV_SEVERITY.ERROR ? LogEventKind.Error : LogEventKind.Message, text));
                        break;

                    case MAVLink.MAVLINK_MSG_ID.GLOBAL_POSITION_INT when packet.data is MAVLink.mavlink_global_position_int_t gp:
                        if (gp.lat != 0 || gp.lon != 0)
                            globalPos.Add(new LogTrackPoint(seconds, gp.lat / 1e7, gp.lon / 1e7, gp.alt / 1000.0));
                        break;

                    case MAVLink.MAVLINK_MSG_ID.GPS_RAW_INT when packet.data is MAVLink.mavlink_gps_raw_int_t gr:
                        if (gr.fix_type >= 3 && (gr.lat != 0 || gr.lon != 0))
                            gpsRaw.Add(new LogTrackPoint(seconds, gr.lat / 1e7, gr.lon / 1e7, gr.alt / 1000.0));
                        break;
                }
            }

            mav.logreadmode = false;
            mav.logplaybackfile = null;
        }

        cancel.ThrowIfCancellationRequested();

        var firmware = textFirmware ?? FirmwareOf(vehicleType, vehicleAutopilot);
        foreach (var (time, mode) in modes)
            events.Add(new LogEvent(time, LogEventKind.Mode, LogSeriesExtractor.ModeName(firmware, (int)mode) ?? "Mode " + mode));
        events.Sort((a, b) => a.Time.CompareTo(b.Time));

        var byName = byKey.Values
            .GroupBy(s => s.Name)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.SysId).ThenBy(s => s.CompId).ToList());

        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 18);
        return new TlogReader(path, file, byName, events, globalPos.Count > 0 ? globalPos : gpsRaw, t0, last);
    }

    protected override LogTable? ReadTable(LogMessageType type, string? instance)
    {
        if (!_byName.TryGetValue(type.Name, out var sources))
            return null;

        var source = instance == null ? sources[0] : sources.FirstOrDefault(s => s.Instance == instance);
        if (source == null)
            return null;

        var count = source.Times.Count;
        var fields = source.Fields;
        var columns = new double[fields.Length][];
        for (var f = 0; f < fields.Length; f++)
            columns[f] = new double[count];

        for (var i = 0; i < count; i++)
        {
            if ((i & 0xfff) == 0 && Abandoned)
                return null;

            object? data = null;
            var len = source.Lengths[i];
            if (!_buffers.TryGetValue(len, out var buffer))
                _buffers[len] = buffer = new byte[len];

            _file.Position = source.Offsets[i];
            if (_file.ReadAtLeast(buffer, len, throwOnEndOfStream: false) == len)
            {
                try
                {
                    data = new MAVLink.MAVLinkMessage(buffer).data;
                }
                catch (InvalidDataException)
                {
                }
            }

            for (var f = 0; f < fields.Length; f++)
                columns[f][i] = data == null ? double.NaN : fields[f].Read(data);
        }

        var dict = new Dictionary<string, double[]>(fields.Length);
        for (var f = 0; f < fields.Length; f++)
            dict[fields[f].Name] = columns[f];
        return new LogTable(source.Times.ToArray(), dict);
    }

    private static string Text(byte[]? bytes)
    {
        if (bytes == null)
            return "";
        var end = Array.IndexOf(bytes, (byte)0);
        return Encoding.UTF8.GetString(bytes, 0, end < 0 ? bytes.Length : end).Trim();
    }

    /// <summary>The ArduPilot firmware a heartbeat describes, for mode names.</summary>
    internal static Firmwares? FirmwareOf(byte type, byte autopilot)
    {
        if (autopilot == (byte)MAVLink.MAV_AUTOPILOT.PX4)
            return Firmwares.PX4;
        if (autopilot != (byte)MAVLink.MAV_AUTOPILOT.ARDUPILOTMEGA)
            return null;

        var t = (MAVLink.MAV_TYPE)type;
        switch (t)
        {
            case MAVLink.MAV_TYPE.FIXED_WING:
            case MAVLink.MAV_TYPE.FLAPPING_WING:
            case >= MAVLink.MAV_TYPE.VTOL_DUOROTOR and <= MAVLink.MAV_TYPE.VTOL_RESERVED5:
                return Firmwares.ArduPlane;
            case MAVLink.MAV_TYPE.QUADROTOR:
            case MAVLink.MAV_TYPE.TRICOPTER:
            case MAVLink.MAV_TYPE.HEXAROTOR:
            case MAVLink.MAV_TYPE.OCTOROTOR:
            case MAVLink.MAV_TYPE.HELICOPTER:
            case MAVLink.MAV_TYPE.DODECAROTOR:
            case MAVLink.MAV_TYPE.COAXIAL:
                return Firmwares.ArduCopter2;
            case MAVLink.MAV_TYPE.GROUND_ROVER:
            case MAVLink.MAV_TYPE.SURFACE_BOAT:
                return Firmwares.ArduRover;
            case MAVLink.MAV_TYPE.SUBMARINE:
                return Firmwares.ArduSub;
            case MAVLink.MAV_TYPE.ANTENNA_TRACKER:
                return Firmwares.ArduTracker;
            default:
                return null;
        }
    }

    private static readonly Dictionary<Type, FieldReader[]> FieldCache = new();

    /// <summary>The numeric fields of a message struct, array elements as name[i]; text arrays are left out.</summary>
    private static FieldReader[] FieldsOf(Type type)
    {
        lock (FieldCache)
        {
            if (FieldCache.TryGetValue(type, out var cached))
                return cached;

            var readers = new List<FieldReader>();
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                var ft = field.FieldType;
                if (IsNumber(ft))
                {
                    readers.Add(new FieldReader(field.Name, Compile(type, field, null)));
                    continue;
                }

                if (!ft.IsArray || !IsNumber(ft.GetElementType()!))
                    continue;

                var size = field.GetCustomAttribute<MarshalAsAttribute>()?.SizeConst ?? 0;
                var element = ft.GetElementType()!;
                // char[] in the message definitions arrive as byte[]: names, text, ids
                if (size <= 0 || ((element == typeof(byte) || element == typeof(sbyte)) && size > 8))
                    continue;
                for (var i = 0; i < size; i++)
                    readers.Add(new FieldReader($"{field.Name}[{i}]", Compile(type, field, i)));
            }

            var result = readers.ToArray();
            FieldCache[type] = result;
            return result;
        }
    }

    private static bool IsNumber(Type t) =>
        t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort) || t == typeof(int) ||
        t == typeof(uint) || t == typeof(long) || t == typeof(ulong) || t == typeof(float) || t == typeof(double);

    /// <summary>(object boxedStruct) => (double)((T)boxedStruct).field[index], NaN for a short array.</summary>
    private static Func<object, double> Compile(Type type, FieldInfo field, int? index)
    {
        var obj = Expression.Parameter(typeof(object), "o");
        Expression value = Expression.Field(Expression.Convert(obj, type), field);
        if (index is { } i)
        {
            var array = Expression.Variable(field.FieldType, "a");
            var body = Expression.Block(typeof(double), [array],
                Expression.Assign(array, value),
                Expression.Condition(
                    Expression.AndAlso(Expression.NotEqual(array, Expression.Constant(null, field.FieldType)),
                        Expression.GreaterThan(Expression.ArrayLength(array), Expression.Constant(i))),
                    Expression.Convert(Expression.ArrayIndex(array, Expression.Constant(i)), typeof(double)),
                    Expression.Constant(double.NaN)));
            return Expression.Lambda<Func<object, double>>(body, obj).Compile();
        }

        return Expression.Lambda<Func<object, double>>(Expression.Convert(value, typeof(double)), obj).Compile();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _file.Dispose();
    }
}
