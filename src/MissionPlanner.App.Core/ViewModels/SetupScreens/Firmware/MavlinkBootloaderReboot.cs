using MissionPlanner.Comms;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Firmware;

/// <summary>
/// Asks ArduPilot running on a USB port to restart into its bootloader, so the user does not have
/// to replug the board (<c>Firmware.AttemptRebootToBootloader</c>): wait for a heartbeat, then
/// MAV_CMD_PREFLIGHT_REBOOT_SHUTDOWN with param1 = 3. The command is sent without waiting for an
/// acknowledgement, because the board drops off USB as it reboots.
/// </summary>
public static class MavlinkBootloaderReboot
{
    /// <summary>Tries each port in turn until one answers with a heartbeat.</summary>
    public static async Task<bool> TryPortsAsync(IEnumerable<string> ports, Action<string>? log, CancellationToken cancel)
    {
        foreach (var port in ports)
        {
            cancel.ThrowIfCancellationRequested();
            var p = port;
            bool sent;
            try
            {
                sent = await Task.Run(() => TryReboot(new SerialPort(p, 115200)), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log?.Invoke($"{p}: {ex.Message}");
                continue;
            }

            if (sent)
            {
                log?.Invoke($"Asked the autopilot on {p} to reboot into its bootloader.");
                return true;
            }
        }

        return false;
    }

    /// <summary>Opens <paramref name="link"/>, waits up to ~2 s for a vehicle heartbeat and sends the reboot. Blocks; closes the link.</summary>
    public static bool TryReboot(ICommsSerial link)
    {
        var mav = new MAVLinkInterface();
        try
        {
            mav.BaseStream = link;
            if (!link.IsOpen)
                link.Open();

            var hb = mav.getHeartBeat();
            if (hb.Length <= 5)
                return false;

            var cmd = new MAVLink.mavlink_command_long_t
            {
                target_system = (byte)hb.sysid,
                target_component = hb.compid,
                command = (ushort)MAVLink.MAV_CMD.PREFLIGHT_REBOOT_SHUTDOWN,
                param1 = 3, // reboot and stay in the bootloader
            };
            mav.sendPacket(cmd, hb.sysid, hb.compid);
            Thread.Sleep(200);
            return true;
        }
        finally
        {
            try
            {
                mav.Close();
            }
            catch (Exception)
            {
            }

            try
            {
                link.Dispose();
            }
            catch (Exception)
            {
            }
        }
    }
}
