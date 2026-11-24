using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using log4net;
using MissionPlanner.Comms;

namespace MissionPlanner.App.Core.ViewModels.Tools;

/// <summary>A position from a NMEA GGA sentence.</summary>
/// <param name="Alt">Antenna altitude above mean sea level, metres.</param>
public sealed record GgaFix(double Lat, double Lng, double Alt, int Quality, string Satellites, string Hdop)
{
    /// <summary>Mission Planner's location label: "lat lng alt Sats n hdop h".</summary>
    public string Describe(double alt) =>
        string.Create(CultureInfo.InvariantCulture, $"{Lat:0.0000000} {Lng:0.0000000} {alt:0.#} Sats {Satellites} hdop {Hdop}");
}

/// <summary>The GGA parsing of Mission Planner's Follow Me and Moving Base windows.</summary>
public static class NmeaGgaParser
{
    /// <summary>Why a line gave no position.</summary>
    public enum Outcome
    {
        Fix,
        NotGga,
        BadChecksum,
        NoFix,
        Malformed,
    }

    /// <summary>
    /// Reads a $GPGGA / $GNGGA sentence: checksum must match, fix quality 0 is "no fix",
    /// ddmm.mmmm / dddmm.mmmm are turned into degrees and S / W make them negative.
    /// </summary>
    public static Outcome TryParse(string? line, out GgaFix? fix)
    {
        fix = null;
        if (line == null)
            return Outcome.NotGga;
        line = line.Trim();
        if (!line.StartsWith("$GPGGA", StringComparison.Ordinal) && !line.StartsWith("$GNGGA", StringComparison.Ordinal))
            return Outcome.NotGga;

        var items = line.Split(',', '*');
        if (items.Length < 15)
            return Outcome.Malformed;

        var star = line.IndexOf('*');
        if (star < 0 || !string.Equals(items[^1], Checksum(line).ToString("X2", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase))
            return Outcome.BadChecksum;

        if (!int.TryParse(items[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var quality))
            return Outcome.Malformed;
        if (quality == 0)
            return Outcome.NoFix;

        if (!TryDegrees(items[2], out var lat) || !TryDegrees(items[4], out var lng))
            return Outcome.Malformed;
        if (items[3] == "S")
            lat = -lat;
        if (items[5] == "W")
            lng = -lng;

        double.TryParse(items[9], NumberStyles.Float, CultureInfo.InvariantCulture, out var alt);

        fix = new GgaFix(lat, lng, alt, quality, items[7], items[8]);
        return Outcome.Fix;
    }

    /// <summary>XOR of the characters after '$' up to '*'.</summary>
    public static byte Checksum(string sentence)
    {
        byte sum = 0;
        foreach (var c in sentence)
        {
            if (c == '$')
                continue;
            if (c == '*')
                break;
            sum ^= (byte)c;
        }

        return sum;
    }

    /// <summary>NMEA (d)ddmm.mmmm to decimal degrees.</summary>
    internal static bool TryDegrees(string text, out double degrees)
    {
        degrees = 0;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var raw))
            return false;
        var v = raw / 100.0;
        var whole = Math.Truncate(v);
        degrees = whole + (v - whole) / 0.60;
        return true;
    }
}

/// <summary>Lets an action through at most once per period (Follow Me / Moving Base update rate).</summary>
public sealed class RateGate
{
    private DateTime _next = DateTime.MinValue;

    public RateGate(double hz) => Hz = hz;

    public double Hz { get; set; }

    public TimeSpan Period => TimeSpan.FromSeconds(1 / Math.Max(0.01, Hz));

    public bool Due(DateTime now)
    {
        if (now < _next)
            return false;
        _next = now + Period;
        return true;
    }
}

/// <summary>Where the NMEA GPS is: a serial port or a network stream.</summary>
public enum NmeaSourceKind
{
    Serial,

    /// <summary>Listen for a TCP connection (Mission Planner's "TCP Host - 14551").</summary>
    TcpHost,

