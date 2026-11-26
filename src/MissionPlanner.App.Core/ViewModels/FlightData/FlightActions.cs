using System.Globalization;
using MissionPlanner.ArduPilot;

namespace MissionPlanner.App.Core.ViewModels.FlightData;

/// <summary>The "Do Action" list of Mission Planner's Actions tab (FlightData.actions), in its order.</summary>
public enum FlightAction
{
    Loiter_Unlim,
    Return_To_Launch,
    Preflight_Calibration,
    Mission_Start,
    Preflight_Reboot_Shutdown,
    Trigger_Camera,
    System_Time,
    Battery_Reset,
    ADSB_Out_Ident,
    Scripting_cmd_stop_and_restart,
    Scripting_cmd_stop,
    HighLatency_Enable,
    HighLatency_Disable,
    Toggle_Safety_Switch,
    Parachute_Release,
    Engine_Start,
    Engine_Stop,
    Terminate_Flight,
    Format_SD_Card,
    Reboot_Mass_Storage,
}

/// <summary>A Do Action entry for the combo box.</summary>
public sealed record FlightActionOption(FlightAction Action)
{
    public string Name { get; } = Action.ToString().Replace('_', ' ');

    public override string ToString() => Name;
}

/// <summary>The plain COMMAND_LONG an action sends when it has no dedicated library call.</summary>
public readonly record struct ActionCommand(MAVLink.MAV_CMD Command, float P1, float P2, float P3);

public static class FlightActions
{
    public static IReadOnlyList<FlightActionOption> Options { get; } =
        Enum.GetValues<FlightAction>().Select(a => new FlightActionOption(a)).ToArray();

    /// <summary>
    /// The generic command for an action, as Mission Planner derives it: the enum name upper-cased
    /// as a MAV_CMD, else "DO_START_" + name, else "DO_" + name, with Preflight_Calibration and
    /// Battery_Reset presets.
    /// Null for actions handled by a dedicated call (see <see cref="HasDedicatedCall"/>).
    /// </summary>
    public static ActionCommand? GenericCommand(FlightAction action, Firmwares firmware)
    {
        if (HasDedicatedCall(action))
            return null;

        float p1 = 0, p2 = 0, p3 = 0;
        switch (action)
        {
            case FlightAction.Preflight_Calibration:
                if (firmware == Firmwares.ArduCopter2)
                    p1 = 1; // gyro
                p3 = 1; // baro / airspeed
                break;
            case FlightAction.Battery_Reset:
                p1 = 0xff; // all batteries
                p2 = 100; // percent
                break;
        }

        var upper = action.ToString().ToUpper(CultureInfo.InvariantCulture);
        if (Enum.TryParse<MAVLink.MAV_CMD>(upper, out var cmd) && Enum.IsDefined(cmd))
            return new ActionCommand(cmd, p1, p2, p3);
        if (Enum.TryParse("DO_START_" + upper, out cmd) && Enum.IsDefined(cmd))
            return new ActionCommand(cmd, p1, p2, p3);
        // ADSB_Out_Ident is DO_ADSB_OUT_IDENT (the WinForms lookup misses it)
        if (Enum.TryParse("DO_" + upper, out cmd) && Enum.IsDefined(cmd))
            return new ActionCommand(cmd, p1, p2, p3);
        return null;
    }

    /// <summary>Actions that use their own library call instead of a plain command.</summary>
    public static bool HasDedicatedCall(FlightAction action) => action is
        FlightAction.Format_SD_Card or FlightAction.Trigger_Camera or FlightAction.Scripting_cmd_stop_and_restart or
        FlightAction.Scripting_cmd_stop or FlightAction.System_Time or FlightAction.Terminate_Flight or
        FlightAction.Parachute_Release or FlightAction.Preflight_Reboot_Shutdown or FlightAction.Reboot_Mass_Storage or
        FlightAction.HighLatency_Enable or FlightAction.HighLatency_Disable or FlightAction.Toggle_Safety_Switch or
        FlightAction.Engine_Start or FlightAction.Engine_Stop;

