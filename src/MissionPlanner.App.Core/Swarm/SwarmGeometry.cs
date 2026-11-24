using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.Swarm;

/// <summary>
/// Formation offsets as Mission Planner's swarm uses them: metres in the leader's frame, x to the
/// leader's right, y ahead of it, z up, turned with the leader's heading.
/// </summary>
/// <remarks>
/// Mission Planner turns the offsets in the leader's UTM grid, whose north is a degree or two off true
/// north away from the zone's central meridian, so its formations came out turned by that much; here
/// they are turned from true north, the reference of the heading.
/// </remarks>
public static class SwarmGeometry
{
    /// <summary>The point at <paramref name="offset"/> from the leader, the leader heading <paramref name="leaderYaw"/> degrees.</summary>
    public static PointLatLngAlt OffsetTarget(PointLatLngAlt leader, double leaderYaw, Vector3 offset)
    {
        var heading = -leaderYaw * MathHelper.deg2rad;
        var east = offset.x * Math.Cos(heading) - offset.y * Math.Sin(heading);
        var north = offset.x * Math.Sin(heading) + offset.y * Math.Cos(heading);

        var lat = leader.Lat + north / EarthRadius * MathHelper.rad2deg;
        var lng = leader.Lng + east / (EarthRadius * Math.Cos(leader.Lat * MathHelper.deg2rad)) * MathHelper.rad2deg;
        return new PointLatLngAlt(lat, lng, leader.Alt + offset.z);
    }

    /// <summary>WGS84 equatorial radius; offsets of a formation are small enough for a flat earth.</summary>
    private const double EarthRadius = 6378137.0;

    /// <summary>
    /// Where <paramref name="vehicle"/> sits in the leader's frame (the inverse of <see cref="OffsetTarget"/>, z = 0),
    /// Mission Planner's "Update Pos".
    /// </summary>
    public static Vector3 OffsetFromLeader(PointLatLngAlt leader, double leaderYaw, PointLatLngAlt vehicle)
    {
        var north = (vehicle.Lat - leader.Lat) * MathHelper.deg2rad * EarthRadius;
        var east = (vehicle.Lng - leader.Lng) * MathHelper.deg2rad * EarthRadius * Math.Cos(leader.Lat * MathHelper.deg2rad);

        var heading = -leaderYaw * MathHelper.deg2rad;
        var x = east * Math.Cos(heading) + north * Math.Sin(heading);
        var y = -east * Math.Sin(heading) + north * Math.Cos(heading);
        return new Vector3(x, y, 0);
    }

    public static double Wrap180(double input)
    {
        if (input > 180)
            return input - 360;
        if (input < -180)
            return input + 360;
        return input;
    }
}

/// <summary>ArduPilot's AC_PID as Mission Planner's swarm carries it (used to fly a plane in formation).</summary>
public sealed class SwarmPid
{
    private const float FiltHzMin = 0.01f;

    private float _dt;
    private float _input;
    private float _derivative;
    private readonly float _kp;
    private readonly float _ki;
    private float _integrator;
    private readonly float _imax;
    private readonly float _kd;
    private float _filtHz;
    private bool _resetFilter = true;

    public SwarmPid(float p, float i, float d, float imax, float filtHz, float dt)
    {
        _dt = dt;
        _kp = p;
        _ki = i;
        _kd = d;
        _imax = Math.Abs(imax);
        _filtHz = Math.Max(filtHz, FiltHzMin);
    }

    public void SetInput(float input)
    {
        if (float.IsInfinity(input) || float.IsNaN(input))
            return;

        if (_resetFilter)
        {
            _resetFilter = false;
            _input = input;
            _derivative = 0;
        }

        var change = FiltAlpha() * (input - _input);
        _input += change;
        if (_dt > 0)
            _derivative = change / _dt;
    }

    public float P => _input * _kp;

    public float I()
    {
        if (_ki == 0 || _dt == 0)
            return 0;
        _integrator = Math.Clamp(_integrator + _input * _ki * _dt, -_imax, _imax);
        return _integrator;
    }

    public float D => _kd * _derivative;

    public float Pid() => P + I() + D;

    public void ResetI() => _integrator = 0;

    private float FiltAlpha()
    {
        if (_filtHz == 0)
            return 1;
        var rc = 1 / (2 * MathF.PI * _filtHz);
        return _dt / (_dt + rc);
    }
}