    /// <summary>Connect to a TCP server.</summary>
    TcpClient,

    /// <summary>Listen for UDP datagrams (Mission Planner's "UDP Host - 14551").</summary>
    UdpHost,

    /// <summary>Send to and receive from a UDP endpoint.</summary>
    UdpClient,
}

/// <summary>Text lines from a GPS.</summary>
public interface INmeaLineSource : IDisposable
{
    void Open();

    /// <summary>The next line without its line end, or null if none came within the timeout.</summary>
    string? ReadLine(int timeoutMs);
}

/// <summary>Splits bytes into lines (LF ends a line, CR is dropped).</summary>
internal sealed class LineSplitter
{
    private readonly StringBuilder _line = new();
    private readonly Queue<string> _lines = new();

    public int Pending => _lines.Count;

    public void Add(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            if (b == '\n')
            {
                _lines.Enqueue(_line.ToString());
                _line.Clear();
            }
            else if (b != '\r')
            {
                // a GPS that never sends a line end must not grow this forever
                if (_line.Length > 4096)
                    _line.Clear();
                _line.Append((char)b);
            }
        }
    }

    public bool TryTake(out string line) => _lines.TryDequeue(out line!);
}

/// <summary>Lines from one of Mission Planner's links (serial, TCP client, UDP).</summary>
public sealed class CommsLineSource(ICommsSerial port) : INmeaLineSource
{
    private readonly LineSplitter _split = new();
    private readonly byte[] _buffer = new byte[4096];

    public void Open()
    {
        if (!port.IsOpen)
            port.Open();
    }

    public string? ReadLine(int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            if (_split.TryTake(out var line))
                return line;
            var available = port.IsOpen ? port.BytesToRead : 0;
            if (available > 0)
            {
                var n = port.Read(_buffer, 0, Math.Min(_buffer.Length, available));
                _split.Add(_buffer.AsSpan(0, n));
                continue;
            }

            if (Environment.TickCount64 >= deadline)
                return null;
            Thread.Sleep(10);
        }
    }

    public void Dispose()
    {
        try
        {
            port.Close();
        }
        catch
        {
            // already gone
        }

        port.Dispose();
    }
}

/// <summary>Accepts TCP connections on a port and reads lines from the latest one.</summary>
public sealed class TcpHostLineSource(int port) : INmeaLineSource
{
    private readonly LineSplitter _split = new();
    private readonly byte[] _buffer = new byte[4096];
    private TcpListener? _listener;
    private TcpClient? _client;

    /// <summary>The bound port (useful when 0 asked for any free one).</summary>
    public int LocalPort => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? port;

    public void Open()
    {
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
    }

    public string? ReadLine(int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            if (_split.TryTake(out var line))
                return line;
            var listener = _listener ?? throw new ObjectDisposedException(nameof(TcpHostLineSource));
            if (listener.Pending())
            {
                // as Mission Planner: the newest connection replaces the old one
                _client?.Dispose();
                _client = listener.AcceptTcpClient();
            }

            if (_client is { Connected: true } c && c.Available > 0)
            {
                var n = c.GetStream().Read(_buffer, 0, Math.Min(_buffer.Length, c.Available));
                _split.Add(_buffer.AsSpan(0, n));
                continue;
            }

            if (Environment.TickCount64 >= deadline)
                return null;
            Thread.Sleep(10);
        }
    }

    public void Dispose()
    {
        _client?.Dispose();
        _client = null;
        _listener?.Stop();
        _listener = null;
    }
}

