using System.Collections.Concurrent;

namespace MissionPlanner.App.Tests.Logs;

/// <summary>
/// An ArduCopter stand-in that holds onboard logs: answers LOG_REQUEST_LIST with LOG_ENTRY,
/// LOG_REQUEST_DATA with 90-byte LOG_DATA blocks (a zero-length block at the end, as ArduPilot
/// does) and records LOG_ERASE. Sends heartbeats at 10 Hz.
/// </summary>
internal sealed class FakeLogVehicle : IAsyncDisposable
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

    public FakeLogVehicle(Stream stream, IReadOnlyList<(uint TimeUtc, byte[] Data)> logs)
    {
        _stream = stream;
        Logs = logs;
        _sender = Task.Run(SendLoop);
        _receiver = Task.Factory.StartNew(ReceiveLoop, TaskCreationOptions.LongRunning);
    }

    public IReadOnlyList<(uint TimeUtc, byte[] Data)> Logs { get; }

    public int EraseCount;

    public ConcurrentQueue<uint> Requests { get; } = new();

    /// <summary>Pause between LOG_DATA blocks, to make a transfer slow enough to cancel.</summary>
    public int BlockDelayMs { get; set; }

    /// <summary>Blocks (by index) lost the first time they are sent.</summary>
    public Func<int, bool>? DropOnce { get; set; }

    /// <summary>Bytes the log list claims beyond the real size (a log still being written).</summary>
    public int ExtraListedSize { get; set; }

    private readonly HashSet<(int Log, int Block)> _dropped = new();
    private CancellationTokenSource? _streaming;
    private Task _streamTask = Task.CompletedTask;

    private async Task SendLoop()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                Write(MAVLink.MAVLINK_MSG_ID.HEARTBEAT, new MAVLink.mavlink_heartbeat_t(0, (byte)MAVLink.MAV_TYPE.QUADROTOR,
                    (byte)MAVLink.MAV_AUTOPILOT.ARDUPILOTMEGA, (byte)MAVLink.MAV_MODE_FLAG.CUSTOM_MODE_ENABLED,
                    (byte)MAVLink.MAV_STATE.STANDBY, 3));
                await Task.Delay(100, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // stopped, or the link closed
        }
    }

    private void Write(MAVLink.MAVLINK_MSG_ID id, object msg)
    {
        var bytes = _parse.GenerateMAVLinkPacket20(id, msg, false, SysId, CompId, Interlocked.Increment(ref _seq) & 0xff);
        lock (_writeLock)
        {
            _stream.Write(bytes, 0, bytes.Length);
            _stream.Flush();
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
                Requests.Enqueue(msg.msgid);
                switch ((MAVLink.MAVLINK_MSG_ID)msg.msgid)
                {
                    case MAVLink.MAVLINK_MSG_ID.LOG_REQUEST_LIST:
                        for (var i = 0; i < Logs.Count; i++)
                        {
                            var id = (ushort)(i + 1);
                            Write(MAVLink.MAVLINK_MSG_ID.LOG_ENTRY, new MAVLink.mavlink_log_entry_t(Logs[i].TimeUtc,
                                (uint)(Logs[i].Data.Length + ExtraListedSize), id, (ushort)Logs.Count, (ushort)Logs.Count));
                        }

                        if (Logs.Count == 0)
                            Write(MAVLink.MAVLINK_MSG_ID.LOG_ENTRY, new MAVLink.mavlink_log_entry_t(0, 0, 0, 0, 0));
                        break;

                    case MAVLink.MAVLINK_MSG_ID.LOG_REQUEST_DATA:
                        // a new request replaces the one being streamed, as on ArduPilot
                        StopStream();
                        var req = (MAVLink.mavlink_log_request_data_t)msg.data;
                        var cts = new CancellationTokenSource();
                        _streaming = cts;
                        _streamTask = Task.Run(() => SendData(req, cts.Token));
                        break;

                    case MAVLink.MAVLINK_MSG_ID.LOG_REQUEST_END:
                        StopStream();
                        break;

                    case MAVLink.MAVLINK_MSG_ID.LOG_ERASE:
                        Interlocked.Increment(ref EraseCount);
                        break;
                }
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

    private void StopStream()
    {
        _streaming?.Cancel();
        _streamTask.Wait(2000);
        _streaming = null;
    }

    private void SendData(MAVLink.mavlink_log_request_data_t req, CancellationToken stop)
    {
        try
        {
            if (req.id < 1 || req.id > Logs.Count)
                return;
            var data = Logs[req.id - 1].Data;
            var end = (long)req.ofs + req.count;
            var ofs = (long)req.ofs;
            while (ofs < data.Length && ofs < end && !stop.IsCancellationRequested)
            {
                var count = (int)Math.Min(90, Math.Min(data.Length - ofs, end - ofs));
                var block = new byte[90];
                Array.Copy(data, ofs, block, 0, count);
                var drop = DropOnce != null && DropOnce((int)(ofs / 90)) && _dropped.Add((req.id, (int)(ofs / 90)));
                if (!drop)
                    Write(MAVLink.MAVLINK_MSG_ID.LOG_DATA, new MAVLink.mavlink_log_data_t((uint)ofs, req.id, (byte)count, block));
                ofs += count;
                if (BlockDelayMs > 0)
                    Thread.Sleep(BlockDelayMs);
            }

            // past the end: an empty block says so
            if (!stop.IsCancellationRequested && ofs >= data.Length && end > data.Length)
                Write(MAVLink.MAVLINK_MSG_ID.LOG_DATA, new MAVLink.mavlink_log_data_t((uint)ofs, req.id, 0, new byte[90]));
        }
        catch (Exception)
        {
            // the link closed
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _streaming?.Cancel();
        await _sender.ConfigureAwait(false);
        await Task.WhenAny(_receiver, Task.Delay(2000)).ConfigureAwait(false);
        _stop.Dispose();
    }
}
