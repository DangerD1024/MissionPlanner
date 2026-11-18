using log4net;
using MissionPlanner.App.Core.Session;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Comms;

/// <summary>What the CAN forwarder needs from the vehicle link; faked in tests.</summary>
public interface ICanFrameTransport
{
    void Send(object mavlinkMessage);

    /// <summary>Calls the handler for each CAN_FRAME / CANFD_FRAME and COMMAND_ACK from the vehicle until disposed.</summary>
    IDisposable Subscribe(Action<MAVLink.MAVLinkMessage> handler);

    /// <summary>MAV_CMD_CAN_FORWARD; fire and forget (the reply comes as a COMMAND_ACK).</summary>
    void RequestForwarding(int bus);
}

/// <summary>CAN traffic through the session's MAVLink connection to the selected vehicle.</summary>
public sealed class MavlinkCanFrameTransport : ICanFrameTransport
{
    private readonly MAVLinkInterface _mav;
    private readonly uint _sysid;
    private readonly byte _compid;

    public MavlinkCanFrameTransport(VehicleSession session)
    {
        _mav = session.Mav;
        _sysid = session.Vehicle.sysid;
        _compid = session.Vehicle.compid;
    }

    public byte TargetSystem => (byte)_sysid;

    public byte TargetComponent => _compid;

    public void Send(object mavlinkMessage) => _mav.sendPacket(mavlinkMessage, _sysid, _compid);

    public IDisposable Subscribe(Action<MAVLink.MAVLinkMessage> handler)
    {
        bool Handle(MAVLink.MAVLinkMessage m)
        {
            handler(m);
            return true;
        }

        var ids = new[]
        {
            _mav.SubscribeToPacketType(MAVLink.MAVLINK_MSG_ID.CAN_FRAME, Handle, _sysid, _compid),
            _mav.SubscribeToPacketType(MAVLink.MAVLINK_MSG_ID.CANFD_FRAME, Handle, _sysid, _compid),
            _mav.SubscribeToPacketType(MAVLink.MAVLINK_MSG_ID.COMMAND_ACK, Handle, _sysid, _compid),
        };
        return new Unsubscriber(() =>
        {
            foreach (var id in ids)
                _mav.UnSubscribeToPacketType(id);
        });
    }

    public void RequestForwarding(int bus) =>
        _mav.sendPacket(new MAVLink.mavlink_command_long_t
        {
            target_system = (byte)_sysid,
            target_component = _compid,
            command = (ushort)MAVLink.MAV_CMD.CAN_FORWARD,
            param1 = bus,
        }, _sysid, _compid);

