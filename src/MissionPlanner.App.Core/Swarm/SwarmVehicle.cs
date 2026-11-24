using MissionPlanner.App.Core.Session;
using MissionPlanner.ArduPilot;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.Swarm;

/// <summary>
/// One vehicle of a swarm: the telemetry the swarm controllers read and the library calls they make
/// (the same <see cref="MAVLinkInterface"/> calls Mission Planner's Swarm windows make on
/// <c>MAVState.parent</c>). The calls block; controllers run them on a worker thread.
/// </summary>
public interface ISwarmVehicle
{
    byte SysId { get; }

    byte CompId { get; }

    /// <summary>"MAV 1-1", as Mission Planner's swarm status panels name a vehicle.</summary>
    string Name { get; }

    double Lat { get; }

    double Lng { get; }

    /// <summary>Altitude above home in metres (the guided targets are relative-alt).</summary>
    double Alt { get; }

    /// <summary>Heading in degrees.</summary>
    double Yaw { get; }

    /// <summary>Velocity north/east/down in m/s.</summary>
    double Vx { get; }

    double Vy { get; }

    double Vz { get; }

    /// <summary>Ground speed in m/s.</summary>
    double GroundSpeed { get; }

    /// <summary>Turn radius in metres from ground speed and bank (0 below 1 m/s).</summary>
    double TurnRadius { get; }

    bool Armed { get; }

    string Mode { get; }

    /// <summary>GPS fix type (3 = 3D).</summary>
    int GpsFix { get; }

    int GpsFix2 { get; }

    /// <summary>ArduPlane: the formation flies it with attitude targets instead of position targets.</summary>
    bool IsPlane { get; }

    PointLatLngAlt Home { get; }

    /// <summary>The last guided target the swarm sent it (shown in the status list), or null.</summary>
    PointLatLngAlt? GuidedTarget { get; set; }

    /// <summary>A parameter's value if the vehicle has it (parameters downloaded).</summary>
    double? GetParam(string name);

    bool Arm(bool arm);

    void SetMode(string mode);

    bool Command(MAVLink.MAV_CMD cmd, float p1, float p2, float p3, float p4, float p5, float p6, float p7, bool requireAck = true);

    /// <summary>SET_POSITION_TARGET_GLOBAL_INT, relative-alt frame.</summary>
    void SetPositionTarget(bool pos, bool vel, bool yaw, double lat, double lng, double alt, double vx, double vy, double vz,
        double yawRad, double yawRate);

    /// <summary>A guided "fly to here" (MISSION_ITEM with current = 2).</summary>
    void GuidedWaypoint(double lat, double lng, double alt);

    void MountControl(double pitch, double roll, double yaw);

    /// <summary>Asks for the position stream (and attitude) at a rate.</summary>
    void RequestPositionRate(int hz, bool attitude = false);

    bool SetParam(string name, double value);

    void SendAttitudeTarget(MAVLink.mavlink_set_attitude_target_t target);

    /// <summary>Downloads the mission (item 0 is home).</summary>
    IReadOnlyList<Locationwp> DownloadMission();
}

public static class SwarmVehicleExtensions
{
    /// <summary>Position with altitude above home.</summary>
    public static PointLatLngAlt Position(this ISwarmVehicle v) => new(v.Lat, v.Lng, v.Alt);

    public static Vector3 Velocity(this ISwarmVehicle v) => new(v.Vx, v.Vy, v.Vz);

    public static bool HasPosition(this ISwarmVehicle v) => v.Lat != 0 && v.Lng != 0;

    /// <summary>
    /// Position and velocity target with yaw ignored (Mission Planner's <c>DroneBase.SendPositionVelocity</c>).
    /// </summary>
    public static void SendPositionVelocity(this ISwarmVehicle v, PointLatLngAlt pos, Vector3 vel)
    {
        v.SetPositionTarget(true, true, false, pos.Lat, pos.Lng, pos.Alt, vel.x, vel.y, vel.z, 0, 0);
        v.GuidedTarget = new PointLatLngAlt(pos);
    }
}

/// <summary>The vehicles a swarm page works with.</summary>
public interface ISwarmFleet
{
    IReadOnlyList<ISwarmVehicle> Vehicles { get; }
}

/// <summary>Every vehicle heard on the session's link (Mission Planner's <c>MainV2.Comports</c> × <c>MAVlist</c>).</summary>
public sealed class SessionFleet(VehicleSession session) : ISwarmFleet
{
    private readonly Dictionary<MAVState, MavSwarmVehicle> _cache = new(ReferenceEqualityComparer.Instance);

