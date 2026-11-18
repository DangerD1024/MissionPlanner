using MissionPlanner.App.Core.Session;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Comms;

/// <summary>The SERIAL_CONTROL traffic of one vehicle; faked in tests.</summary>
public interface ISerialControlTransport
{
    void Send(MAVLink.mavlink_serial_control_t packet);

    /// <summary>Calls <paramref name="handler"/> (on a worker thread) for each SERIAL_CONTROL the vehicle sends, until disposed.</summary>
    IDisposable Subscribe(Action<MAVLink.mavlink_serial_control_t> handler);
}

/// <summary>SERIAL_CONTROL through the session's MAVLink connection, to and from the selected vehicle.</summary>
public sealed class MavlinkSerialControlTransport : ISerialControlTransport
{
    private readonly MAVLinkInterface _mav;
    private readonly uint _sysid;
    private readonly byte _compid;

    public MavlinkSerialControlTransport(VehicleSession session)
    {
        _mav = session.Mav;
        _sysid = session.Vehicle.sysid;
        _compid = session.Vehicle.compid;
    }

    public void Send(MAVLink.mavlink_serial_control_t packet) => _mav.sendPacket(packet, _sysid, _compid);

    public IDisposable Subscribe(Action<MAVLink.mavlink_serial_control_t> handler)
    {
        var id = _mav.SubscribeToPacketType(MAVLink.MAVLINK_MSG_ID.SERIAL_CONTROL, m =>
        {
            handler(m.ToStructure<MAVLink.mavlink_serial_control_t>());
            return true;
        }, _sysid, _compid);
        return new Unsubscriber(() => _mav.UnSubscribeToPacketType(id));
    }

    private sealed class Unsubscriber(Action action) : IDisposable
    {
        private Action? _action = action;

        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}

/// <summary>
/// A port on the vehicle (the system shell, or SERIALn) reached with SERIAL_CONTROL, as Mission
/// Planner's MAVLinkSerialPort does: writes go out in 70-byte packets, and an empty request polls
/// for output — 20 times a second for half a second after any traffic, 5 times a second otherwise.
/// Taking the port is exclusive; <see cref="Close"/> hands it back to the autopilot.
/// </summary>
public sealed class SerialControlLink : IByteLink
{
    private const int ChunkSize = 70;

    private readonly ISerialControlTransport _transport;
    private readonly ByteQueue _input = new();
    private IDisposable? _subscription;
    private CancellationTokenSource? _stop;
    private Task _poller = Task.CompletedTask;
    private long _lastTraffic;
    private long _lastPoll;
    private volatile bool _pollNow;

    public SerialControlLink(ISerialControlTransport transport, MAVLink.SERIAL_CONTROL_DEV device, uint baudRate = 0)
    {
        _transport = transport;
        Device = device;
        BaudRate = baudRate;
        Name = DeviceName(device);
    }

    public MAVLink.SERIAL_CONTROL_DEV Device { get; }

    /// <summary>Baud rate to set on a SERIALn/TELEM port when opening; 0 leaves it unchanged (the shell has none).</summary>
    public uint BaudRate { get; }

    public string Name { get; }

    public bool IsOpen { get; private set; }

    /// <summary>Poll interval right after traffic.</summary>
    public int FastPollMs { get; init; } = 50;

    /// <summary>Poll interval when the port is quiet.</summary>
    public int IdlePollMs { get; init; } = 200;

    /// <summary>Packets with data received so far (diagnostics, tests).</summary>
    public int RepliesWithData { get; private set; }

    public static string DeviceName(MAVLink.SERIAL_CONTROL_DEV device) => device switch
    {
        MAVLink.SERIAL_CONTROL_DEV.SHELL => "System shell",
        MAVLink.SERIAL_CONTROL_DEV.TELEM1 => "TELEM1",
        MAVLink.SERIAL_CONTROL_DEV.TELEM2 => "TELEM2",
        MAVLink.SERIAL_CONTROL_DEV.GPS1 => "GPS1",
        MAVLink.SERIAL_CONTROL_DEV.GPS2 => "GPS2",
        >= MAVLink.SERIAL_CONTROL_DEV.SERIAL_CONTROL_SERIAL0 and <= MAVLink.SERIAL_CONTROL_DEV.SERIAL_CONTROL_SERIAL9 =>
            "SERIAL" + (device - MAVLink.SERIAL_CONTROL_DEV.SERIAL_CONTROL_SERIAL0),
        _ => device.ToString(),
    };

