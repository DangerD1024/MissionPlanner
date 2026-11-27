using System.Net;
using System.Net.Sockets;
using log4net;

namespace MissionPlanner.App.Core.Session;

/// <summary>
/// Finds a vehicle without the user picking a port, like QGroundControl: while nothing is
/// connected it listens on UDP 14550 and probes serial ports for a MAVLink heartbeat (read only,
/// nothing is written), then reports the first link that has one. Ports that stay silent are
/// probed again after a pause, so a board plugged in later is found within a few seconds.
/// A link the user disconnected is held (<see cref="HoldOff"/>): it is not connected again by
/// itself until its vehicle has gone away — the USB device unplugged, or a UDP/TCP vehicle silent
/// for <see cref="GoneAfter"/> — or the user connects to it.
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
    public AutoConnectService(Func<bool> canConnect)
    {
        _canConnect = canConnect;
        ProbeUdp = UdpHasVehicle;
    }

    /// <summary>A link with a vehicle on it (raised on a worker thread); the scan pauses until <see cref="Resume"/>.</summary>
    public event Action<LinkSpec, string>? Found;

    /// <summary>What the scan is doing, for the status line (worker thread).</summary>
    public event Action<string>? StatusChanged;

    public bool Enabled => _loop != null;

    /// <summary>Replaceable for tests: lists serial ports.</summary>
    public Func<IReadOnlyList<string>> ListPorts { get; set; } = LinkFactory.GetSerialPortNames;

    /// <summary>Replaceable for tests: true if a heartbeat from a vehicle arrives on the port within the time.</summary>
    public Func<string, int, TimeSpan, CancellationToken, bool> ProbeSerial { get; set; } = SerialProbe.HasVehicleHeartbeat;

    /// <summary>Replaceable for tests: true if a vehicle's heartbeat came in on UDP <see cref="UdpPort"/> since the last call.</summary>
    public Func<bool> ProbeUdp { get; set; }

    /// <summary>Replaceable for tests: true if SITL's TCP port 5760 has a vehicle on it.</summary>
    public Func<TimeSpan, CancellationToken, bool> ProbeTcp { get; set; } =
        (time, cancel) => OperatingSystem.IsWindows()
            // the Windows SITL exits when its first TCP client disconnects: a probe that connects and closes
            // ends the simulator. Look for the listening port instead (no connection); connecting for real
            // then tells whether a vehicle is on it.
            ? IsListening(5760)
            : TcpProbe.HasVehicleHeartbeat("127.0.0.1", 5760, time, cancel);

    /// <summary>True if something on this computer listens on the TCP port (asks the OS; connects to nothing).</summary>
    public static bool IsListening(int port)
    {
        try
        {
            return System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                .Any(e => e.Port == port && (IPAddress.IsLoopback(e.Address) || e.Address.Equals(IPAddress.Any) || e.Address.Equals(IPAddress.IPv6Any)));
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            return false;
        }
    }

    /// <summary>Replaceable for tests: the device a serial port name stands for (by-id links resolved).</summary>
    public Func<string, string> ResolvePort { get; set; } = RealPath;

    /// <summary>
    /// Replaceable for tests: the USB device a serial port belongs to (null when unknown). A flight
    /// controller can have two USB serial ports (MatekH743: "-if00" and "-if02"); a Disconnect holds them all.
    /// </summary>
    public Func<string, string?> PortDevice { get; set; } = UsbDevices.DeviceOf;

    /// <summary>How long a held UDP/TCP vehicle must stay silent to count as gone.</summary>
    public TimeSpan GoneAfter { get; set; } = TimeSpan.FromSeconds(10);

    private sealed class Hold(string key, string label, bool network, string? device = null)
    {
        public string Key { get; } = key;

        /// <summary>The USB device of a held serial port: its other ports are the same vehicle.</summary>
        public string? Device { get; } = device;
        public string Label { get; } = label;
        public bool Network { get; } = network;
        public DateTime LastHeard { get; set; } = DateTime.UtcNow;
    }

    private readonly List<Hold> _holds = new();

    /// <summary>
    /// The user disconnected from, or cancelled connecting to, this link: do not connect to it by
    /// itself until its vehicle has gone (USB unplugged, UDP/TCP silent) or <see cref="Release"/>.
    /// </summary>
    public void HoldOff(LinkSpec spec)
    {
        var key = Key(spec);
        var label = spec.Kind switch
        {
            LinkKind.Serial => Path.GetFileName(key.Substring("serial:".Length)),
            LinkKind.UdpListen => "UDP " + spec.Value,
            _ => "TCP " + spec.Value,
        };
        var device = spec.Kind == LinkKind.Serial ? DeviceOf(spec.Address) : null;
        lock (_holds)
        {
            _holds.RemoveAll(h => h.Key == key || (device != null && h.Device == device));
            _holds.Add(new Hold(key, label, spec.Kind != LinkKind.Serial, device));
        }
    }

    /// <summary>The user connects to this link: it is no longer held.</summary>
    public void Release(LinkSpec spec)
    {
        var key = Key(spec);
        // connecting by hand to any port of a held flight controller releases all of them
        var device = spec.Kind == LinkKind.Serial && HasDeviceHolds() ? DeviceOf(spec.Address) : null;
        lock (_holds)
            _holds.RemoveAll(h => h.Key == key || (device != null && h.Device == device));
    }

    public void ReleaseAll()
    {
        lock (_holds)
            _holds.Clear();
    }

    public bool IsHeld(LinkSpec spec)
    {
        var key = Key(spec);
        lock (_holds)
        {
            if (_holds.Any(h => h.Key == key))
                return true;
        }

        // another USB port of a held flight controller
        if (spec.Kind != LinkKind.Serial || !HasDeviceHolds() || DeviceOf(spec.Address) is not { } device)
            return false;
        lock (_holds)
            return _holds.Any(h => h.Device == device);
    }

    private bool HasDeviceHolds()
    {
        lock (_holds)
            return _holds.Any(h => h.Device != null);
    }

    private string? DeviceOf(string port)
    {
        try
        {
            return PortDevice(port);
        }
        catch (Exception ex)
        {
            Log.Debug("usb device of " + port, ex);
            return null;
        }
    }

    private string Key(LinkSpec spec) => spec.Kind switch
    {
        LinkKind.Serial => "serial:" + ResolvePort(spec.Address),
        LinkKind.UdpListen => "udp:" + spec.Value,
        LinkKind.Tcp => "tcp:" + (spec.Address is "localhost" or "" ? "127.0.0.1" : spec.Address) + ":" + spec.Value,
        _ => spec.ToString(),
    };

    /// <summary>
    /// A vehicle was heard on a network link: true if that link is held (its silence clock restarts),
    /// false if it may be connected.
    /// </summary>
    private bool HeardWhileHeld(LinkSpec spec)
    {
        var key = Key(spec);
        lock (_holds)
        {
            var hold = _holds.FirstOrDefault(h => h.Key == key);
            if (hold == null)
                return false;
            hold.LastHeard = DateTime.UtcNow;
            return true;
        }
    }

    /// <summary>
    /// Ends the holds whose vehicle has gone: a serial device no longer listed (unplugged), a
    /// network link silent for <see cref="GoneAfter"/>. While nothing can be scanned (connected to
    /// something else) the network clocks are restarted instead: silence there was not observed.
    /// </summary>
    private void ExpireHolds(IReadOnlyList<string> ports, bool scanning)
    {
        lock (_holds)
        {
            if (_holds.Count == 0)
                return;
            var present = new HashSet<string>(ports.Select(p => "serial:" + ResolvePort(p)), StringComparer.Ordinal);
            var now = DateTime.UtcNow;
            _holds.RemoveAll(h => h.Network ? scanning && now - h.LastHeard > GoneAfter : !present.Contains(h.Key));
            if (!scanning)
            {
                foreach (var h in _holds.Where(h => h.Network))
                    h.LastHeard = now;
            }
        }
    }

    private string SearchingText()
    {
        lock (_holds)
        {
            if (_holds.Count == 0)
                return "Searching for a vehicle…";
            var parts = _holds.Select(h => h.Network ? h.Label + " until the vehicle stops sending"
                : h.Device != null ? h.Label + " and its other USB ports until it is unplugged"
                : h.Label + " until it is unplugged");
            return "Searching for a vehicle… (skipping " + string.Join(", ", parts) + ")";
        }
    }

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
                    ExpireHolds(ListPorts(), scanning: false);
                    await Task.Delay(ScanEvery, stop).ConfigureAwait(false);
                    continue;
                }

                var ports = ListPorts();
                ExpireHolds(ports, scanning: true);
                StatusChanged?.Invoke(SearchingText());

                var udp = LinkSpec.UdpListen(UdpPort);
                if (ProbeUdp() && !HeardWhileHeld(udp))
                {
                    Report(udp, "UDP");
                    continue;
                }

                // SITL bench: ArduPlane's serial0 TCP server. Read-only probe, and only
                // 5760 -- the spare SITL ports belong to the tracker / test scripts and
                // a probe would steal their single-client TCP slot.
                var tcp = LinkSpec.Tcp("127.0.0.1", 5760);
                if (ProbeTcp(ProbeTime, stop) && !HeardWhileHeld(tcp))
                {
                    Report(tcp, "TCP 5760");
                    continue;
                }

                foreach (var (port, bauds) in Candidates(ports, ResolvePort))
                {
                    if (stop.IsCancellationRequested || _paused || !_canConnect())
                        break;
                    if (_retryAt.TryGetValue(port, out var at) && DateTime.UtcNow < at)
                        continue;
                    if (IsHeld(LinkSpec.Serial(port)))
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
                    StatusChanged?.Invoke(SearchingText());
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

/// <summary>Listens on a TCP port for a vehicle heartbeat without writing anything (SITL).</summary>
public static class TcpProbe
{
    public static bool HasVehicleHeartbeat(string host, int port, TimeSpan time, CancellationToken cancel)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(host, port);
            if (!connect.Wait(TimeSpan.FromMilliseconds(500)) || !client.Connected)
                return false;
            using var stream = client.GetStream();
            stream.ReadTimeout = 200;
            var parse = new MAVLink.MavlinkParse();
            var buffer = new byte[4096];
            var accum = new MemoryStream();
            var end = DateTime.UtcNow + time;
            while (DateTime.UtcNow < end && !cancel.IsCancellationRequested)
            {
                int n;
                try
                {
                    n = stream.Read(buffer, 0, buffer.Length);
                }
                catch (IOException)
                {
                    continue;
                }
                if (n == 0)
                    return false;
                accum.Write(buffer, 0, n);
                if (accum.Length > 64 && SerialProbe.ContainsVehicleHeartbeat(parse, accum.ToArray()))
                    return true;
                if (accum.Length > 64 * 1024)
                    accum.SetLength(0);
            }
        }
        catch (Exception)
        {
            // nothing listening there
        }

        return false;
    }
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