    public IReadOnlyList<ISwarmVehicle> Vehicles
    {
        get
        {
            if (!session.IsConnected)
                return [];
            lock (_cache)
            {
                var list = new List<ISwarmVehicle>();
                foreach (var mav in session.Vehicles)
                {
                    // other ground stations on the link are not part of a swarm
                    if (mav.aptype == MAVLink.MAV_TYPE.GCS)
                        continue;
                    if (!_cache.TryGetValue(mav, out var v))
                        _cache[mav] = v = new MavSwarmVehicle(mav);
                    list.Add(v);
                }

                return list;
            }
        }
    }
}

/// <summary>A vehicle on a link, driven through its <see cref="MAVLinkInterface"/>.</summary>
public sealed class MavSwarmVehicle(MAVState mav) : ISwarmVehicle
{
    public MAVState State { get; } = mav;

    private MAVLinkInterface Link => State.parent;

    private CurrentState Cs => State.cs;

    public byte SysId => (byte)State.sysid;

    public byte CompId => State.compid;

    public string Name => $"MAV {SysId}-{CompId}";

    public double Lat => Cs.lat;

    public double Lng => Cs.lng;

    public double Alt => Cs.alt / CurrentState.multiplieralt;

    public double Yaw => Cs.yaw;

    public double Vx => Cs.vx;

    public double Vy => Cs.vy;

    public double Vz => Cs.vz;

    public double GroundSpeed => Cs.groundspeed / CurrentState.multiplierspeed;

    public double TurnRadius => CurrentState.fromDistDisplayUnit(Cs.radius);

    public bool Armed => Cs.armed;

    public string Mode => Cs.mode ?? "";

    public int GpsFix => (int)Cs.gpsstatus;

    public int GpsFix2 => (int)Cs.gpsstatus2;

    public bool IsPlane => Cs.firmware == Firmwares.ArduPlane;

    public PointLatLngAlt Home => Cs.HomeLocation ?? PointLatLngAlt.Zero;

    public PointLatLngAlt? GuidedTarget { get; set; }

    public double? GetParam(string name) => State.param.ContainsKey(name) ? State.param[name].Value : null;

    public bool Arm(bool arm) => Link.doARM(SysId, CompId, arm);

    public void SetMode(string mode) => Link.setMode(SysId, CompId, mode);

    public bool Command(MAVLink.MAV_CMD cmd, float p1, float p2, float p3, float p4, float p5, float p6, float p7, bool requireAck = true) =>
        Link.doCommand(SysId, CompId, cmd, p1, p2, p3, p4, p5, p6, p7, requireAck);

    public void SetPositionTarget(bool pos, bool vel, bool yaw, double lat, double lng, double alt, double vx, double vy, double vz,
        double yawRad, double yawRate)
    {
        Link.setPositionTargetGlobalInt(SysId, CompId, pos, vel, false, yaw, MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT_INT,
            lat, lng, alt, vx, vy, vz, yawRad, yawRate);
        if (pos)
            GuidedTarget = new PointLatLngAlt(lat, lng, alt);
    }

    public void GuidedWaypoint(double lat, double lng, double alt)
    {
        Link.setGuidedModeWP(SysId, CompId, new Locationwp
        {
            alt = (float)alt,
            lat = lat,
            lng = lng,
            id = (ushort)MAVLink.MAV_CMD.WAYPOINT,
            // the altitudes are above home
            frame = (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT,
        });
        GuidedTarget = new PointLatLngAlt(lat, lng, alt);
    }

    public void MountControl(double pitch, double roll, double yaw) => Link.setMountControl(SysId, CompId, pitch, roll, yaw, false);

    public void RequestPositionRate(int hz, bool attitude = false)
    {
        Link.requestDatastream(MAVLink.MAV_DATA_STREAM.POSITION, hz, SysId, CompId);
        Cs.rateposition = hz;
        if (attitude)
            Cs.rateattitude = hz;
    }

    public bool SetParam(string name, double value) => Link.setParam(SysId, CompId, name, value);

    public void SendAttitudeTarget(MAVLink.mavlink_set_attitude_target_t target) => Link.sendPacket(target, SysId, CompId);

    public IReadOnlyList<Locationwp> DownloadMission() =>
        mav_mission.download(Link, SysId, CompId, MAVLink.MAV_MISSION_TYPE.MISSION).GetAwaiter().GetResult();
}