/// <summary>
/// Reads a NMEA GPS on a worker thread as Mission Planner's Follow Me / Moving Base threads do:
/// every line goes to a raw log, a good GGA updates the position, and at the update rate the last
/// position is handed to <see cref="OnUpdate"/>.
/// </summary>
public sealed class NmeaGpsReader : IDisposable
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(NmeaGpsReader));

    private readonly INmeaLineSource _source;
    private readonly string? _rawLogPath;
    private CancellationTokenSource? _stop;
    private Task _loop = Task.CompletedTask;

    public NmeaGpsReader(INmeaLineSource source, double rateHz, string? rawLogPath)
    {
        _source = source;
        _rawLogPath = rawLogPath;
        Gate = new RateGate(rateHz);
    }

    public RateGate Gate { get; }

    /// <summary>Called on the worker thread for each good fix.</summary>
    public Action<GgaFix>? OnFix { get; init; }

    /// <summary>Called on the worker thread at the update rate with the latest fix.</summary>
    public Action<GgaFix>? OnUpdate { get; init; }

    public GgaFix? LastFix { get; private set; }

    public int Lines { get; private set; }

    public int BadLines { get; private set; }

    public int Updates { get; private set; }

    public bool IsRunning => !_loop.IsCompleted;

    /// <summary>Opens the source (throws if that fails) and starts reading.</summary>
    public void Start()
    {
        _source.Open();
        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        _loop = Task.Factory.StartNew(() => Run(token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public void Stop()
    {
        _stop?.Cancel();
        try
        {
            _loop.Wait(2000);
        }
        catch
        {
            // ended by cancellation
        }

        _source.Dispose();
    }

    public void Dispose() => Stop();

    /// <summary>Handles one line; returns whether an update was sent (tests call this directly).</summary>
    public bool Process(string line, DateTime now)
    {
        Lines++;
        switch (NmeaGgaParser.TryParse(line, out var fix))
        {
            case NmeaGgaParser.Outcome.Fix:
                LastFix = fix;
                OnFix?.Invoke(fix!);
                break;
            case NmeaGgaParser.Outcome.BadChecksum or NmeaGgaParser.Outcome.Malformed:
                BadLines++;
                break;
        }

        if (LastFix is not { } last || last.Lat == 0 || last.Lng == 0 || !Gate.Due(now))
            return false;
        Updates++;
        OnUpdate?.Invoke(last);
        return true;
    }

    private void Run(CancellationToken stop)
    {
        StreamWriter? raw = null;
        try
        {
            if (_rawLogPath != null)
                raw = new StreamWriter(_rawLogPath, append: false);
        }
        catch (Exception ex)
        {
            Log.Warn("raw NMEA log", ex);
        }

        try
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var line = _source.ReadLine(500);
                    if (line == null)
                        continue;
                    raw?.WriteLine(line);
                    Process(line, DateTime.Now);
                }
                catch (Exception ex) when (!stop.IsCancellationRequested)
                {
                    // as Mission Planner: wait a period and keep going
                    Log.Debug("nmea read", ex);
                    stop.WaitHandle.WaitOne(Gate.Period);
                }
            }
        }
        finally
        {
            raw?.Dispose();
        }
    }
}

/// <summary>Opens a GPS source for the Follow Me and Moving Base pages.</summary>
public static class NmeaSources
{
    /// <summary>Mission Planner's TCP / UDP host port for the moving base.</summary>
    public const int DefaultHostPort = 14551;

    /// <param name="address">Serial port name, or "host:port" for the client kinds.</param>
    /// <param name="value">Baud rate for serial, the local port for the host kinds.</param>
    public static INmeaLineSource Create(NmeaSourceKind kind, string address, int value) => kind switch
    {
        NmeaSourceKind.Serial => new CommsLineSource(LinkFactory.Create(LinkSpec.Serial(address, value))),
        NmeaSourceKind.TcpHost => new TcpHostLineSource(value),
        NmeaSourceKind.TcpClient => new CommsLineSource(LinkFactory.Create(LinkSpec.Parse("tcp:" + address))),
        NmeaSourceKind.UdpHost => new CommsLineSource(LinkFactory.Create(LinkSpec.UdpListen(value))),
        NmeaSourceKind.UdpClient => new CommsLineSource(LinkFactory.Create(LinkSpec.Parse("udpout:" + address))),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
