using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;

namespace MissionPlanner.App.Core.ViewModels.FlightData;

/// <summary>
/// Gimbal / mount commands. A vehicle with a MAVLink gimbal manager (GIMBAL_MANAGER_INFORMATION
/// seen) gets DO_GIMBAL_MANAGER_PITCHYAW; older mounts get DO_MOUNT_CONTROL with the mount
/// mode in p7, as Mission Planner's Payload tab and "Set Mount" button send.
/// </summary>
public static class GimbalCommands
{
    public static double Wrap180(double angle)
    {
        if (double.IsNaN(angle) || double.IsInfinity(angle))
            return 0;
        angle %= 360;
        if (angle > 180)
            angle -= 360;
        else if (angle <= -180)
            angle += 360;
        return angle;
    }

    /// <summary>Gimbal manager angles: p1 pitch, p2 yaw (degrees), rates NaN, p5 flags (YAW_LOCK = earth frame), p7 gimbal id.</summary>
    public static VehicleCommand PitchYaw(double pitch, double yaw, bool yawLock, byte gimbalId = 0) =>
        new(MAVLink.MAV_CMD.DO_GIMBAL_MANAGER_PITCHYAW, (float)Math.Clamp(pitch, -180, 180), (float)Wrap180(yaw), float.NaN, float.NaN,
            yawLock ? (float)MAVLink.GIMBAL_MANAGER_FLAGS.YAW_LOCK : 0, 0, gimbalId);

    /// <summary>Gimbal manager mode change (RETRACT, NEUTRAL) with no angles.</summary>
    public static VehicleCommand ManagerFlags(MAVLink.GIMBAL_MANAGER_FLAGS flags, byte gimbalId = 0) =>
        new(MAVLink.MAV_CMD.DO_GIMBAL_MANAGER_PITCHYAW, float.NaN, float.NaN, float.NaN, float.NaN, (float)flags, 0, gimbalId);

    /// <summary>Legacy mount angles: p1 pitch, p2 roll, p3 yaw (degrees), p7 MAVLINK_TARGETING.</summary>
    public static VehicleCommand MountAngles(double pitch, double roll, double yaw) =>
        new(MAVLink.MAV_CMD.DO_MOUNT_CONTROL, (float)pitch, (float)roll, (float)Wrap180(yaw), 0, 0, 0,
            (float)MAVLink.MAV_MOUNT_MODE.MAVLINK_TARGETING);

    /// <summary>Legacy mount mode: DO_MOUNT_CONTROL with the mode in p7 (Mission Planner's "Set Mount").</summary>
    public static VehicleCommand MountMode(MAVLink.MAV_MOUNT_MODE mode) =>
        new(MAVLink.MAV_CMD.DO_MOUNT_CONTROL, 0, 0, 0, 0, 0, 0, (float)mode);

    /// <summary>DO_MOUNT_CONFIGURE with a mode; ArduPilot honours it with or without a gimbal manager.</summary>
    public static VehicleCommand Configure(MAVLink.MAV_MOUNT_MODE mode) => new(MAVLink.MAV_CMD.DO_MOUNT_CONFIGURE, (float)mode);

    /// <summary>Point at a location: DO_SET_ROI_LOCATION (gimbal manager) or DO_SET_ROI, as COMMAND_INT, altitude above home.</summary>
    public static VehicleCommand PointAt(double lat, double lng, double altAboveHome, bool gimbalManager, byte gimbalId = 0) =>
        gimbalManager
            ? VehicleCommand.Int(MAVLink.MAV_CMD.DO_SET_ROI_LOCATION, lat, lng, (float)altAboveHome, MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT, p1: gimbalId)
            : VehicleCommand.Int(MAVLink.MAV_CMD.DO_SET_ROI, lat, lng, (float)altAboveHome, MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT);

    public static VehicleCommand RoiNone(byte gimbalId = 0) => new(MAVLink.MAV_CMD.DO_SET_ROI_NONE, gimbalId);
}

