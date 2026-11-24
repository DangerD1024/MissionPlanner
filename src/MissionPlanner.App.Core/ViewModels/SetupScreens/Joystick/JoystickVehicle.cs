using MissionPlanner.App.Core.Session;
using MissionPlanner.ArduPilot;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Joystick;

/// <summary>What the joystick needs from the vehicle. Action methods block; they run on a worker.</summary>
public interface IJoystickVehicle
{
    bool IsConnected { get; }

    uint SysId { get; }

    byte CompId { get; }

    double? GetParam(string name);

    void SendRcOverride(MAVLink.mavlink_rc_channels_override_t rc);

    void SendManualControl(MAVLink.mavlink_manual_control_t mc);

    bool Arm(bool arm);

    void SetMode(string mode);

    bool Command(MAVLink.MAV_CMD cmd, float p1, float p2 = 0, float p3 = 0, float p4 = 0, float p5 = 0, float p6 = 0, float p7 = 0,
        bool requireAck = true);

    bool SetParam(string name, double value);

    /// <summary>Points the gimbal at its region of interest (DO_SET_ROI at the gimbal point).</summary>
    bool PointGimbal();

    bool IsCopter { get; }
}

/// <summary>The session's selected vehicle.</summary>
public sealed class SessionJoystickVehicle : IJoystickVehicle
{
    private readonly VehicleSession _session;
    private long _foreignOverrideTicks;

    public SessionJoystickVehicle(VehicleSession session)
    {
        _session = session;
        // Our own sends never loop back, so any RC_CHANNELS_OVERRIDE arriving on this
        // link is another controller (e.g. the VOT_C tracker holding/steering). Yield
        // while it is active instead of fighting it channel-by-channel (the FC applies
        // whichever override arrives last, which makes the airframe oscillate).
        session.Mav.OnPacketReceived += (_, m) =>
        {
            if (m.msgid == (uint)MAVLink.MAVLINK_MSG_ID.RC_CHANNELS_OVERRIDE)
                Interlocked.Exchange(ref _foreignOverrideTicks, Environment.TickCount64);
        };
    }

    public bool IsConnected => _session.IsConnected && _session.Mav.BaseStream?.IsOpen == true;

    public uint SysId => _session.Vehicle.sysid;

    public byte CompId => _session.Vehicle.compid;

    public bool IsCopter => _session.Cs.firmware == Firmwares.ArduCopter2;

    public double? GetParam(string name)
    {
        var list = _session.Vehicle.param;
        return list.ContainsKey(name) ? list[name].Value : null;
    }

    public void SendRcOverride(MAVLink.mavlink_rc_channels_override_t rc)
    {
        if (Environment.TickCount64 - Interlocked.Read(ref _foreignOverrideTicks) < 600)
            return;   // another controller owns the sticks right now
        _session.Mav.sendPacket(rc, SysId, CompId);
    }

    public void SendManualControl(MAVLink.mavlink_manual_control_t mc) => _session.Mav.sendPacket(mc, SysId, CompId);

    public bool Arm(bool arm) => _session.Mav.doARM(SysId, CompId, arm);

    public void SetMode(string mode) => _session.Mav.setMode(SysId, CompId, mode);

    public bool Command(MAVLink.MAV_CMD cmd, float p1, float p2 = 0, float p3 = 0, float p4 = 0, float p5 = 0, float p6 = 0, float p7 = 0,
        bool requireAck = true) =>
        _session.Mav.doCommand(SysId, CompId, cmd, p1, p2, p3, p4, p5, p6, p7, requireAck);

    public bool SetParam(string name, double value) => _session.Mav.setParam(SysId, CompId, name, value);

    public bool PointGimbal()
    {
        var cs = _session.Cs;
        return _session.Mav.doCommandInt(SysId, CompId, MAVLink.MAV_CMD.DO_SET_ROI, 0, 0, 0, 0,
            (int)(cs.gimballat * 1e7), (int)(cs.gimballng * 1e7), (float)cs.GimbalPoint.Alt);
    }
}
