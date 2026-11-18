using System.Net;
using System.Net.Sockets;
using log4net;

namespace MissionPlanner.App.Core.Session;

/// <summary>
/// Finds a vehicle without the user picking a port, like QGroundControl: while nothing is
/// connected it listens on UDP 14550 and probes serial ports for a MAVLink heartbeat (read only,
/// nothing is written), then reports the first link that has one. Ports that stay silent are
/// probed again after a pause, so a board plugged in later is found within a few seconds.
/// </summary>
public sealed class AutoConnectService : IDisposable
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(AutoConnectService));

    public const int UdpPort = 14550;
    private static readonly TimeSpan ScanEvery = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan SilentRetry = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProbeTime = TimeSpan.FromSeconds(2.5);

    private readonly Func<bool> _canConnect;
    private readonly Dictionary<string, DateTime> _retryAt = new();
    private CancellationTokenSource? _loop;
    private UdpClient? _udp;

    /// <param name="canConnect">True while nothing is connected or connecting.</param>
    public AutoConnectService(Func<bool> canConnect) => _canConnect = canConnect;

    /// <summary>A link with a vehicle on it (raised on a worker thread); the scan pauses until <see cref="Resume"/>.</summary>
    public event Action<LinkSpec, string>? Found;

    /// <summary>What the scan is doing, for the status line (worker thread).</summary>
    public event Action<string>? StatusChanged;

    public bool Enabled => _loop != null;

    /// <summary>Replaceable for tests: lists serial ports.</summary>
    public Func<IReadOnlyList<string>> ListPorts { get; set; } = LinkFactory.GetSerialPortNames;

    /// <summary>Replaceable for tests: true if a heartbeat from a vehicle arrives on the port within the time.</summary>
    public Func<string, int, TimeSpan, CancellationToken, bool> ProbeSerial { get; set; } = SerialProbe.HasVehicleHeartbeat;

    private volatile bool _paused;

    public void Start()
    {
        if (_loop != null)
            return;
        _loop = new CancellationTokenSource();
        var token = _loop.Token;
        _ = Task.Run(() => RunAsync(token));
    }

    public void Stop()
    {
        _loop?.Cancel();
        _loop = null;
        CloseUdp();
        StatusChanged?.Invoke("");
    }

    /// <summary>Continue scanning after a reported link failed to connect or later dropped.</summary>
    public void Resume(string? failedPort = null)
    {
        if (failedPort != null)
            _retryAt[failedPort] = DateTime.UtcNow + SilentRetry;
        _paused = false;
    }

    private async Task RunAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (_paused || !_canConnect())
                {
                    CloseUdp();
                    await Task.Delay(ScanEvery, stop).ConfigureAwait(false);
                    continue;
                }

                StatusChanged?.Invoke("Searching for a vehicle…");

                if (UdpHasVehicle())
                {
                    Report(LinkSpec.UdpListen(UdpPort), "UDP");
                    continue;
                }

                foreach (var (port, bauds) in Candidates(ListPorts()))
                {
                    if (stop.IsCancellationRequested || _paused || !_canConnect())
                        break;
                    if (_retryAt.TryGetValue(port, out var at) && DateTime.UtcNow < at)
                        continue;

                    StatusChanged?.Invoke("Checking " + Path.GetFileName(port) + "…");
                    var found = false;
                    foreach (var baud in bauds)
                    {
                        if (await Task.Run(() => ProbeSerial(port, baud, ProbeTime, stop), stop).ConfigureAwait(false))
                        {
                            Report(LinkSpec.Serial(port, baud), port);
                            found = true;
                            break;
                        }
                    }

                    if (found)
                        break;
                    _retryAt[port] = DateTime.UtcNow + SilentRetry;
                }

                if (!_paused)
                    StatusChanged?.Invoke("Searching for a vehicle…");
                await Task.Delay(ScanEvery, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn("auto-connect", ex);
                await Task.Delay(ScanEvery, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private void Report(LinkSpec spec, string label)
    {
        _paused = true;
        CloseUdp();
        StatusChanged?.Invoke("Found a vehicle on " + Path.GetFileName(label));
        Found?.Invoke(spec, label);
    }

    /// <summary>
    /// Serial ports worth probing, each physical device once (a /dev/serial/by-id link and the
    /// ttyACM node it points to are the same), ArduPilot's MAVLink interface (-if00) first and its
    /// second interface (-if02, often SLCAN) last; USB-serial adapters (radios) at 57600 then 115200.
    /// </summary>
    public static IReadOnlyList<(string Port, int[] Bauds)> Candidates(IReadOnlyList<string> ports, Func<string, string>? resolve = null)
    {
        resolve ??= RealPath;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<(string, int[], int)>();
        foreach (var port in ports.OrderBy(p => p.Contains("/by-id/", StringComparison.Ordinal) ? 0 : 1))
        {
            if (port.StartsWith("/dev/ttyS", StringComparison.Ordinal))
                continue; // legacy UARTs: nothing hot-plugs there, and probing many is slow
            var real = resolve(port);
            if (!seen.Add(real))
                continue;

            var name = port.ToLowerInvariant();
            var rank = name.EndsWith("-if02", StringComparison.Ordinal) ? 2 : name.Contains("ardupilot") || name.Contains("px4") || name.Contains("cube") ? 0 : 1;
            var radio = real.Contains("ttyUSB", StringComparison.Ordinal) || name.Contains("ftdi") || name.Contains("cp210");
            result.Add((port, radio ? [57600, 115200] : [115200], rank));
        }

        return result.OrderBy(r => r.Item3).Select(r => (r.Item1, r.Item2)).ToList();
    }

    private static string RealPath(string port)
    {
        try
        {
            return File.ResolveLinkTarget(port, returnFinalTarget: true)?.FullName ?? port;
        }
        catch (Exception)
        {
            return port;
        }
    }

    private bool UdpHasVehicle()
    {
        try
        {
            if (_udp == null)
            {
                _udp = new UdpClient(AddressFamily.InterNetwork);
                _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udp.Client.Bind(new IPEndPoint(IPAddress.Any, UdpPort));
            }

            var parse = new MAVLink.MavlinkParse();
            while (_udp.Available > 0)
            {
                IPEndPoint? from = null;
                var data = _udp.Receive(ref from);
                if (SerialProbe.ContainsVehicleHeartbeat(parse, data))
                    return true;
            }
        }
        catch (SocketException)
        {
            // the port is taken (another GCS); serial still works
            CloseUdp();
        }

        return false;
    }

    private void CloseUdp()
    {
        try
        {
            _udp?.Dispose();
        }
        catch (Exception)
        {
        }

        _udp = null;
    }

    public void Dispose() => Stop();
}

/// <summary>Listens on a serial port for a vehicle heartbeat without writing anything.</summary>
public static class SerialProbe
{
    public static bool HasVehicleHeartbeat(string port, int baud, TimeSpan time, CancellationToken cancel)
    {
        try
        {
            using var sp = new System.IO.Ports.SerialPort(port, baud) { ReadTimeout = 200 };
            sp.Open();
            var parse = new MAVLink.MavlinkParse();
            var buffer = new byte[4096];
            var stream = new MemoryStream();
            var end = DateTime.UtcNow + time;
            while (DateTime.UtcNow < end && !cancel.IsCancellationRequested)
            {
                int n;
                try
                {
                    n = sp.Read(buffer, 0, buffer.Length);
                }
                catch (TimeoutException)
                {
                    continue;
                }

                stream.Write(buffer, 0, n);
                if (stream.Length > 64 && ContainsVehicleHeartbeat(parse, stream.ToArray()))
                    return true;
                if (stream.Length > 64 * 1024)
                    stream.SetLength(0);
            }
        }
        catch (Exception)
        {
            // busy, gone, or not a serial device
        }

        return false;
    }

    /// <summary>True if the bytes hold a HEARTBEAT from something that is not a ground station.</summary>
    public static bool ContainsVehicleHeartbeat(MAVLink.MavlinkParse parse, byte[] data)
    {
        // scan for packet starts; the parser reads one packet from a stream positioned on one
        for (var i = 0; i < data.Length - 8; i++)
        {
            if (data[i] != MAVLink.MAVLINK_STX && data[i] != MAVLink.MAVLINK_STX_MAVLINK1)
                continue;
            try
            {
                using var ms = new MemoryStream(data, i, data.Length - i, false);
                var msg = parse.ReadPacket(ms);
                if (msg?.msgid == (uint)MAVLink.MAVLINK_MSG_ID.HEARTBEAT && msg.data is MAVLink.mavlink_heartbeat_t hb &&
                    hb.type != (byte)MAVLink.MAV_TYPE.GCS && msg.compid != 0)
                    return true;
            }
            catch (Exception)
            {
                // partial packet at the end of the buffer
            }
        }

        return false;
    }
}
