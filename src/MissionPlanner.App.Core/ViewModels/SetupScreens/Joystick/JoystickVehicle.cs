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
public sealed class SessionJoystickVehicle(VehicleSession session) : IJoystickVehicle
{
    public bool IsConnected => session.IsConnected && session.Mav.BaseStream?.IsOpen == true;

    public uint SysId => session.Vehicle.sysid;

    public byte CompId => session.Vehicle.compid;

    public bool IsCopter => session.Cs.firmware == Firmwares.ArduCopter2;

    public double? GetParam(string name)
    {
        var list = session.Vehicle.param;
        return list.ContainsKey(name) ? list[name].Value : null;
    }

    public void SendRcOverride(MAVLink.mavlink_rc_channels_override_t rc) => session.Mav.sendPacket(rc, SysId, CompId);

    public void SendManualControl(MAVLink.mavlink_manual_control_t mc) => session.Mav.sendPacket(mc, SysId, CompId);

    public bool Arm(bool arm) => session.Mav.doARM(SysId, CompId, arm);

    public void SetMode(string mode) => session.Mav.setMode(SysId, CompId, mode);

    public bool Command(MAVLink.MAV_CMD cmd, float p1, float p2 = 0, float p3 = 0, float p4 = 0, float p5 = 0, float p6 = 0, float p7 = 0,
        bool requireAck = true) =>
        session.Mav.doCommand(SysId, CompId, cmd, p1, p2, p3, p4, p5, p6, p7, requireAck);

    public bool SetParam(string name, double value) => session.Mav.setParam(SysId, CompId, name, value);

    public bool PointGimbal()
    {
        var cs = session.Cs;
        return session.Mav.doCommandInt(SysId, CompId, MAVLink.MAV_CMD.DO_SET_ROI, 0, 0, 0, 0,
            (int)(cs.gimballat * 1e7), (int)(cs.gimballng * 1e7), (float)cs.GimbalPoint.Alt);
    }
}
