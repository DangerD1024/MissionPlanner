using MissionPlanner.App.Core.ViewModels.SetupScreens.Calibration;

namespace MissionPlanner.App.Tests.SetupScreens;

/// <summary>A scripted vehicle for the calibration processes: records commands and sent messages, and delivers pushed messages to subscribers.</summary>
internal sealed class FakeCalibrationLink : ICalibrationLink
{
    private readonly MAVLink.MavlinkParse _parse = new();
    private readonly List<(MAVLink.MAVLINK_MSG_ID Id, Action<MAVLink.MAVLinkMessage> Handler)> _subs = new();
    private int _seq;

    public List<(MAVLink.MAV_CMD Command, float[] Params)> Commands { get; } = new();

    public List<object> Sent { get; } = new();

    /// <summary>What CommandAsync answers; throw from it to simulate a timeout.</summary>
    public Func<MAVLink.MAV_CMD, bool> Answer { get; set; } = _ => true;

    public int SubscriptionCount
    {
        get
        {
            lock (_subs)
                return _subs.Count;
        }
    }

    public bool IsSubscribed(MAVLink.MAVLINK_MSG_ID id)
    {
        lock (_subs)
            return _subs.Any(s => s.Id == id);
    }

    public Task<bool> CommandAsync(MAVLink.MAV_CMD command, float p1 = 0, float p2 = 0, float p3 = 0, float p4 = 0,
        float p5 = 0, float p6 = 0, float p7 = 0)
    {
        Commands.Add((command, [p1, p2, p3, p4, p5, p6, p7]));
        try
        {
            return Task.FromResult(Answer(command));
        }
        catch (Exception ex)
        {
            return Task.FromException<bool>(ex);
        }
    }

    public IDisposable Subscribe(MAVLink.MAVLINK_MSG_ID id, Action<MAVLink.MAVLinkMessage> handler)
    {
        var entry = (id, handler);
        lock (_subs)
            _subs.Add(entry);
        return new Sub(() =>
        {
            lock (_subs)
                _subs.Remove(entry);
        });
    }

    public Task SendAsync(object message)
    {
        Sent.Add(message);
        return Task.CompletedTask;
    }

    /// <summary>Encodes a message as the vehicle would send it and hands it to the subscribers of its type.</summary>
    public void Push(MAVLink.MAVLINK_MSG_ID id, object data)
    {
        var bytes = _parse.GenerateMAVLinkPacket20(id, data, false, 1, 1, _seq++ & 0xff);
        var msg = new MAVLink.MAVLinkMessage(bytes);
        List<Action<MAVLink.MAVLinkMessage>> handlers;
        lock (_subs)
            handlers = _subs.Where(s => s.Id == id).Select(s => s.Handler).ToList();
        foreach (var h in handlers)
            h(msg);
    }

    public void PushStatusText(string text, MAVLink.MAV_SEVERITY severity = MAVLink.MAV_SEVERITY.INFO)
    {
        var bytes = new byte[50];
        System.Text.Encoding.ASCII.GetBytes(text, 0, Math.Min(text.Length, 50), bytes, 0);
        Push(MAVLink.MAVLINK_MSG_ID.STATUSTEXT, new MAVLink.mavlink_statustext_t { severity = (byte)severity, text = bytes });
    }

    public void PushPositionRequest(MAVLink.ACCELCAL_VEHICLE_POS pos) =>
        Push(MAVLink.MAVLINK_MSG_ID.COMMAND_LONG, new MAVLink.mavlink_command_long_t
        {
            command = (ushort)MAVLink.MAV_CMD.ACCELCAL_VEHICLE_POS,
            param1 = (float)pos,
            target_system = 255,
            target_component = 190,
        });

    private sealed class Sub(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