/// <summary>
/// Payload tab, after Mission Planner's: pitch / yaw sliders for the camera gimbal, Retract,
/// Neutral, RC targeting, centre and look down, and "Point Camera Here" on the map. Uses the
/// gimbal manager protocol when the vehicle has one, the mount commands otherwise. Sliders send
/// without waiting for an acknowledgement, at most 5 times a second.
/// </summary>
public sealed partial class PayloadViewModel : PageViewModel
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(PayloadViewModel));

    private const int SendIntervalMs = 200;

    private readonly IFlightCommands _commands;
    private bool _anglesDirty;
    private bool _syncing;
    private long _lastSend;
    private long _tick;

    public PayloadViewModel(AppServices services, IFlightCommands commands)
        : base("Payload")
    {
        Services = services;
        _commands = commands;
    }

    public AppServices Services { get; }

    public bool CanCommand => _commands.CanCommand;

    public bool IsConnected => _commands.IsConnected;

    [ObservableProperty]
    private double _pitch;

    [ObservableProperty]
    private double _yaw;

    [ObservableProperty]
    private double _pitchMin = -90;

    [ObservableProperty]
    private double _pitchMax = 30;

    /// <summary>Roll, for mounts driven by DO_MOUNT_CONTROL (the gimbal manager has no roll).</summary>
    [ObservableProperty]
    private double _roll;

    [ObservableProperty]
    private double _rollMin = -30;

    [ObservableProperty]
    private double _rollMax = 30;

    [ObservableProperty]
    private double _yawMin = -180;

    [ObservableProperty]
    private double _yawMax = 180;

    /// <summary>Gimbal manager only: hold the heading (earth frame) instead of following the vehicle.</summary>
    [ObservableProperty]
    private bool _yawLock;

    [ObservableProperty]
    private bool _usesGimbalManager;

    [ObservableProperty]
    private string _mountText = "";

    [ObservableProperty]
    private string _attitudeText = "";

    partial void OnPitchChanged(double value) => MarkDirty();

    partial void OnYawChanged(double value) => MarkDirty();

    partial void OnRollChanged(double value) => MarkDirty();

    private void MarkDirty()
    {
        if (!_syncing)
            _anglesDirty = true;
    }

    public override void OnShown() => UpdateStatus();

    public override void Tick()
    {
        if (_anglesDirty && Environment.TickCount64 - _lastSend >= SendIntervalMs)
            SendAngles();
        if (_tick++ % 5 == 0)
            UpdateStatus();
    }

    /// <summary>Which protocol, the slider ranges and the reported attitude.</summary>
    private void UpdateStatus()
    {
        OnPropertyChanged(nameof(CanCommand));
        OnPropertyChanged(nameof(IsConnected));
        var vehicle = Services.Session.Vehicle;
        var gm = vehicle.GimbalManager;
        var info = gm != null && gm.ManagerInfo.TryGetValue(0, out var i) ? i : (MAVLink.mavlink_gimbal_manager_information_t?)null;
        UsesGimbalManager = info != null;

        double? Param(string n) => Services.Params.Has(n) ? Services.Params.Get(n) : null;
        if (info is { } gmi)
        {
            MountText = string.Create(CultureInfo.InvariantCulture, $"Gimbal manager, device {gmi.gimbal_device_id}");
            SetRange(Rad(gmi.pitch_min, -90), Rad(gmi.pitch_max, 30), Rad(gmi.yaw_min, -180), Rad(gmi.yaw_max, 180));
        }
        else
        {
            var type = Param("MNT1_TYPE") ?? Param("MNT_TYPE");
            var typeName = type is { } t ? Services.Params.Meta(Services.Params.Has("MNT1_TYPE") ? "MNT1_TYPE" : "MNT_TYPE")
                ?.Values.FirstOrDefault(v => v.Key == t).Value ?? t.ToString("0", CultureInfo.InvariantCulture) : null;
            MountText = type switch
            {
                null => L.Tr(IsConnected ? "No mount parameters on this vehicle" : "Connect to a vehicle"),
                0 => L.Tr("No mount configured (MNT1_TYPE = None)"),
                _ => L.Format("Mount: {0}", typeName),
            };
            SetRange(Param("MNT1_PITCH_MIN") ?? -90, Param("MNT1_PITCH_MAX") ?? 30, Param("MNT1_YAW_MIN") ?? -180,
                Param("MNT1_YAW_MAX") ?? 180);
            var rollMin = Param("MNT1_ROLL_MIN") ?? -30;
            var rollMax = Param("MNT1_ROLL_MAX") ?? 30;
            if (rollMin < rollMax)
            {
                RollMin = Math.Round(rollMin);
                RollMax = Math.Round(rollMax);
            }
        }

        var cs = Services.Session.Cs;
        var reported = vehicle.getPacketLast((uint)MAVLink.MAVLINK_MSG_ID.MOUNT_STATUS) != null ||
                       vehicle.getPacketLast((uint)MAVLink.MAVLINK_MSG_ID.GIMBAL_DEVICE_ATTITUDE_STATUS) != null;
        AttitudeText = reported
            ? string.Create(CultureInfo.InvariantCulture, $"Reported: pitch {cs.campointa:0.#}°  roll {cs.campointb:0.#}°  yaw {cs.campointc:0.#}°")
            : "";
    }

    private static double Rad(float radians, double fallback) =>
        float.IsNaN(radians) || radians == 0 ? fallback : radians * 180 / Math.PI;

    private void SetRange(double pitchMin, double pitchMax, double yawMin, double yawMax)
    {
        if (pitchMin >= pitchMax)
            (pitchMin, pitchMax) = (-90, 30);
        if (yawMin >= yawMax)
            (yawMin, yawMax) = (-180, 180);
        PitchMin = Math.Round(pitchMin);
        PitchMax = Math.Round(pitchMax);
        YawMin = Math.Round(yawMin);
        YawMax = Math.Round(yawMax);
    }

    /// <summary>The command the sliders send for the current angles.</summary>
    public VehicleCommand AnglesCommand() =>
        UsesGimbalManager ? GimbalCommands.PitchYaw(Pitch, Yaw, YawLock) : GimbalCommands.MountAngles(Pitch, Roll, Yaw);

    private void SendAngles()
    {
        _anglesDirty = false;
        _lastSend = Environment.TickCount64;
        if (!IsConnected)
            return;
        var s = Services.Session;
        var mav = s.Mav;
        var sysid = s.Vehicle.sysid;
        var compid = s.Vehicle.compid;
        var cmd = AnglesCommand();
        _ = Task.Run(() =>
        {
            try
            {
                cmd.Send(mav, sysid, compid, requireAck: false);
            }
            catch (Exception ex)
            {
                Log.Info("gimbal angles", ex);
            }
        });
    }

    private Task Run(string what, VehicleCommand cmd, string ok) =>
        _commands.RunAsync(what, (mav, sysid, compid, _) => cmd.Send(mav, sysid, compid), ok,
            $"{what}: the vehicle rejected {cmd.Command}");

    /// <summary>Moves the sliders without sending them.</summary>
    private void SetSliders(double pitch, double yaw)
    {
        _syncing = true;
        Pitch = pitch;
        Yaw = yaw;
        Roll = 0;
        _syncing = false;
    }

    [RelayCommand]
    private Task Retract()
    {
        var cmd = UsesGimbalManager
            ? GimbalCommands.ManagerFlags(MAVLink.GIMBAL_MANAGER_FLAGS.RETRACT)
            : GimbalCommands.MountMode(MAVLink.MAV_MOUNT_MODE.RETRACT);
        return Run("Retract", cmd, "Gimbal retracted");
    }

    [RelayCommand]
    private Task Neutral()
    {
        var cmd = UsesGimbalManager
            ? GimbalCommands.ManagerFlags(MAVLink.GIMBAL_MANAGER_FLAGS.NEUTRAL)
            : GimbalCommands.MountMode(MAVLink.MAV_MOUNT_MODE.NEUTRAL);
        return Run("Neutral", cmd, "Gimbal at neutral");
    }

    /// <summary>Hands the gimbal back to the RC sticks.</summary>
    [RelayCommand]
    private Task RcTargeting()
    {
        var cmd = UsesGimbalManager
            ? GimbalCommands.Configure(MAVLink.MAV_MOUNT_MODE.RC_TARGETING)
            : GimbalCommands.MountMode(MAVLink.MAV_MOUNT_MODE.RC_TARGETING);
        return Run("RC targeting", cmd, "Gimbal follows the RC sticks");
    }

    /// <summary>Mission Planner's reset button: angles to zero in MAVLink targeting.</summary>
    [RelayCommand]
    private Task Center()
    {
        SetSliders(0, 0);
        return Run("Center gimbal", AnglesCommand(), "Gimbal centred");
    }

    [RelayCommand]
    private Task LookDown()
    {
        SetSliders(Math.Max(PitchMin, -90), 0);
        return Run("Look down", AnglesCommand(), "Gimbal pointing down");
    }

    [RelayCommand]
    private Task ClearRoi() => Run("Clear ROI", GimbalCommands.RoiNone(), "Region of interest cleared");

    /// <summary>Map "Point Camera Here": asks the target altitude above home (as Mission Planner), then sets the ROI.</summary>
    public async Task PointCameraAtAsync(double lat, double lng)
    {
        if (!IsConnected)
        {
            _commands.Report("Connect to a vehicle first.", true);
            return;
        }

        if (lat == 0 || lng == 0)
        {
            _commands.Report("Bad coordinates", true);
            return;
        }

        var answer = await Services.Dialogs.PromptAsync("Point Camera Here", L.Format("Target altitude above home ({0})", Units.Altitude), "0");
        if (answer == null)
            return;
        if (!FlightDataViewModel.TryParseNumber(answer, out var alt))
        {
            _commands.Report($"\"{answer}\" is not an altitude", true);
            return;
        }

        var metres = alt / CurrentState.multiplieralt;
        var gm = Services.Session.Vehicle.GimbalManager?.HasCapability(MAVLink.GIMBAL_MANAGER_CAP_FLAGS.CAN_POINT_LOCATION_GLOBAL) == true;
        var cmd = GimbalCommands.PointAt(lat, lng, metres, gm);
        await Run("Point camera", cmd, string.Create(CultureInfo.InvariantCulture, $"Camera pointing at {lat:0.000000} {lng:0.000000}"));
    }
}
