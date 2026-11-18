using System.Collections.Concurrent;
using System.Text;

namespace MissionPlanner.App.Tests.ConfigScreens;

/// <summary>
/// A scripted ArduCopter that answers the parameter protocol: PARAM_REQUEST_LIST,
/// PARAM_REQUEST_READ and PARAM_SET (echoed back as PARAM_VALUE, as the autopilot does).
/// </summary>
internal sealed class ParamVehicle : IAsyncDisposable
{
    public const byte SysId = 1;
    public const byte CompId = 1;

    private readonly Stream _stream;
    private readonly object _writeLock = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly MAVLink.MavlinkParse _parse = new();
    private readonly List<string> _names;
    private readonly ConcurrentDictionary<string, float> _values;
    private readonly Task _sender;
    private readonly Task _receiver;
    private int _seq;

    public ParamVehicle(Stream stream, IEnumerable<(string Name, float Value)> parameters)
    {
        _stream = stream;
        var list = parameters.ToList();
        _names = list.Select(p => p.Name).ToList();
        _values = new ConcurrentDictionary<string, float>(list.ToDictionary(p => p.Name, p => p.Value));
        _sender = Task.Run(SendLoop);
        _receiver = Task.Factory.StartNew(ReceiveLoop, TaskCreationOptions.LongRunning);
    }

    /// <summary>PARAM_SET requests received, in order.</summary>
    public ConcurrentQueue<(string Name, float Value)> Sets { get; } = new();

    public float Value(string name) => _values[name];

    private async Task SendLoop()
    {
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
                await Task.Delay(100, _stop.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // stopped or the link closed
        }
    }

    private void SendParam(int index)
    {
        var name = _names[index];
        var id = new byte[16];
        Encoding.ASCII.GetBytes(name, 0, Math.Min(16, name.Length), id, 0);
        Write(MAVLink.MAVLINK_MSG_ID.PARAM_VALUE, new MAVLink.mavlink_param_value_t(
            _values[name], (ushort)_names.Count, (ushort)index, id, (byte)MAVLink.MAV_PARAM_TYPE.REAL32));
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

    private static string Name(byte[] id) => Encoding.ASCII.GetString(id).TrimEnd('\0');

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
                switch ((MAVLink.MAVLINK_MSG_ID)msg.msgid)
                {
                    case MAVLink.MAVLINK_MSG_ID.PARAM_REQUEST_LIST:
                        for (var i = 0; i < _names.Count; i++)
                            SendParam(i);
                        break;
                    case MAVLink.MAVLINK_MSG_ID.PARAM_REQUEST_READ:
                        var read = (MAVLink.mavlink_param_request_read_t)msg.data;
                        var index = read.param_index >= 0 ? read.param_index : _names.IndexOf(Name(read.param_id));
                        if (index >= 0 && index < _names.Count)
                            SendParam(index);
                        break;
                    case MAVLink.MAVLINK_MSG_ID.PARAM_SET:
                        var set = (MAVLink.mavlink_param_set_t)msg.data;
                        var name = Name(set.param_id);
                        if (_values.ContainsKey(name))
                        {
                            _values[name] = set.param_value;
                            Sets.Enqueue((name, set.param_value));
                            SendParam(_names.IndexOf(name));
                        }

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
