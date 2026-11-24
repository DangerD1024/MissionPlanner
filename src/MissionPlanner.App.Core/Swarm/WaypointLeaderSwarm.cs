using log4net;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.Swarm;

/// <summary>A vehicle of the follow swarm with what the controller tracks about it.</summary>
public sealed class SwarmDrone(ISwarmVehicle vehicle)
{
    public ISwarmVehicle Vehicle { get; } = vehicle;

    public PointLatLngAlt Location { get; set; } = new();

    public Vector3 Velocity { get; set; } = new();

    public PointLatLngAlt? TargetLocation { get; set; }

    public Vector3 TargetVelocity { get; set; } = new();

    /// <summary>Index of the nearest point of the path (0.1 m steps).</summary>
    public int PathIndex { get; set; }

    public bool TakeoffDone { get; set; }

    /// <summary>Direction of travel, degrees.</summary>
    public double Heading => Math.Atan2(Velocity.y, Velocity.x) * (180 / Math.PI);

    /// <summary>Position in one second.</summary>
    public PointLatLngAlt ProjectedLocation => Location.newpos(Heading, Velocity.length());

    public void SendPositionVelocity(PointLatLngAlt pos, Vector3 vel) => Vehicle.SendPositionVelocity(pos, vel);
}

/// <summary>
/// Mission Planner's Follow Swarm (<c>Swarm.WaypointLeader</c>): the air master's mission is the path; a
/// ground master (a person, a car) moves along it and the drones fly in a line along the path ahead of it,
/// <see cref="Lead"/> metres in front and <see cref="Separation"/> apart. Run at 10 Hz, it takes the drones
/// through takeoff, flying to the user, following, returning along the path and landing one by one.
/// </summary>
public sealed class WaypointLeaderSwarm
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(WaypointLeaderSwarm));

    public enum Mode
    {
        idle,
        takeoff,
        flytouser,
        followuser,
        RTH,
        LandAlt,
        Land,
    }

    private readonly object _sync = new();
    private List<PointLatLngAlt> _path = new();

    public List<SwarmDrone> Drones { get; } = new();

    public ISwarmVehicle? AirMaster { get; set; }

    public ISwarmVehicle? GroundMaster { get; set; }

    /// <summary>Line separation, metres.</summary>
    public double Separation { get; set; } = 5;

    /// <summary>How far ahead of the ground master the first drone flies, metres.</summary>
    public double Lead { get; set; } = 20;

    /// <summary>The ground master this far off the path sends the drones home, metres.</summary>
    public double OffPathTrigger { get; set; } = 10;

    /// <summary>Fly in a V instead of a line.</summary>
    public bool V { get; set; }

    /// <summary>Altitude step between drones at takeoff and landing, metres.</summary>
    public double TakeoffLandAltSep { get; set; } = 2;

    /// <summary>Alternate drones fly <see cref="TakeoffLandAltSep"/> higher.</summary>
    public bool AltInterleave { get; set; }

    /// <summary>Waypoint acceleration while following, m/s/s.</summary>
    public double WpNavAccel { get; set; } = 1;

    public Mode CurrentMode { get; set; } = Mode.idle;

    /// <summary>The air master's mission (item 0 is home); the path is built from it.</summary>
    public IReadOnlyList<Locationwp> Mission { get; private set; } = [];

    /// <summary>The path in 0.1 m steps.</summary>
    public IReadOnlyList<PointLatLngAlt> Path
    {
        get
        {
            lock (_sync)
                return _path;
        }
    }

    /// <summary>Raised when talking to a drone fails (Mission Planner shows it in a popup).</summary>
    public event Action<string>? Problem;

    /// <summary>Waits used while landing; replaceable for tests.</summary>
    public Action<int> Sleep { get; set; } = Thread.Sleep;

    /// <summary>Set to stop waits in progress.</summary>
    public volatile bool Stopping;

    public void SetMission(IReadOnlyList<Locationwp> mission)
    {
        lock (_sync)
        {
            Mission = mission;
            _path = GeneratePath(mission);
        }
    }

    /// <summary>Takes every vehicle as a drone of the group (Mission Planner does so on Set Ground/Air Master).</summary>
    public void SetDrones(IEnumerable<ISwarmVehicle> vehicles)
    {
        Drones.Clear();
        Drones.AddRange(vehicles.Select(v => new SwarmDrone(v)));
    }

    private SwarmDrone AirMasterDrone => Drones.First(d => d.Vehicle == AirMaster);

    private SwarmDrone GroundMasterDrone => Drones.First(d => d.Vehicle == GroundMaster);

    public bool IsReady => AirMaster != null && GroundMaster != null &&
                           Drones.Any(d => d.Vehicle == AirMaster) && Drones.Any(d => d.Vehicle == GroundMaster);

    /// <summary>The mission as a path of points 0.1 m apart (Mission Planner's <c>Path.GeneratePath</c>).</summary>
    public static List<PointLatLngAlt> GeneratePath(IReadOnlyList<Locationwp> mission)
    {
        var result = new List<PointLatLngAlt>();
        PointLatLngAlt? prev = null;

        foreach (var wp in mission)
        {
            if (prev == null)
            {
                // home, at altitude 0
                prev = new PointLatLngAlt(wp.lat, wp.lng, 0);
                continue;
            }

            if (wp.id != (ushort)MAVLink.MAV_CMD.WAYPOINT && wp.id != (ushort)MAVLink.MAV_CMD.SPLINE_WAYPOINT)
                continue;

            var wp2 = new PointLatLngAlt(wp.lat, wp.lng, wp.alt);
            var bearing = prev.GetBearing(wp2);
            var distwps = prev.GetDistance(wp2);
            var startalt = prev.Alt == 0 ? wp2.Alt : prev.Alt;
            var endalt = wp2.Alt;

            if (distwps > 5000)
                continue;

            for (var d = 0.1; d < distwps; d += 0.1)
            {
                var pnt = prev.newpos(bearing, d);
                pnt.Alt = startalt + d / distwps * (endalt - startalt);
                result.Add(pnt);
            }

            prev = wp2;
        }

        return result;
    }

    /// <summary>
    /// Points along the path from the one nearest <paramref name="location"/>: the first <paramref name="lead"/> metres
    /// on, then every <paramref name="separation"/> metres back (Mission Planner's <c>GetLocations</c>, behind the
    /// location once the distance turns negative). Returns path indexes.
    /// </summary>
    public static List<int> GetLocationIndexes(IReadOnlyList<PointLatLngAlt> path, PointLatLngAlt location, double lead, double separation)
    {
        var result = new List<int>();
        if (path.Count == 0)
            return result;

        var start = 0;
        var mindist = double.MaxValue;
        for (var i = 0; i < path.Count; i++)
        {
            var d = location.GetDistance(path[i]);
            if (d < mindist)
            {
                mindist = d;
                start = i;
            }
        }

        var closest = path[start];
        var a = 0;
        for (var i = start; i < path.Count - 1; i++)
        {
            var targetdistance = lead - a * separation;
            if (targetdistance < 0)
                i -= 2;
            if (i < 0)
                break;

            if (closest.GetDistance(path[i]) >= Math.Abs(targetdistance))
            {
                result.Add(i);
                i = start;
                if (result.Count > 20)
                    break;
                if (separation == 0)
                    break;
                a++;
            }
        }

        return result;
    }

    public static List<PointLatLngAlt> GetLocations(IReadOnlyList<PointLatLngAlt> path, PointLatLngAlt location, double lead, double separation) =>
        GetLocationIndexes(path, location, lead, separation).Select(i => new PointLatLngAlt(path[i])).ToList();

    /// <summary>The line folded into a V: the front point, then a pair either side of each point behind it.</summary>
    public static List<PointLatLngAlt> GetLocationsV(IReadOnlyList<PointLatLngAlt> path, PointLatLngAlt location, double lead, double separation)
    {
        var result = new List<PointLatLngAlt>();
        var list = GetLocations(path, location, lead, separation);
        if (list.Count == 0)
            return result;

        var front = list[0];
        result.Add(front);
        var a = 1;
        foreach (var p in list.Skip(1))
        {
            result.Add(p.newpos(p.GetBearing(front) + 90, separation / 2 * a));
            result.Add(p.newpos(p.GetBearing(front) - 90, separation / 2 * a));
            a++;
        }

        return result;
    }

    /// <summary>Distance from <paramref name="location"/> to the nearest leg or point of the waypoints.</summary>
    public static double GetOffPathDistance(IReadOnlyList<PointLatLngAlt> wps, PointLatLngAlt location)
    {
        var disttotal = double.MaxValue;
        PointLatLngAlt? lineStart = null;
        foreach (var p in wps)
        {
            var lineEnd = new PointLatLngAlt(p.Lat, p.Lng);
            if (lineStart == null)
            {
                lineStart = lineEnd;
                continue;
            }

            var lineDist = lineStart.GetDistance2(lineEnd);
            var distToLocation = lineStart.GetDistance2(location);
            var angle = lineStart.GetBearing(location) - lineStart.GetBearing(lineEnd);
            if (angle < 0)
                angle += 360;

            var alongline = Math.Cos(angle * MathHelper.deg2rad) * distToLocation;
            if (Math.Abs(alongline) <= lineDist)
                disttotal = Math.Min(disttotal, Math.Abs(Math.Sin(angle * MathHelper.deg2rad) * distToLocation));

            lineStart = lineEnd;
        }

        // the points too: off the end of a leg, a corner is nearest
        foreach (var p in wps)
            disttotal = Math.Min(disttotal, p.GetDistance(location));

        return disttotal;
    }

    private void Fail(Exception ex)
    {
        Log.Error(ex);
        Problem?.Invoke("Communication with one of the drones is failing: " + ex.Message);
    }

    private static void DefaultAccel(ISwarmVehicle v, bool onlyIfChanged)
    {
        try
        {
            var wpnav = v.GetParam("WPNAV_ACCEL");
            var wpacc = v.GetParam("WP_ACC");
            if (wpnav != null)
            {
                if (!onlyIfChanged || wpnav != 100)
                    v.SetParam("WPNAV_ACCEL", 100); // 100 cm/s/s
            }
            else if (wpacc != null)
            {
                if (!onlyIfChanged || wpacc != 1)
                    v.SetParam("WP_ACC", 1); // 1 m/s/s
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex);
        }
    }

    /// <summary>One pass of the controller (Mission Planner's <c>DroneGroup.UpdatePositions</c>).</summary>
    public void Update()
    {
        if (!IsReady)
            return;

        foreach (var drone in Drones)
        {
            var v = drone.Vehicle;
            drone.Location = new PointLatLngAlt(v.Lat, v.Lng, v.Alt);
            drone.Velocity = new Vector3(v.Vx, v.Vy, v.Vz);
        }

        // by default the drones match the ground master's velocity
        foreach (var drone in Drones)
            drone.TargetVelocity = GroundMasterDrone.Velocity;

        // the air master first, then the others in the order they fly; the ground master is not flown
        var newlist = new List<SwarmDrone> { AirMasterDrone };
        newlist.AddRange(Drones.Where(d => d != AirMasterDrone && d != GroundMasterDrone));

        if (CollisionCheck(newlist))
            return;

        var path = Path;
        if (path.Count == 0)
            return;

        foreach (var drone in Drones)
        {
            var locs = GetLocationIndexes(path, drone.Location, 0, 0);
            if (locs.Count > 0)
                drone.PathIndex = locs[0];
        }

        switch (CurrentMode)
        {
            case Mode.idle:
                Idle();
                break;
            case Mode.takeoff:
                TakeoffStep(newlist, path);
                break;
            case Mode.flytouser:
                FlyToUser(newlist, path);
                break;
            case Mode.followuser:
                FollowUser(newlist, path);
                break;
            case Mode.RTH:
                ReturnHome(newlist, path);
                break;
            case Mode.LandAlt:
                LandAlt(newlist);
                break;
            case Mode.Land:
                Land(newlist, path);
                break;
        }
    }

    /// <summary>Holds or climbs drones about to touch. Returns true when it sent something (the pass ends).</summary>
    private bool CollisionCheck(List<SwarmDrone> list)
    {
        foreach (var drone1 in list)
        {
            if (drone1 == GroundMasterDrone || !drone1.Vehicle.Armed)
                continue;

            foreach (var drone2 in list)
            {
                if (drone1 == drone2 || drone2 == GroundMasterDrone || !drone2.Vehicle.Armed)
                    continue;

                // close now, at the same height: the higher one climbs
                if (drone1.Location.GetDistance(drone2.Location) < Separation / 2 &&
                    Math.Abs(drone1.Location.Alt - drone2.Location.Alt) < 1)
                {
                    var up = drone1.Location.Alt > drone2.Location.Alt ? drone1 : drone2;
                    up.SendPositionVelocity(new PointLatLngAlt(up.Location) { Alt = up.Location.Alt + TakeoffLandAltSep }, Vector3.Zero);
                    return true;
                }

                // close in one second
                if (drone1.ProjectedLocation.GetDistance(drone2.ProjectedLocation) < Separation / 2)
                {
                    var headingDelta = (Math.Abs(drone1.Heading - drone2.Heading) + 360) % 360;
                    var sameHeight = Math.Abs(drone1.Location.Alt - drone2.Location.Alt) < 1;

                    // same way: hold the one behind and let them settle
                    if (headingDelta < 45 && drone1.Vehicle.GroundSpeed > 0.5 && sameHeight)
                    {
                        drone1.SendPositionVelocity(drone1.Location, Vector3.Zero);
                        return true;
                    }

                    // at each other: stop both, one climbs
                    if (headingDelta > 135 && sameHeight)
                    {
                        drone1.SendPositionVelocity(new PointLatLngAlt(drone1.Location) { Alt = drone1.Location.Alt + TakeoffLandAltSep }, Vector3.Zero);
                        drone2.SendPositionVelocity(drone2.Location, Vector3.Zero);
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private void Idle()
    {
        CurrentMode = Mode.takeoff;
        foreach (var drone in Drones)
        {
            var v = drone.Vehicle;
            v.RequestPositionRate(5);
            drone.TakeoffDone = false;

            if (drone == GroundMasterDrone)
                continue;

            try
            {
                // RTL at the current altitude
                if (v.GetParam("RTL_ALT") != null)
                    v.SetParam("RTL_ALT", 0);
                else if (v.GetParam("RTL_ALT_M") != null)
                    v.SetParam("RTL_ALT_M", 0);
            }
            catch (Exception ex)
            {
                Log.Warn(ex);
            }

            DefaultAccel(v, false);
        }
    }

    private void TakeoffStep(List<SwarmDrone> newlist, IReadOnlyList<PointLatLngAlt> path)
    {
        var a = 0;
        // places behind the air master's home, a line's length back
        var newpositions = GetLocations(path, AirMaster!.Home, Drones.Count * Separation, Separation);
        if (newpositions.Count == 0)
            return;

        foreach (var drone in newlist)
        {
            var v = drone.Vehicle;
            if (a >= newpositions.Count)
                break;
            try
            {
                if (!v.Mode.Equals("guided", StringComparison.OrdinalIgnoreCase))
                    v.SetMode("GUIDED");
                if (!v.Armed && !v.Arm(true))
                    return;
            }
            catch (Exception ex)
            {
                Fail(ex);
                return;
            }

            // stagger the heights 0, 1, 2, 0...
            drone.TargetLocation = newpositions[a];
            drone.TargetLocation.Alt += TakeoffLandAltSep * (a % 3);
            var takeoffalt = (float)drone.TargetLocation.Alt;

            try
            {
                if (v.Alt < takeoffalt - 0.5 && !drone.TakeoffDone &&
                    v.Command(MAVLink.MAV_CMD.TAKEOFF, 0, 0, 0, 0, 0, 0, takeoffalt))
                    drone.TakeoffDone = true;
            }
            catch (Exception ex)
            {
                Fail(ex);
                return;
            }

            v.GuidedTarget = new PointLatLngAlt(0, 0, drone.TargetLocation.Alt);

            // still climbing: on to the next drone
            if (v.Alt < takeoffalt - 0.5)
            {
                Sleep(100);
                if (!v.Armed)
                    return;
                a++;
                continue;
            }

            // at height: to its place
            drone.SendPositionVelocity(drone.TargetLocation, Vector3.Zero);

            // every third drone waits for the one in front to get there
            if (drone.TargetLocation.GetDistance(drone.Location) > Separation && a % 3 == 2)
                return;

            a++;
        }

        // wait for all to get within separation of their place
        foreach (var drone in newlist)
        {
            if (drone.TargetLocation == null)
                return;
            if (drone.TargetLocation.GetDistance(drone.Location) > Separation)
            {
                drone.SendPositionVelocity(drone.TargetLocation, Vector3.Zero);
                return;
            }
        }

        CurrentMode = Mode.flytouser;
    }

    private void FlyToUser(List<SwarmDrone> newlist, IReadOnlyList<PointLatLngAlt> path)
    {
        var positions = GetLocations(path, AirMasterDrone.Location, Separation, Separation);
        if (positions.Count == 0)
        {
            // at the end of the path
            if (path[^1].GetDistance(AirMasterDrone.Location) < Separation)
                CurrentMode = Mode.RTH;
            return;
        }

        var b = 0;
        foreach (var drone in newlist)
        {
            if (b == positions.Count)
                break;
            drone.TargetLocation = positions[b];
            if (AltInterleave)
                drone.TargetLocation.Alt += TakeoffLandAltSep * (b % 2);
            drone.SendPositionVelocity(drone.TargetLocation, Vector3.Zero);
            b++;
        }

        // the same places as following uses, so there is no jump
        var follow = GetLocations(path, GroundMasterDrone.Location, Lead, Separation);
        if (follow.Count > 0 && AirMasterDrone.Location.GetDistance(follow[0]) < Separation)
        {
            // faster for the dynamic part
            foreach (var drone in newlist)
            {
                if (drone == GroundMasterDrone)
                    continue;
                var v = drone.Vehicle;
                if (v.GetParam("WPNAV_ACCEL") != null)
                    v.SetParam("WPNAV_ACCEL", WpNavAccel * 100.0);
                else if (v.GetParam("WP_ACC") != null)
                    v.SetParam("WP_ACC", WpNavAccel);
                v.SetMode("GUIDED");
            }

            CurrentMode = Mode.followuser;
        }
    }

    private void FollowUser(List<SwarmDrone> newlist, IReadOnlyList<PointLatLngAlt> path)
    {
        var positions = V
            ? GetLocationsV(path, GroundMasterDrone.Location, Lead, Separation)
            : GetLocations(path, GroundMasterDrone.Location, Lead, Separation);
        if (positions.Count == 0)
            return;

        // the user has left the path
        var wps = Mission.Where(w => w.lat != 0 || w.lng != 0).Select(w => new PointLatLngAlt(w)).ToList();
        if (GetOffPathDistance(wps, GroundMasterDrone.Location) > OffPathTrigger)
            CurrentMode = Mode.RTH;

        var c = 0;
        foreach (var drone in newlist)
        {
            if (drone.Vehicle == GroundMaster)
                continue;
            if (c > positions.Count - 1)
                break;
            drone.TargetLocation = positions[c];
            if (AltInterleave)
                drone.TargetLocation.Alt += TakeoffLandAltSep * (c % 2);
            drone.SendPositionVelocity(drone.TargetLocation, drone.TargetVelocity / 3);
            c++;
        }
    }

    private void ReturnHome(List<SwarmDrone> newlist, IReadOnlyList<PointLatLngAlt> path)
    {
        var positions = GetLocations(path, AirMasterDrone.Location, Separation, Separation);
        if (positions.Count == 0)
        {
            if (AirMasterDrone.Location.GetDistance(path[^1]) < Separation)
                CurrentMode = Mode.LandAlt;
            return;
        }

        var d = 0;
        foreach (var drone in newlist)
        {
            if (d > positions.Count - 1)
                break;
            DefaultAccel(drone.Vehicle, true);

            drone.TargetLocation = positions[d];
            var prevalt = drone.TargetLocation.Alt;
            if (AltInterleave)
                drone.TargetLocation.Alt += TakeoffLandAltSep * (d % 2);
            drone.SendPositionVelocity(drone.TargetLocation, Vector3.Zero);
            // landing steps up from the path height
            drone.TargetLocation.Alt = prevalt;
            d++;
        }
    }

    private void LandAlt(List<SwarmDrone> newlist)
    {
        var e = 0;
        foreach (var drone in newlist)
        {
            drone.TargetLocation ??= new PointLatLngAlt(drone.Location);
            drone.TargetLocation.Alt += TakeoffLandAltSep * e;
            DefaultAccel(drone.Vehicle, true);

            drone.SendPositionVelocity(drone.TargetLocation, Vector3.Zero);
            Sleep(200);
            drone.SendPositionVelocity(drone.TargetLocation, Vector3.Zero);
            e++;
        }

        foreach (var drone in newlist)
        {
            // wait for the height
            while (!Stopping && drone.Vehicle.Alt < drone.TargetLocation!.Alt - 0.5)
            {
                if (!drone.Vehicle.Armed)
                    break;
                Sleep(200);
                drone.SendPositionVelocity(drone.TargetLocation, Vector3.Zero);
            }

            if (Stopping)
                return;

            Sleep(200);
            Log.Info(drone.Vehicle.SysId + " " + drone.Vehicle.Alt + " at alt " + drone.TargetLocation!.Alt);
            drone.Vehicle.SetMode("RTL");
        }

        CurrentMode = Mode.Land;
    }

    private void Land(List<SwarmDrone> newlist, IReadOnlyList<PointLatLngAlt> path)
    {
        // the drone nearest home lands first
        SwarmDrone? closest = null;
        var home = AirMaster!.Home;
        var lastpnt = path[^1];
        foreach (var drone in newlist)
        {
            if (!drone.Vehicle.Armed)
                continue;
            // already low: on to the next
            if (drone.Vehicle.Alt < lastpnt.Alt - 1)
                continue;
            if (closest == null || home.GetDistance(drone.Location) < home.GetDistance(closest.Location))
                closest = drone;
        }

        if (closest != null && !closest.Vehicle.Mode.Equals("rtl", StringComparison.OrdinalIgnoreCase))
            closest.Vehicle.SetMode("RTL");
    }
}