    /// <summary>
    /// Runs an action against the vehicle. Blocking: call from a worker thread. Returns true when
    /// the vehicle accepted it (or, for fire-and-forget messages, when it was sent).
    /// </summary>
    public static bool Execute(MAVLinkInterface mav, uint sysid, byte compid, FlightAction action, CurrentState cs)
    {
        switch (action)
        {
            case FlightAction.Format_SD_Card:
                // p1 and p2 must both be 1 to start a format
                return mav.doCommandInt(sysid, compid, MAVLink.MAV_CMD.STORAGE_FORMAT, 1, 1, 0, 0, 0, 0, 0);
            case FlightAction.Trigger_Camera:
                mav.setDigicamControl(true);
                return true;
            case FlightAction.Scripting_cmd_stop_and_restart:
                return mav.doCommandInt(sysid, compid, MAVLink.MAV_CMD.SCRIPTING, (int)MAVLink.SCRIPTING_CMD.STOP_AND_RESTART, 0, 0, 0, 0, 0, 0);
            case FlightAction.Scripting_cmd_stop:
                return mav.doCommandInt(sysid, compid, MAVLink.MAV_CMD.SCRIPTING, (int)MAVLink.SCRIPTING_CMD.STOP, 0, 0, 0, 0, 0, 0);
            case FlightAction.System_Time:
            {
                var micros = (ulong)((DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds * 1000);
                mav.sendPacket(new MAVLink.mavlink_system_time_t { time_unix_usec = micros, time_boot_ms = 0 }, sysid, compid);
                return true;
            }
            case FlightAction.Terminate_Flight:
                return mav.doCommand(sysid, compid, MAVLink.MAV_CMD.DO_FLIGHTTERMINATION, 1, 0, 0, 0, 0, 0, 0);
            case FlightAction.Parachute_Release:
                return mav.doCommand(sysid, compid, MAVLink.MAV_CMD.DO_PARACHUTE, (float)MAVLink.PARACHUTE_ACTION.PARACHUTE_RELEASE, 0, 0, 0, 0, 0, 0);
            case FlightAction.Preflight_Reboot_Shutdown:
                return Session.VehicleReboot.Send(mav);
            case FlightAction.Reboot_Mass_Storage:
                // ArduPilot's REBOOT_TO_MASS_STORAGE action
                return mav.doCommand(sysid, compid, MAVLink.MAV_CMD.PREFLIGHT_REBOOT_SHUTDOWN, 5, 0, 0, 0, 0, 0, 0);
            case FlightAction.HighLatency_Enable:
                return mav.doHighLatency(true);
            case FlightAction.HighLatency_Disable:
                return mav.doHighLatency(false);
            case FlightAction.Toggle_Safety_Switch:
            {
                if (sysid == 0)
                    return false;
                var customMode = cs.sensors_enabled.motor_control && cs.sensors_enabled.seen ? 1u : 0u;
                mav.setMode(sysid, compid, new MAVLink.mavlink_set_mode_t { custom_mode = customMode, target_system = (byte)sysid },
                    MAVLink.MAV_MODE_FLAG.SAFETY_ARMED);
                return true;
            }
            case FlightAction.Engine_Start:
                return mav.doEngineControl(sysid, compid, true);
            case FlightAction.Engine_Stop:
                return mav.doEngineControl(sysid, compid, false);
        }

        var cmd = GenericCommand(action, cs.firmware) ?? throw new InvalidOperationException("no command for " + action);
        return mav.doCommand(sysid, compid, cmd.Command, cmd.P1, cmd.P2, cmd.P3, 0, 0, 0, 0);
    }
}

/// <summary>Flight mode names for a firmware, from the library's mode table.</summary>
public static class FlightModes
{
    /// <summary>
    /// The mode names for the combo box. <see cref="Common.getModesList"/> returns null for
    /// firmwares it has no table for (ArduSub, AP_Periph, Other) and may fail before the
    /// parameter metadata is available: both give an empty list.
    /// </summary>
    public static IReadOnlyList<string> For(Firmwares firmware, Func<Firmwares, List<KeyValuePair<int, string>>?>? source = null)
    {
        List<KeyValuePair<int, string>>? list;
        try
        {
            list = (source ?? Common.getModesList)(firmware);
        }
        catch (Exception)
        {
            list = null;
        }

        return Names(list);
    }

    /// <summary>Distinct, non-empty names in table order.</summary>
    public static IReadOnlyList<string> Names(IEnumerable<KeyValuePair<int, string>>? modes)
    {
        if (modes == null)
            return [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (var kv in modes)
        {
            var name = kv.Value?.Trim();
            if (string.IsNullOrEmpty(name) || !seen.Add(name))
                continue;
            names.Add(name);
        }

        return names;
    }

    /// <summary>The list's spelling of a mode (setMode matches case-insensitively), or null if the vehicle has no such mode.</summary>
    public static string? Find(IReadOnlyList<string> modes, string name) =>
        modes.FirstOrDefault(m => string.Equals(m, name, StringComparison.OrdinalIgnoreCase));
}