    private sealed class Unsubscriber(Action action) : IDisposable
    {
        private Action? _action = action;

        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}

/// <summary>
/// Mission Planner's "MAVLink-CAN1/2" mode: asks the autopilot to forward one CAN bus over MAVLink
/// (MAV_CMD_CAN_FORWARD, repeated every second because the autopilot stops after a few seconds
/// without it), turns the CAN_FRAME / CANFD_FRAME messages it sends into SLCAN for the DroneCAN
/// library, and sends the library's frames back as CAN_FRAME on the same bus.
/// </summary>
public sealed class MavlinkCanForwarder : IDisposable
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(MavlinkCanForwarder));

    private readonly ICanFrameTransport _transport;
    private readonly byte _targetSystem;
    private readonly byte _targetComponent;
    private IDisposable? _subscription;
    private CancellationTokenSource? _stop;
    private Task _keepAlive = Task.CompletedTask;

    public MavlinkCanForwarder(ICanFrameTransport transport, int bus, byte targetSystem, byte targetComponent)
    {
        if (bus is < 1 or > 2)
            throw new ArgumentOutOfRangeException(nameof(bus), bus, "CAN bus 1 or 2");
        _transport = transport;
        Bus = bus;
        _targetSystem = targetSystem;
        _targetComponent = targetComponent;
        Stream.FrameSent += SendFrame;
    }

    /// <summary>1 or 2, as CAN_P1 / CAN_P2.</summary>
    public int Bus { get; }

    /// <summary>The SLCAN side, for <c>DroneCAN.StartSLCAN</c>.</summary>
    public SlcanBridgeStream Stream { get; } = new();

    /// <summary>The autopilot's answer to CAN_FORWARD, once it came (MAV_RESULT).</summary>
    public MAVLink.MAV_RESULT? ForwardResult { get; private set; }

    public long FramesFromVehicle { get; private set; }

    public long FramesToVehicle { get; private set; }

    public int KeepAliveMs { get; init; } = 1000;

    public void Start()
    {
        _subscription = _transport.Subscribe(OnMessage);
        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        _keepAlive = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    _transport.RequestForwarding(Bus);
                }
                catch (Exception ex)
                {
                    Log.Debug("CAN_FORWARD", ex);
                }

                try
                {
                    await Task.Delay(KeepAliveMs, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });
    }

    public void Stop()
    {
        if (_stop == null)
            return;
        _stop.Cancel();
        try
        {
            _keepAlive.Wait(500);
        }
        catch
        {
            // cancelled
        }

        _stop.Dispose();
        _stop = null;
        _subscription?.Dispose();
        _subscription = null;
        try
        {
            // bus 0: stop forwarding now rather than at the autopilot's timeout
            _transport.RequestForwarding(0);
        }
        catch
        {
            // link gone
        }
    }

    public void Dispose()
    {
        Stop();
        Stream.FrameSent -= SendFrame;
        Stream.Dispose();
    }

    /// <summary>An incoming MAVLink message (exposed for tests).</summary>
    internal void OnMessage(MAVLink.MAVLinkMessage message)
    {
        switch (message.data)
        {
            case MAVLink.mavlink_can_frame_t f when f.bus == Bus - 1:
                FramesFromVehicle++;
                Stream.Receive(ToFrame(f.id, f.data, Math.Min(f.len, (byte)8), fd: false));
                break;
            case MAVLink.mavlink_canfd_frame_t f when f.bus == Bus - 1:
                FramesFromVehicle++;
                // len is the DLC on the autopilot side; accept a byte count too
                var length = f.len <= 15 ? Slcan.DlcToLength(f.len) : Math.Min(f.len, (byte)64);
                Stream.Receive(ToFrame(f.id, f.data, length, fd: true));
                break;
            case MAVLink.mavlink_command_ack_t ack when ack.command == (ushort)MAVLink.MAV_CMD.CAN_FORWARD:
                ForwardResult = (MAVLink.MAV_RESULT)ack.result;
                break;
        }
    }

    private static CanFrame ToFrame(uint id, byte[]? data, int length, bool fd)
    {
        var bytes = new byte[length];
        if (data != null)
            Array.Copy(data, bytes, Math.Min(length, data.Length));
        // bit 31 flags an extended id, as in the autopilot's CANFrame
        var extended = (id & 0x80000000) != 0 || (id & CanFrame.ExtendedIdMask) > 0x7FF;
        return new CanFrame(id & CanFrame.ExtendedIdMask, bytes, extended, fd);
    }

    private void SendFrame(CanFrame frame)
    {
        var id = frame.Id | (frame.Extended ? 0x80000000 : 0);
        if (frame.Fd || frame.Data.Length > 8)
        {
            var data = new byte[64];
            Array.Copy(frame.Data, data, Math.Min(64, frame.Data.Length));
            _transport.Send(new MAVLink.mavlink_canfd_frame_t(id, _targetSystem, _targetComponent, (byte)(Bus - 1),
                (byte)Slcan.LengthToDlc(frame.Data.Length), data));
        }
        else
        {
            var data = new byte[8];
            Array.Copy(frame.Data, data, frame.Data.Length);
            _transport.Send(new MAVLink.mavlink_can_frame_t(id, _targetSystem, _targetComponent, (byte)(Bus - 1),
                (byte)frame.Data.Length, data));
        }

        FramesToVehicle++;
    }
}
