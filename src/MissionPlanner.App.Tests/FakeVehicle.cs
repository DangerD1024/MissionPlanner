using System.Collections.Concurrent;

namespace MissionPlanner.App.Tests;

/// <summary>
/// Minimal ArduCopter stand-in on any stream: sends HEARTBEAT and ATTITUDE at 10 Hz and records
/// every MAVLink message the ground station sends back.
/// </summary>
public sealed class FakeVehicle : IAsyncDisposable
{
    public const byte SysId = 1;
    public const byte CompId = 1;

    private readonly Stream _write;
    private readonly Stream _read;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _sender;
    private readonly Task _receiver;
    private readonly MAVLink.MavlinkParse _parse = new();
    private int _seq;

    public FakeVehicle(Stream stream, float rollDegrees)
        : this(stream, stream, rollDegrees)
    {
    }

    public FakeVehicle(Stream write, Stream read, float rollDegrees)
    {
        _write = write;
        _read = read;
        RollDegrees = rollDegrees;
        _sender = Task.Run(SendLoop);
        _receiver = Task.Factory.StartNew(ReceiveLoop, TaskCreationOptions.LongRunning);
    }

    public float RollDegrees { get; }

    /// <summary>Messages received from the ground station.</summary>
    public ConcurrentQueue<MAVLink.MAVLinkMessage> Received { get; } = new();

    public Exception? SendError { get; private set; }

    private async Task SendLoop()
    {
        var bootMs = 0u;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var hb = new MAVLink.mavlink_heartbeat_t(
                    custom_mode: 0,
                    type: (byte)MAVLink.MAV_TYPE.QUADROTOR,
                    autopilot: (byte)MAVLink.MAV_AUTOPILOT.ARDUPILOTMEGA,
                    base_mode: (byte)MAVLink.MAV_MODE_FLAG.CUSTOM_MODE_ENABLED,
                    system_status: (byte)MAVLink.MAV_STATE.STANDBY,
                    mavlink_version: 3);
                var att = new MAVLink.mavlink_attitude_t(bootMs, RollDegrees * (float)(Math.PI / 180), 0, 0, 0, 0, 0);

                Write(MAVLink.MAVLINK_MSG_ID.HEARTBEAT, hb);
                Write(MAVLink.MAVLINK_MSG_ID.ATTITUDE, att);
                await _write.FlushAsync().ConfigureAwait(false);

                bootMs += 100;
                await Task.Delay(100, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (!_stop.IsCancellationRequested)
        {
            SendError = ex;
        }
        catch
        {
            // the link is being torn down
        }
    }

    private void Write(MAVLink.MAVLINK_MSG_ID id, object msg)
    {
        var bytes = _parse.GenerateMAVLinkPacket20(id, msg, false, SysId, CompId, _seq++ & 0xff);
        _write.Write(bytes, 0, bytes.Length);
    }

    private void ReceiveLoop()
    {
        var parse = new MAVLink.MavlinkParse();
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var msg = parse.ReadPacket(_read);
                if (msg != null && msg.Length > 0)
                    Received.Enqueue(msg);
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
                // includes EndOfStreamException: the link closed
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
        // the receive loop exits on the stream's read timeout or when the owner closes the stream
        await Task.WhenAny(_receiver, Task.Delay(2000)).ConfigureAwait(false);
        _stop.Dispose();
    }
}
