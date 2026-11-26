using System.Diagnostics;
using MissionPlanner.App.Core.Links;

namespace MissionPlanner.App.Core.Session;

/// <summary>
/// Restarting the autopilot. The library's <c>doReboot</c> also reopens a USB port by itself half a second
/// after the command (the WinForms app's way back), which here races the app's own reconnect: two connects
/// on one link, each counting down its own timeout. <see cref="Send"/> only sends the command; the session
/// notices it went out (<see cref="IsRestart"/>), reports a USB drop that follows as
/// <see cref="VehicleSession.LostByReboot"/>, and the connection bar connects once the board is back.
/// </summary>
public static class VehicleReboot
{
    /// <summary>MAV_CMD_PREFLIGHT_REBOOT_SHUTDOWN p1 = 1 to the selected vehicle, sent again once if unanswered, as <c>doReboot</c>.</summary>
    public static bool Send(MAVLinkInterface mav)
    {
        var sysid = mav.sysidcurrent;
        var compid = (byte)mav.compidcurrent;
        if (Try())
            return true;
        // the board can drop off USB before its answer gets through: the command went out, it is restarting
        if (mav.BaseStream is not { IsOpen: true })
            return true;
        return Try();

        bool Try()
        {
            try
            {
                return mav.doCommand(sysid, compid, MAVLink.MAV_CMD.PREFLIGHT_REBOOT_SHUTDOWN, 1, 0, 0, 0, 0, 0, 0);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>A command restarting the autopilot into its firmware (p1 = 1; not the bootloader, DFU or a lock-up test).</summary>
    public static bool IsRestart(MAVLink.MAVLinkMessage message)
    {
        if (message.msgid == (uint)MAVLink.MAVLINK_MSG_ID.COMMAND_LONG && message.data is MAVLink.mavlink_command_long_t c)
            return c.command == (ushort)MAVLink.MAV_CMD.PREFLIGHT_REBOOT_SHUTDOWN && c.param1 == 1;
        if (message.msgid == (uint)MAVLink.MAVLINK_MSG_ID.COMMAND_INT && message.data is MAVLink.mavlink_command_int_t i)
            return i.command == (ushort)MAVLink.MAV_CMD.PREFLIGHT_REBOOT_SHUTDOWN && i.param1 == 1;
        return false;
    }
}

/// <summary>
/// After a reboot drops a USB link: waits for the board to come back and connects to the same port. Used
/// when auto-connect is off (auto-connect finds the board by itself).
/// </summary>
public sealed class RebootReconnect
{
    /// <summary>A board that is not back by then needs a look (or a power cycle): stop trying.</summary>
    public TimeSpan GiveUpAfter { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Once the port is back, a moment for the bootloader to hand over to the firmware.</summary>
    public TimeSpan Settle { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan Poll { get; init; } = TimeSpan.FromMilliseconds(250);

    public Func<string, bool> PortExists { get; init; } = LinkHealth.SerialPortExists;

    /// <summary>
    /// Connects once the port is back, trying again until <see cref="GiveUpAfter"/>. True once connected;
    /// false when it gave up, or stopped because something else connected (<paramref name="idle"/> false).
    /// </summary>
    public async Task<bool> RunAsync(LinkSpec link, Func<bool> idle, Func<Task<bool>> connect, CancellationToken cancel)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < GiveUpAfter)
        {
            if (!idle())
                return false;
            if (PortExists(link.Address))
            {
                await Task.Delay(Settle, cancel).ConfigureAwait(true);
                if (!idle())
                    return false;
                if (await connect().ConfigureAwait(true))
                    return true;
                cancel.ThrowIfCancellationRequested();
            }

            await Task.Delay(Poll, cancel).ConfigureAwait(true);
        }

        return false;
    }
}