    public void Open()
    {
        if (IsOpen)
            return;

        _subscription = _transport.Subscribe(OnPacket);
        IsOpen = true;
        Interlocked.Exchange(ref _lastTraffic, Environment.TickCount64);
        _transport.Send(Request(Array.Empty<byte>(), 0, 0, BaudRate));

        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        _poller = Task.Run(() => PollLoop(token));
    }

    public void Close()
    {
        if (!IsOpen)
            return;

        IsOpen = false;
        _stop?.Cancel();
        try
        {
            _poller.Wait(500);
        }
        catch
        {
            // the poller only ever ends by cancellation
        }

        _stop?.Dispose();
        _stop = null;

        try
        {
            // without EXCLUSIVE the autopilot takes the port back
            _transport.Send(new MAVLink.mavlink_serial_control_t
            {
                device = (byte)Device,
                flags = 0,
                timeout = 0,
                baudrate = 0,
                count = 0,
                data = new byte[ChunkSize],
            });
        }
        catch
        {
            // link already down
        }

        _subscription?.Dispose();
        _subscription = null;
        _input.Release();
    }

    public void Write(byte[] data, int offset, int count)
    {
        if (!IsOpen)
            throw new InvalidOperationException("The port is not open.");

        for (var sent = 0; sent < count; sent += ChunkSize)
        {
            var n = Math.Min(ChunkSize, count - sent);
            if (sent > 0)
                Thread.Sleep(10); // as Mission Planner: do not flood the vehicle's port
            _transport.Send(Request(data, offset + sent, n, 0));
        }

        Interlocked.Exchange(ref _lastTraffic, Environment.TickCount64);
        _pollNow = true;
    }

    public int Read(byte[] buffer, int offset, int count, int timeoutMs) =>
        _input.Take(buffer, offset, count, IsOpen ? timeoutMs : 0);

    public void DiscardInput() => _input.Clear();

    public void Dispose() => Close();

    private MAVLink.mavlink_serial_control_t Request(byte[] data, int offset, int count, uint baud)
    {
        var payload = new byte[ChunkSize];
        Array.Copy(data, offset, payload, 0, count);
        return new MAVLink.mavlink_serial_control_t
        {
            device = (byte)Device,
            flags = (byte)(MAVLink.SERIAL_CONTROL_FLAG.EXCLUSIVE | MAVLink.SERIAL_CONTROL_FLAG.RESPOND |
                           MAVLink.SERIAL_CONTROL_FLAG.MULTI),
            // the autopilot answers at once with what it has; waiting there would stall its main loop
            timeout = 0,
            baudrate = baud,
            count = (byte)count,
            data = payload,
        };
    }

    private void OnPacket(MAVLink.mavlink_serial_control_t packet)
    {
        if (!IsOpen || packet.device != (byte)Device || packet.count == 0 || packet.data == null)
            return;

        var n = Math.Min(packet.count, packet.data.Length);
        RepliesWithData++;
        _input.Append(packet.data.AsSpan(0, n));
        Interlocked.Exchange(ref _lastTraffic, Environment.TickCount64);
        // more may be waiting on the vehicle side
        _pollNow = true;
    }

    private async Task PollLoop(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var now = Environment.TickCount64;
                var busy = now - Interlocked.Read(ref _lastTraffic) < 500;
                var interval = busy ? FastPollMs : IdlePollMs;
                if (_pollNow || now - _lastPoll >= interval)
                {
                    _pollNow = false;
                    _lastPoll = now;
                    _transport.Send(Request(Array.Empty<byte>(), 0, 0, 0));
                }

                await Task.Delay(5, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // a failed send (link dropping) must not kill polling; the page notices the link state
                await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}
