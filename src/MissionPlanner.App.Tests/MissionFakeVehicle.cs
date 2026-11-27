using System.Collections.Concurrent;
using System.Text;

namespace MissionPlanner.App.Tests;

/// <summary>
/// A scripted ArduCopter that answers what a connect and a mission read ask: HEARTBEAT at 10 Hz,
/// a SYS_STATUS (optionally with an unhealthy RC receiver), a short parameter list
/// (PARAM_REQUEST_LIST) and a mission (MISSION_REQUEST_LIST / MISSION_REQUEST(_INT), answered to
/// whichever system ID asked). Records every message the ground station sends.
/// </summary>
internal sealed class MissionFakeVehicle : IAsyncDisposable
{
    public const byte SysId = 1;
    public const byte CompId = 1;

    /// <summary>Home, then a takeoff and a waypoint: the mission the vehicle holds.</summary>
    public static readonly (MAVLink.MAV_CMD Command, double Lat, double Lng, float Alt)[] Items =
    [
        (MAVLink.MAV_CMD.WAYPOINT, -35.363261, 149.165230, 584),
        (MAVLink.MAV_CMD.TAKEOFF, 0, 0, 20),
        (MAVLink.MAV_CMD.WAYPOINT, -35.3640, 149.1660, 30),
    ];

    private static readonly (string Name, float Value)[] Params = [("FRAME_CLASS", 1), ("SYSID_THISMAV", 1), ("WPNAV_SPEED", 1000)];

    private readonly Stream _stream;
    private readonly object _writeLock = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly MAVLink.MavlinkParse _parse = new();
    private readonly Task _sender;
    private readonly Task _receiver;
    private int _seq;

    public MissionFakeVehicle(Stream stream)
    {
        _stream = stream;
        _sender = Task.Run(SendLoop);
        _receiver = Task.Factory.StartNew(ReceiveLoop, TaskCreationOptions.LongRunning);
    }

    /// <summary>SYS_STATUS reports an RC receiver that is present and enabled but not healthy.</summary>
    public volatile bool RcReceiverUnhealthy;

    public ConcurrentQueue<MAVLink.MAVLinkMessage> Received { get; } = new();

    public bool Got(MAVLink.MAVLINK_MSG_ID id) => Received.Any(m => m.msgid == (uint)id);

    private async Task SendLoop()
    {
        var tick = 0;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                Write(MAVLink.MAVLINK_MSG_ID.HEARTBEAT, new MAVLink.mavlink_heartbeat_t(
                    custom_mode: 0,
                    type: (byte)MAVLink.MAV_TYPE.QUADROTOR,
                    autopilot: (byte)MAVLink.MAV_AUTOPILOT.ARDUPILOTMEGA,
                    base_mode: (byte)MAVLink.MAV_MODE_FLAG.CUSTOM_MODE_ENABLED,
                    system_status: (byte)MAVLink.MAV_STATE.STANDBY,
                    mavlink_version: 3));
                if (tick++ % 5 == 0)
                {
                    const uint rc = (uint)MAVLink.MAV_SYS_STATUS_SENSOR.RC_RECEIVER;
                    var bad = RcReceiverUnhealthy;
                    Write(MAVLink.MAVLINK_MSG_ID.SYS_STATUS, new MAVLink.mavlink_sys_status_t
                    {
                        onboard_control_sensors_present = bad ? rc : 0,
                        onboard_control_sensors_enabled = bad ? rc : 0,
                        onboard_control_sensors_health = 0,
                        voltage_battery = 12600,
                        battery_remaining = 80,
                    });
                }

                await Task.Delay(100, _stop.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // stopped or the link closed
        }
    }

    private void Write(MAVLink.MAVLINK_MSG_ID id, object msg)
    {
        lock (_writeLock)
        {
            var bytes = _parse.GenerateMAVLinkPacket20(id, msg, false, SysId, CompId, _seq++ & 0xff);
            _stream.Write(bytes, 0, bytes.Length);
            _stream.Flush();
        }
    }

    private void SendParam(int index)
    {
        var (name, value) = Params[index];
        var id = new byte[16];
        Encoding.ASCII.GetBytes(name, 0, name.Length, id, 0);
        Write(MAVLink.MAVLINK_MSG_ID.PARAM_VALUE, new MAVLink.mavlink_param_value_t(
            value, (ushort)Params.Length, (ushort)index, id, (byte)MAVLink.MAV_PARAM_TYPE.REAL32));
    }

    private void SendItem(MAVLink.MAVLinkMessage request, ushort seq, byte missionType)
    {
        if (missionType != (byte)MAVLink.MAV_MISSION_TYPE.MISSION || seq >= Items.Length)
            return;
        var (command, lat, lng, alt) = Items[seq];
        Write(MAVLink.MAVLINK_MSG_ID.MISSION_ITEM_INT, new MAVLink.mavlink_mission_item_int_t
        {
            target_system = (byte)request.sysid,
            target_component = (byte)request.compid,
            seq = seq,
            frame = (byte)(seq == 0 ? MAVLink.MAV_FRAME.GLOBAL : MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT),
            command = (ushort)command,
            current = 0,
            autocontinue = 1,
            x = (int)(lat * 1e7),
            y = (int)(lng * 1e7),
            z = alt,
            mission_type = missionType,
        });
    }

    private void ReceiveLoop()
    {
        var parse = new MAVLink.MavlinkParse();
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var msg = parse.ReadPacket(_stream);
                if (msg == null || msg.Length == 0)
                    continue;
                Received.Enqueue(msg);
                switch ((MAVLink.MAVLINK_MSG_ID)msg.msgid)
                {
                    case MAVLink.MAVLINK_MSG_ID.PARAM_REQUEST_LIST:
                        for (var i = 0; i < Params.Length; i++)
                            SendParam(i);
                        break;
                    case MAVLink.MAVLINK_MSG_ID.MISSION_REQUEST_LIST:
                        var list = (MAVLink.mavlink_mission_request_list_t)msg.data;
                        Write(MAVLink.MAVLINK_MSG_ID.MISSION_COUNT, new MAVLink.mavlink_mission_count_t
                        {
                            target_system = (byte)msg.sysid,
                            target_component = (byte)msg.compid,
                            count = (ushort)(list.mission_type == (byte)MAVLink.MAV_MISSION_TYPE.MISSION ? Items.Length : 0),
                            mission_type = list.mission_type,
                        });
                        break;
                    case MAVLink.MAVLINK_MSG_ID.MISSION_REQUEST_INT:
                        var reqInt = (MAVLink.mavlink_mission_request_int_t)msg.data;
                        SendItem(msg, reqInt.seq, reqInt.mission_type);
                        break;
                    case MAVLink.MAVLINK_MSG_ID.MISSION_REQUEST:
                        var req = (MAVLink.mavlink_mission_request_t)msg.data;
                        SendItem(msg, req.seq, req.mission_type);
                        break;
                }
            }
            catch (TimeoutException)
            {
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _sender.ConfigureAwait(false);
        _stream.Dispose();
        await Task.WhenAny(_receiver, Task.Delay(2000)).ConfigureAwait(false);
        _stop.Dispose();
    }
}
