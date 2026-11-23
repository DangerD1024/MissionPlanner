using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.ArduPilot;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.FlightData;

/// <summary>Mission Planner's "Resume Mission": the mission to upload so the vehicle carries on at a waypoint.</summary>
public static class ResumeMission
{
    /// <summary>
    /// Home (item 0), then of the items before <paramref name="resumeAt"/> only the takeoff and the
    /// DO commands (camera, servo, speed… settings still apply), then everything from the resume
    /// point on.
    /// </summary>
    public static List<Locationwp> Plan(IReadOnlyList<Locationwp> mission, int resumeAt)
    {
        var list = new List<Locationwp>();
        for (var i = 0; i < mission.Count; i++)
        {
            var wp = mission[i];
            if (i != 0 && i < resumeAt && !Keep(wp.id))
                continue;
            list.Add(wp);
        }

        return list;
    }

    private static bool Keep(ushort id) =>
        id == (ushort)MAVLink.MAV_CMD.TAKEOFF ||
        (id > (ushort)MAVLink.MAV_CMD.LAST && id <= (ushort)MAVLink.MAV_CMD.DO_LAST);
}

/// <summary>A gimbal mode in the Actions tab.</summary>
public sealed record MountModeOption(MAVLink.MAV_MOUNT_MODE Mode, string Name)
{
    public override string ToString() => Name;
}

public sealed partial class FlightDataViewModel
{
    // ---- loiter radius -----------------------------------------------------------------------

    /// <summary>LOITER_RAD (Plane) or WP_LOITER_RAD, in the distance unit; null when the vehicle has neither.</summary>
    [ObservableProperty]
    private decimal? _newLoiterRadius;

    public string DistanceUnit => Units.Distance;

    public bool HasLoiterRadius => LoiterRadiusParam != null;

    private string? LoiterRadiusParam => Services.Params.FirstExisting("LOITER_RAD", "WP_LOITER_RAD");

    private void RefreshLoiterRadius()
    {
        OnPropertyChanged(nameof(HasLoiterRadius));
        if (LoiterRadiusParam is { } p && Services.Params.Get(p) is { } v && NewLoiterRadius == null)
            NewLoiterRadius = Math.Round((decimal)(v * CurrentState.multiplierdist));
    }

    [RelayCommand]
    private Task ChangeLoiterRadiusAsync()
    {
        if (NewLoiterRadius is not { } r || LoiterRadiusParam is not { } param)
            return Task.CompletedTask;
        var metres = (double)r / CurrentState.multiplierdist;
        return RunAsync("Change loiter radius", (mav, sysid, compid, _) => mav.setParam(sysid, compid, param, metres),
            $"Loiter radius set to {r} {Units.Distance}", $"Could not set {param}");
    }

    // ---- mount mode --------------------------------------------------------------------------

    public static IReadOnlyList<MountModeOption> MountModes { get; } =
    [
        new(MAVLink.MAV_MOUNT_MODE.RETRACT, "Retract"),
        new(MAVLink.MAV_MOUNT_MODE.NEUTRAL, "Neutral"),
        new(MAVLink.MAV_MOUNT_MODE.MAVLINK_TARGETING, "MAVLink targeting"),
        new(MAVLink.MAV_MOUNT_MODE.RC_TARGETING, "RC targeting"),
        new(MAVLink.MAV_MOUNT_MODE.GPS_POINT, "GPS point"),
        new(MAVLink.MAV_MOUNT_MODE.SYSID_TARGET, "Track a system ID"),
        new(MAVLink.MAV_MOUNT_MODE.HOME_LOCATION, "Home location"),
    ];

    [ObservableProperty]
    private MountModeOption? _selectedMountMode = MountModes[3];

    [RelayCommand]
    private Task SetMountModeAsync()
    {
        if (SelectedMountMode is not { } m)
            return Task.CompletedTask;
        var hasParam = Services.Params.Has("MNT_MODE");
        return RunAsync("Mount mode", (mav, sysid, compid, _) =>
                hasParam
                    ? mav.setParam(sysid, compid, "MNT_MODE", (int)m.Mode)
                    : mav.doCommand(sysid, compid, MAVLink.MAV_CMD.DO_MOUNT_CONTROL, 0, 0, 0, 0, 0, 0, (int)m.Mode, false),
            $"Mount mode {m.Name} sent", "The vehicle rejected the mount mode");
    }

