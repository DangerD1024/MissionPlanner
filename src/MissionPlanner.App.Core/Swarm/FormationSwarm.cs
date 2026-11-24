using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.Swarm;

/// <summary>
/// The group commands of Mission Planner's swarm windows (<c>Swarm.Arm</c>, <c>Takeoff</c>, ...). The ones
/// named "excluding leader" skip the leader, which the pilot flies.
/// </summary>
public abstract class SwarmBase
{
    /// <summary>The vehicle the pilot flies; the others follow it.</summary>
    public ISwarmVehicle? Leader { get; set; }

    /// <summary>Takeoff altitude of the group takeoff (Mission Planner uses 5 m).</summary>
    public float TakeoffAlt { get; set; } = 5;

    private IEnumerable<ISwarmVehicle> Followers(IEnumerable<ISwarmVehicle> vehicles) => vehicles.Where(v => v != Leader);

    public void Arm(IEnumerable<ISwarmVehicle> vehicles)
    {
        foreach (var v in Followers(vehicles))
            v.Arm(true);
    }

    public void Disarm(IEnumerable<ISwarmVehicle> vehicles)
    {
        foreach (var v in Followers(vehicles))
            v.Arm(false);
    }

    /// <summary>Guided, then takeoff, every vehicle but the leader.</summary>
    public void Takeoff(IEnumerable<ISwarmVehicle> vehicles)
    {
        foreach (var v in Followers(vehicles))
        {
            v.SetMode("GUIDED");
            v.Command(MAVLink.MAV_CMD.TAKEOFF, 0, 0, 0, 0, 0, 0, TakeoffAlt);
        }
    }

    /// <summary>Land every vehicle, the leader too.</summary>
    public void Land(IEnumerable<ISwarmVehicle> vehicles)
    {
        foreach (var v in vehicles)
            v.SetMode("Land");
    }

    public void Guided(IEnumerable<ISwarmVehicle> vehicles)
    {
        foreach (var v in Followers(vehicles))
            v.SetMode("GUIDED");
    }

    public void Auto(IEnumerable<ISwarmVehicle> vehicles)
    {
        foreach (var v in Followers(vehicles))
            v.SetMode("AUTO");
    }

    /// <summary>Called once when the update loop starts.</summary>
    public virtual void Begin()
    {
    }

    /// <summary>One pass of the update loop: read the leader, send every follower its target.</summary>
    public abstract void Step(IReadOnlyList<ISwarmVehicle> vehicles);
}

/// <summary>
/// Mission Planner's Swarm (Formation): every vehicle holds an offset from the leader in the leader's frame,
/// sent at 10 Hz as a guided position and velocity target with the leader's heading (a plane gets
/// attitude targets instead).
/// </summary>
public sealed class FormationSwarm : SwarmBase
{
    private readonly Dictionary<ISwarmVehicle, Vector3> _offsets = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ISwarmVehicle, (SwarmPid Roll, SwarmPid Pitch, SwarmPid Yaw, SwarmPid Thrust)> _pids = new(ReferenceEqualityComparer.Instance);

    private PointLatLngAlt? _leaderPos;

    /// <summary>Point a gimbal at the leader's heading instead of yawing the vehicle.</summary>
    public bool Gimbal { get; set; }

    /// <summary>Offset of a vehicle (a new one is placed at x = number of offsets, as Mission Planner does).</summary>
    public Vector3 GetOffset(ISwarmVehicle v)
    {
        lock (_offsets)
            return _offsets.TryGetValue(v, out var o) ? o : new Vector3(_offsets.Count, 0, 0);
    }

    public bool HasOffset(ISwarmVehicle v)
    {
        lock (_offsets)
            return _offsets.ContainsKey(v);
    }

    public void SetOffset(ISwarmVehicle v, double x, double y, double z)
    {
        lock (_offsets)
            _offsets[v] = new Vector3(x, y, z);
    }

    /// <summary>
    /// Makes <paramref name="leader"/> the leader and moves every offset so the formation keeps its shape
    /// around it (Mission Planner's Set Leader).
    /// </summary>
    public void SetLeader(IEnumerable<ISwarmVehicle> vehicles, ISwarmVehicle leader)
    {
        var lead = GetOffset(leader);
        foreach (var v in vehicles)
        {
            var o = GetOffset(v);
            SetOffset(v, o.x - lead.x, o.y - lead.y, o.z - lead.z);
        }

        Leader = leader;
        SetOffset(leader, 0, 0, 0);
    }

    /// <summary>
    /// Takes the offsets from where the vehicles are now, relative to the leader (Mission Planner's Update Pos);
    /// vehicles more than 200 m away keep theirs. Returns how many were updated.
    /// </summary>
    public int CaptureOffsets(IEnumerable<ISwarmVehicle> vehicles)
    {
        if (Leader == null || !Leader.HasPosition())
            return 0;

        var n = 0;
        foreach (var v in vehicles)
        {
            if (v == Leader || !v.HasPosition())
                continue;
            var o = SwarmGeometry.OffsetFromLeader(Leader.Position(), Leader.Yaw, v.Position());
            if (Math.Abs(o.x) < 200 && Math.Abs(o.y) < 200)
            {
                SetOffset(v, o.x, o.y, o.z);
                n++;
            }
        }

        return n;
    }

    public override void Begin()
    {
        // make sure the leader's position comes in fast
        Leader?.RequestPositionRate(10, attitude: true);
    }

