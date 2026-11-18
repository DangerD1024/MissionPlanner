using System.Text;
using log4net;
using MissionPlanner.App.Core.Session;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Calibration;

/// <summary>
/// What the calibration processes need from the vehicle link. The real one talks to the selected
/// vehicle through <see cref="MAVLinkInterface"/>; tests script the vehicle's side.
/// </summary>
public interface ICalibrationLink
{
    /// <summary>
    /// Sends a COMMAND_LONG to the vehicle and waits for its COMMAND_ACK (library rules: true on
    /// ACCEPTED, false on any other result, <see cref="TimeoutException"/> without an answer).
    /// Never blocks the calling thread.
    /// </summary>
    Task<bool> CommandAsync(MAVLink.MAV_CMD command, float p1 = 0, float p2 = 0, float p3 = 0, float p4 = 0,
        float p5 = 0, float p6 = 0, float p7 = 0);

    /// <summary>Calls <paramref name="handler"/> (on the link's reader thread) for each message of this type from the vehicle until disposed.</summary>
    IDisposable Subscribe(MAVLink.MAVLINK_MSG_ID id, Action<MAVLink.MAVLinkMessage> handler);

    /// <summary>Sends one message to the vehicle without waiting for an answer.</summary>
    Task SendAsync(object message);
}

/// <summary><see cref="ICalibrationLink"/> for the vehicle selected in the session.</summary>
public sealed class VehicleCalibrationLink(VehicleSession session) : ICalibrationLink
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(VehicleCalibrationLink));

    public Task<bool> CommandAsync(MAVLink.MAV_CMD command, float p1 = 0, float p2 = 0, float p3 = 0, float p4 = 0,
        float p5 = 0, float p6 = 0, float p7 = 0)
    {
        var v = session.Vehicle;
        return Task.Run(() => session.Mav.doCommand(v.sysid, v.compid, command, p1, p2, p3, p4, p5, p6, p7));
    }

    public IDisposable Subscribe(MAVLink.MAVLINK_MSG_ID id, Action<MAVLink.MAVLinkMessage> handler)
    {
        var v = session.Vehicle;
        var mav = session.Mav;
        var token = mav.SubscribeToPacketType(id, msg =>
        {
            try
            {
                handler(msg);
            }
            catch (Exception ex)
            {
                // a page bug must not stop the reader
                Log.Warn("calibration message " + id, ex);
            }

            return true;
        }, v.sysid, v.compid);
        return new Unsubscriber(() => mav.UnSubscribeToPacketType(token));
    }

    public Task SendAsync(object message)
    {
        var v = session.Vehicle;
        return Task.Run(() => session.Mav.sendPacket(message, v.sysid, v.compid));
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

internal static class StatusTextDecoder
{
    /// <summary>The text of a STATUSTEXT, without the zero padding.</summary>
    public static string Decode(MAVLink.MAVLinkMessage msg)
    {
        var text = msg.ToStructure<MAVLink.mavlink_statustext_t>().text ?? [];
        var end = Array.IndexOf(text, (byte)0);
        return Encoding.ASCII.GetString(text, 0, end < 0 ? text.Length : end).Trim();
    }
}