    // ---- abort landing, message, joystick, raw sensors -----------------------------------------

    [RelayCommand]
    private Task AbortLandingAsync() =>
        RunAsync("Abort landing", (mav, sysid, compid, _) => mav.doCommand(sysid, compid, MAVLink.MAV_CMD.DO_GO_AROUND, 0, 0, 0, 0, 0, 0, 0),
            "Landing aborted: the vehicle climbs away", "The vehicle rejected the go-around");

    /// <summary>Sends a STATUSTEXT the vehicle writes into its log (Mission Planner's "Message").</summary>
    [RelayCommand]
    private async Task SendLogMessageAsync()
    {
        if (!CheckCanCommand())
            return;
        var text = await Services.Dialogs.PromptAsync("Message", "Text for the vehicle to write into its log (up to 50 characters):");
        if (string.IsNullOrWhiteSpace(text))
            return;
        text = text.Length > 50 ? text[..50] : text;
        await RunAsync("Message", (mav, sysid, compid, _) =>
        {
            mav.send_text((byte)MAVLink.MAV_SEVERITY.NOTICE, text);
            return true;
        }, "Message sent: " + text, "Could not send the message");
    }

    [RelayCommand]
    private void OpenJoystick()
    {
        Services.RequestMenuPage(Screen.Setup, "Joystick");
    }

    [RelayCommand]
    private void OpenRawSensors() => Services.Windows.Show(new RawSensorsViewModel(Services), 900, 640);

    // ---- resume mission ----------------------------------------------------------------------

    [RelayCommand]
    private async Task ResumeMissionAsync()
    {
        if (!CheckCanCommand())
            return;
        if (!await Services.Dialogs.ConfirmAsync("Resume mission",
                "This rewrites the mission on the vehicle (the waypoints before the resume point are left out), then " +
                "switches to AUTO. A copter on the ground is armed and takes off first.", "Continue"))
            return;

        var cs = Services.Session.Cs;
        var suggested = Math.Max(1, (int)cs.lastautowp).ToString(CultureInfo.InvariantCulture);
        var answer = await Services.Dialogs.PromptAsync("Resume mission", "Resume at waypoint number:", suggested);
        if (answer == null || !int.TryParse(answer, NumberStyles.Integer, CultureInfo.InvariantCulture, out var resumeAt) || resumeAt < 1)
            return;

        var copter = cs.firmware == Firmwares.ArduCopter2;
        await RunAsync($"Resume at waypoint {resumeAt}", (mav, sysid, compid, state) =>
        {
            var mission = mav_mission.download(mav, sysid, compid, MAVLink.MAV_MISSION_TYPE.MISSION, null).GetAwaiter().GetResult();
            if (resumeAt >= mission.Count)
                throw new InvalidOperationException($"the mission has only {mission.Count - 1} waypoints");
            var target = mission[resumeAt];
            var plan = ResumeMission.Plan(mission, resumeAt);
            mav_mission.upload(mav, sysid, compid, MAVLink.MAV_MISSION_TYPE.MISSION, plan, null).GetAwaiter().GetResult();
            mav.setWPCurrent(sysid, compid, 1);

            if (copter && (!state.armed || state.alt < 1))
            {
                mav.setMode(sysid, compid, "GUIDED");
                if (!WaitFor(() => string.Equals(state.mode, "Guided", StringComparison.OrdinalIgnoreCase), 10000))
                    throw new TimeoutException();
                if (!state.armed && !mav.doARM(sysid, compid, true))
                    return false;
                if (!mav.doCommand(sysid, compid, MAVLink.MAV_CMD.TAKEOFF, 0, 0, 0, 0, 0, 0, target.alt))
                    return false;
                WaitFor(() => state.alt / CurrentState.multiplieralt >= target.alt - 2, 40000);
            }

            mav.setMode(sysid, compid, "AUTO");
            return WaitFor(() => string.Equals(state.mode, "Auto", StringComparison.OrdinalIgnoreCase), 10000);
        }, $"Resumed at waypoint {resumeAt}", "Resume mission failed");

        // the planner shows what the vehicle now has
        try
        {
            await Services.Mission.ReadFromVehicleAsync(Services.Session, MAVLink.MAV_MISSION_TYPE.MISSION);
        }
        catch (Exception ex)
        {
            Log.Info("read mission after resume", ex);
        }
    }
}