    public override void Step(IReadOnlyList<ISwarmVehicle> vehicles)
    {
        if (Leader == null || !Leader.HasPosition())
            return;

        _leaderPos = Leader.Position();

        foreach (var v in vehicles)
        {
            if (v == Leader)
                continue;
            try
            {
                SendCommand(v);
            }
            catch (Exception ex)
            {
                log4net.LogManager.GetLogger(typeof(FormationSwarm)).Warn("formation: " + v.Name, ex);
            }
        }
    }

    /// <summary>The target of a follower for the leader's current position and heading.</summary>
    public PointLatLngAlt TargetFor(ISwarmVehicle v, PointLatLngAlt leaderPos, double leaderYaw) =>
        SwarmGeometry.OffsetTarget(leaderPos, leaderYaw, GetOffset(v));

    private void SendCommand(ISwarmVehicle mav)
    {
        var leader = Leader!;
        var target = TargetFor(mav, _leaderPos!, leader.Yaw);
        mav.GuidedTarget = target;

        if (mav.IsPlane)
        {
            SendPlane(mav, target, GetOffset(mav).x);
            return;
        }

        // position and the leader's velocity
        mav.SetPositionTarget(true, true, false, target.Lat, target.Lng, target.Alt, leader.Vx, leader.Vy, leader.Vz, 0, 0);

        // within 3 degrees don't send
        if (Math.Abs(mav.Yaw - leader.Yaw) > 3)
        {
            if (!Gimbal)
                mav.Command(MAVLink.MAV_CMD.CONDITION_YAW, (float)leader.Yaw, 100, 0, 0, 0, 0, 0, false);
            else
                mav.MountControl(45, 0, leader.Yaw);
        }
    }

    private void SendPlane(ISwarmVehicle mav, PointLatLngAlt target, double x)
    {
        var leader = Leader!;
        var here = mav.Position();
        var dist = target.GetDistance(here);
        var targyaw = here.GetBearing(target);

        var targettrailer = target.newpos(leader.Yaw, Math.Abs(dist) * -0.25);
        var targetleader = target.newpos(leader.Yaw, 10 + dist);

        double yawerror;
        if (dist < 100)
        {
            targyaw = here.GetBearing(targetleader);
            // the target is behind: slow down
            if (Math.Abs(SwarmGeometry.Wrap180(here.GetBearing(target) - targyaw)) > 45)
                dist *= -1;
        }
        else
        {
            targyaw = here.GetBearing(targettrailer);
        }

        yawerror = SwarmGeometry.Wrap180(targyaw - mav.Yaw);

        var att = new MAVLink.mavlink_set_attitude_target_t
        {
            target_system = mav.SysId,
            target_component = mav.CompId,
            type_mask = 0xff,
        };

        if (!_pids.TryGetValue(mav, out var pid))
        {
            pid = (new SwarmPid(1f, .03f, 0.02f, 10, 20, 0.1f), new SwarmPid(1f, .03f, 0.02f, 10, 20, 0.1f),
                new SwarmPid(1, 0, 0, 15, 20, 0.1f), new SwarmPid(0.01f, 0.001f, 0, 0.5f, 20, 0.1f));
            _pids[mav] = pid;
        }

        // pitch from the altitude error
        att.type_mask -= 0b00000010;
        pid.Pitch.SetInput((float)(target.Alt - mav.Alt));
        double newpitch = pid.Pitch.Pid();

        // roll: the bank for the arc to the target, averaged with the leader's turn less this offset
        var mavturnradius = leader.TurnRadius - x;
        var distToTarget = here.GetDistance(target);
        var bearingToTarget = here.GetBearing(target);
        if (distToTarget < 30)
            bearingToTarget = here.GetBearing(targetleader);
        if (distToTarget > 100)
            bearingToTarget = here.GetBearing(targettrailer);

        var bearingDelta = SwarmGeometry.Wrap180(bearingToTarget - mav.Yaw);
        var tangent90 = bearingDelta > 0 ? 90 : -90;
        double newroll = 0;
        if (Math.Abs(bearingDelta) < 85)
        {
            var insideAngle = Math.Abs(tangent90 - bearingDelta);
            var angleCenter = 180 - insideAngle * 2;
            var sine1 = Math.Max(distToTarget, 40) / Math.Sin(angleCenter * MathHelper.deg2rad);
            var radius = sine1 * Math.Sin(insideAngle * MathHelper.deg2rad);
            radius = (Math.Abs(radius) + Math.Abs(mavturnradius)) / 2;
            var angleBank = mav.GroundSpeed * mav.GroundSpeed / radius / 9.8 * MathHelper.rad2deg;
            newroll = bearingDelta > 0 ? Math.Abs(angleBank) : -Math.Abs(angleBank);
        }

        newroll += Math.Clamp(bearingDelta, -20, 20);

        // thrust from the distance to the target
        att.type_mask -= 0b01000000;
        pid.Thrust.SetInput((float)dist);
        if (dist > 40)
            pid.Thrust.ResetI();
        att.thrust = (float)Math.Clamp(pid.Thrust.Pid(), 0.1, 1);

        var q = Quaternion.from_euler312(newroll * MathHelper.deg2rad, newpitch * MathHelper.deg2rad, yawerror * MathHelper.deg2rad);
        att.q = [(float)q.q1, (float)q.q2, (float)q.q3, (float)q.q4];
        att.type_mask -= 0b10000101;

        mav.SendAttitudeTarget(att);
    }
}
