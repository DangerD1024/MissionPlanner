using System.Collections.Concurrent;
using System.Text;

namespace MissionPlanner.App.Tests.FlightData;

/// <summary>
/// A scripted ArduCopter for the extra Flight Data tabs: heartbeat, SERVO_OUTPUT_RAW (output 9
/// follows DO_SET_SERVO), two ADS-B contacts, a uAvionix transponder status, and a COMMAND_ACK
/// (accepted) for every COMMAND_LONG. Records what the ground station sends.
/// </summary>
internal sealed class ExtrasFakeVehicle : IAsyncDisposable
{
    public const byte SysId = 1;
    public const byte CompId = 1;

    private readonly Stream _stream;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _sender;
    private readonly Task _receiver;
    private readonly MAVLink.MavlinkParse _parse = new();
    private readonly object _writeLock = new();
    private int _seq;
    private volatile ushort _servo9 = 1500;

    public ExtrasFakeVehicle(Stream stream)
    {
        _stream = stream;
        _sender = Task.Run(SendLoop);
        _receiver = Task.Factory.StartNew(ReceiveLoop, TaskCreationOptions.LongRunning);
    }

    public ConcurrentQueue<MAVLink.MAVLinkMessage> Received { get; } = new();

    /// <summary>Sends UAVIONIX_ADSB_OUT_STATUS once a second when set.</summary>
    public volatile bool TransponderPresent = true;

    public IEnumerable<MAVLink.mavlink_command_long_t> Commands =>
        Received.Where(m => m.msgid == (uint)MAVLink.MAVLINK_MSG_ID.COMMAND_LONG).Select(m => (MAVLink.mavlink_command_long_t)m.data);

    private async Task SendLoop()
    {
        var tick = 0;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                Write(MAVLink.MAVLINK_MSG_ID.HEARTBEAT, new MAVLink.mavlink_heartbeat_t(
                    custom_mode: 0, type: (byte)MAVLink.MAV_TYPE.QUADROTOR, autopilot: (byte)MAVLink.MAV_AUTOPILOT.ARDUPILOTMEGA,
                    base_mode: (byte)MAVLink.MAV_MODE_FLAG.CUSTOM_MODE_ENABLED, system_status: (byte)MAVLink.MAV_STATE.STANDBY,
                    mavlink_version: 3));
                Write(MAVLink.MAVLINK_MSG_ID.SERVO_OUTPUT_RAW, new MAVLink.mavlink_servo_output_raw_t
                {
                    time_usec = (uint)(tick * 100_000),
                    servo1_raw = 1000, servo2_raw = 1000, servo3_raw = 1000, servo4_raw = 1000,
                    servo9_raw = _servo9,
                    port = 0,
                });

                if (tick % 5 == 0)
                {
                    Write(MAVLink.MAVLINK_MSG_ID.ADSB_VEHICLE, Adsb(0xABC123, "QFA12", -35.36, 149.17, 1200_000, 9000, 25000));
                    Write(MAVLink.MAVLINK_MSG_ID.ADSB_VEHICLE, Adsb(0x7C1234, "", -35.37, 149.16, 600_000, 27000, 12000));
                }

                if (tick % 10 == 0 && TransponderPresent)
                {
                    Write(MAVLink.MAVLINK_MSG_ID.UAVIONIX_ADSB_OUT_STATUS, new MAVLink.mavlink_uavionix_adsb_out_status_t
                    {
                        squawk = 1200,
                        state = 16 | 64 | 128, // ON
                        NIC_NACp = 9 | (10 << 4),
                        boardTemp = 35,
                        fault = 0,
                        flight_id = Encoding.ASCII.GetBytes("FAKE01\0\0"),
                    });
                }

                await _stream.FlushAsync().ConfigureAwait(false);
                tick++;
                await Task.Delay(100, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // cancelled or the link closed
        }
    }

    private static MAVLink.mavlink_adsb_vehicle_t Adsb(uint icao, string callsign, double lat, double lng, int altMm, ushort headingCdeg, ushort speedCms)
    {
        var cs = new byte[9];
        Encoding.ASCII.GetBytes(callsign).CopyTo(cs, 0);
        return new MAVLink.mavlink_adsb_vehicle_t
        {
            ICAO_address = icao,
            lat = (int)(lat * 1e7),
            lon = (int)(lng * 1e7),
            altitude = altMm,
            heading = headingCdeg,
            hor_velocity = speedCms,
            ver_velocity = 150,
            flags = (ushort)(MAVLink.ADSB_FLAGS.VALID_COORDS | MAVLink.ADSB_FLAGS.VALID_ALTITUDE | MAVLink.ADSB_FLAGS.VALID_HEADING |
                             MAVLink.ADSB_FLAGS.VALID_VELOCITY | MAVLink.ADSB_FLAGS.VALID_CALLSIGN),
            squawk = 1200,
            altitude_type = 0,
            callsign = cs,
            emitter_type = 1,
            tslc = 1,
        };
    }

    private void Write(MAVLink.MAVLINK_MSG_ID id, object msg)
    {
        lock (_writeLock)
        {
            var bytes = _parse.GenerateMAVLinkPacket20(id, msg, false, SysId, CompId, _seq++ & 0xff);
            _stream.Write(bytes, 0, bytes.Length);
        }
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
                if (msg.msgid == (uint)MAVLink.MAVLINK_MSG_ID.COMMAND_LONG)
                {
                    var cmd = (MAVLink.mavlink_command_long_t)msg.data;
                    if (cmd.command == (ushort)MAVLink.MAV_CMD.DO_SET_SERVO && (int)cmd.param1 == 9)
                        _servo9 = (ushort)cmd.param2;
                    Write(MAVLink.MAVLINK_MSG_ID.COMMAND_ACK, new MAVLink.mavlink_command_ack_t
                    {
                        command = cmd.command,
                        result = (byte)MAVLink.MAV_RESULT.ACCEPTED,
                        target_system = (byte)msg.sysid,
                        target_component = (byte)msg.compid,
                    });
                    _stream.Flush();
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
        await Task.WhenAny(_receiver, Task.Delay(2000)).ConfigureAwait(false);
        _stop.Dispose();
    }
}
