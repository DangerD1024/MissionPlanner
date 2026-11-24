using System.Globalization;
using System.Net;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using MissionPlanner.App.Core.Session;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Comms;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.Tools;

/// <summary>
/// A TCP port bridged to a byte link, as Mission Planner's "MAVSerial pass": one client at a time
/// (others are turned away while it is connected), the link opened when the first client arrives
/// and kept open until <see cref="Stop"/>.
/// </summary>
public sealed class TcpSerialBridge : IDisposable
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(TcpSerialBridge));

    private readonly IByteLink _link;
    private readonly int _port;
    private readonly IPAddress _bind;
    private TcpListener? _listener;
    private CancellationTokenSource? _stop;
    private Task _accept = Task.CompletedTask;
    private TcpClient? _client;
    private long _toDevice;
    private long _fromDevice;

    public TcpSerialBridge(IByteLink link, int port, IPAddress? bind = null)
    {
        _link = link;
        _port = port;
        _bind = bind ?? IPAddress.Any;
    }

    public IByteLink Link => _link;

    /// <summary>The bound port (the real one when 0 asked for any free port).</summary>
    public int LocalPort => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? _port;

    public bool IsRunning => _listener != null;

    public bool ClientConnected => _client is { Connected: true };

    public string? ClientName { get; private set; }

    public long BytesToDevice => Interlocked.Read(ref _toDevice);

    public long BytesFromDevice => Interlocked.Read(ref _fromDevice);

    public int Clients { get; private set; }

    /// <summary>Starts listening; throws when the port is taken.</summary>
    public void Start()
    {
        if (_listener != null)
            return;
        var listener = new TcpListener(_bind, _port);
        listener.Start();
        _listener = listener;
        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        _accept = Task.Run(() => AcceptLoop(listener, token));
    }

    public void Stop()
    {
        var listener = Interlocked.Exchange(ref _listener, null);
        if (listener == null)
            return;
        _stop?.Cancel();
        try
        {
            listener.Stop();
        }
        catch
        {
            // already stopped
        }

        try
        {
            _client?.Close();
        }
        catch
        {
            // already closed
        }

        try
        {
            _accept.Wait(2000);
        }
        catch
        {
            // ended by cancellation
        }

        try
        {
            _link.Close();
        }
        catch (Exception ex)
        {
            Log.Debug("bridge link close", ex);
        }

        _stop?.Dispose();
        _stop = null;
    }

    public void Dispose() => Stop();

    private async Task AcceptLoop(TcpListener listener, CancellationToken stop)
    {
        var sessions = new List<Task>();
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stop).ConfigureAwait(false);
            }
            catch (Exception) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Debug("bridge accept", ex);
                break;
            }

            if (ClientConnected)
            {
                // exclusive: the port already has its user
                client.Dispose();
                continue;
            }

            _client = client;
            Clients++;
            ClientName = client.Client.RemoteEndPoint?.ToString();
            sessions.Add(Task.Factory.StartNew(() => Pump(client, stop), stop, TaskCreationOptions.LongRunning, TaskScheduler.Default));
            sessions.RemoveAll(t => t.IsCompleted);
        }

        try
        {
            await Task.WhenAll(sessions).WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // a session that does not end in time is abandoned
        }
    }

    private void Pump(TcpClient client, CancellationToken stop)
    {
        try
        {
            using var _ = client;
            client.NoDelay = true;
            var stream = client.GetStream();
            if (!_link.IsOpen)
                _link.Open();

            var buffer = new byte[4096];
            var fromDevice = new byte[4096];
            while (!stop.IsCancellationRequested && client.Connected && _link.IsOpen)
            {
                var idle = true;
                if (client.Available > 0)
                {
                    var n = stream.Read(buffer, 0, Math.Min(buffer.Length, client.Available));
                    if (n > 0)
                    {
                        _link.Write(buffer, 0, n);
                        Interlocked.Add(ref _toDevice, n);
                        idle = false;
                    }
                }
                else if (client.Client.Poll(0, SelectMode.SelectRead) && client.Available == 0)
                {
                    // readable with nothing to read: the other side closed
                    break;
                }

                var m = _link.Read(fromDevice, 0, fromDevice.Length, idle ? 1 : 0);
                if (m > 0)
                {
                    stream.Write(fromDevice, 0, m);
                    Interlocked.Add(ref _fromDevice, m);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug("bridge session", ex);
        }
        finally
        {
            if (ReferenceEquals(_client, client))
            {
                _client = null;
                ClientName = null;
            }
        }
    }
}

/// <summary>A port of the autopilot reachable through SERIAL_CONTROL.</summary>
public sealed record SerialDeviceChoice(MAVLink.SERIAL_CONTROL_DEV Device, string Text)
{
    public override string ToString() => Text;
}

/// <summary>
/// TOOLS > MAVSerial pass (Mission Planner's Ctrl+F > MAVSerial pass): a TCP port (500) bridged to
/// the vehicle's GPS port through SERIAL_CONTROL, so u-center or another GPS tool can talk to the
/// GPS through the autopilot. The autopilot gives up the port while the bridge runs.
/// </summary>
public sealed partial class MavSerialPassViewModel : ToolPageViewModel
{
    private const string PortKey = "next_mavserialpass_port";
    private const string DeviceKey = "next_mavserialpass_device";
    private const string BaudKey = "next_mavserialpass_baud";

    private TcpSerialBridge? _bridge;

    public MavSerialPassViewModel(AppServices services)
        : base("MAVSerial pass",
            "Create an exclusive passthrough to the vehicle's GPS: connect a GPS tool (such as u-center) to TCP port 500 on this computer. The autopilot stops using the GPS until you stop the passthrough.",
            services)
    {
        var port = Settings.Instance.GetInt32(PortKey, 500);
        _tcpPort = port is > 0 and <= 65535 ? port : 500;
        var device = Settings.Instance.GetInt32(DeviceKey, (int)MAVLink.SERIAL_CONTROL_DEV.GPS1);
        _device = Devices.FirstOrDefault(d => (int)d.Device == device) ?? Devices[2];
        _baud = Settings.Instance.GetInt32(BaudKey, 0);
        services.Session.StateChanged += (_, state) =>
        {
            if (state != LinkState.Connected)
                services.Dispatcher.Post(() =>
                {
                    if (_bridge != null)
                    {
                        Stop();
                        Report("The vehicle disconnected; the passthrough stopped.", true);
                    }
                });
        };
    }

    public static IReadOnlyList<SerialDeviceChoice> Devices { get; } =
    [
        new(MAVLink.SERIAL_CONTROL_DEV.TELEM1, "TELEM1"),
        new(MAVLink.SERIAL_CONTROL_DEV.TELEM2, "TELEM2"),
        new(MAVLink.SERIAL_CONTROL_DEV.GPS1, "GPS1"),
        new(MAVLink.SERIAL_CONTROL_DEV.GPS2, "GPS2"),
        .. Enumerable.Range(0, 10).Select(i => new SerialDeviceChoice((MAVLink.SERIAL_CONTROL_DEV)((int)MAVLink.SERIAL_CONTROL_DEV.SERIAL_CONTROL_SERIAL0 + i), "SERIAL" + i)),
    ];

    /// <summary>0 leaves the port's baud rate as the autopilot set it.</summary>
    public static IReadOnlyList<int> Bauds { get; } = [0, 4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800];

    [ObservableProperty]
    private decimal _tcpPort;

    [ObservableProperty]
    private SerialDeviceChoice? _device;

    [ObservableProperty]
    private int _baud;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartStopText), nameof(CanEditSettings))]
    private bool _isRunning;

    public string StartStopText => IsRunning ? "Stop" : "Start";

    public bool CanEditSettings => !IsRunning;

    [ObservableProperty]
    private string _clientText = "";

    [ObservableProperty]
    private string _trafficText = "";

    partial void OnTcpPortChanged(decimal value) => Settings.Instance[PortKey] = ((int)value).ToString(CultureInfo.InvariantCulture);

    partial void OnDeviceChanged(SerialDeviceChoice? value)
    {
        if (value != null)
            Settings.Instance[DeviceKey] = ((int)value.Device).ToString(CultureInfo.InvariantCulture);
    }

    partial void OnBaudChanged(int value) => Settings.Instance[BaudKey] = value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Starts the bridge to a link (tests pass a fake; the page uses the vehicle's SERIAL_CONTROL).</summary>
    internal bool Start(IByteLink link)
    {
        var bridge = new TcpSerialBridge(link, (int)TcpPort);
        try
        {
            bridge.Start();
        }
        catch (Exception ex)
        {
            Report($"Could not listen on TCP port {TcpPort}: {ex.Message}", true);
            return false;
        }

        _bridge = bridge;
        IsRunning = true;
        UpdateStatus();
        Report($"Listening on TCP port {bridge.LocalPort}: connect your GPS tool there to reach {link.Name}.");
        return true;
    }

    internal TcpSerialBridge? Bridge => _bridge;

    [RelayCommand]
    private void StartStop()
    {
        if (IsRunning)
        {
            Stop();
            Report("Passthrough stopped; the autopilot has the port back.");
            return;
        }

        if (!IsConnected)
        {
            Report("Connect to a vehicle first.", true);
            return;
        }

        if (Device is not { } dev)
            return;
        Start(new SerialControlLink(new MavlinkSerialControlTransport(Services.Session), dev.Device, (uint)Baud));
    }

    private void Stop()
    {
        var bridge = Interlocked.Exchange(ref _bridge, null);
        bridge?.Stop();
        IsRunning = false;
        ClientText = "";
        TrafficText = "";
    }

    public override void Tick()
    {
        base.Tick();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_bridge is not { } b)
            return;
        ClientText = b.ClientConnected ? L.Format("Connected: {0}", b.ClientName ?? "") : L.Tr("Waiting for a connection");
        TrafficText = L.Format("To the device {0:N0} bytes, from the device {1:N0} bytes", b.BytesToDevice, b.BytesFromDevice);
    }
}
