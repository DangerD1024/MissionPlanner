using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.Swarm;

/// <summary>
/// Mission Planner's Follow the Leader (<c>FollowPath</c>): the leader's track is recorded and the other
/// vehicles fly along it behind the leader, one every <see cref="FollowDistance"/> metres, sent as guided
/// waypoints at 5 Hz.
/// </summary>
/// <remarks>
/// Mission Planner walks the trail from its first point and sends every follower the same point; this
/// walks back from the leader's newest point and gives each follower its own place in the line, which is
/// what the tool is meant to do.
/// </remarks>
public sealed class FollowPathSwarm : SwarmBase
{
    /// <summary>Most points kept of the leader's track.</summary>
    public const int MaxTrail = 20000;

    /// <summary>Most places in the line (Mission Planner plans 20).</summary>
    public const int MaxPlaces = 20;

    private readonly List<PointLatLngAlt> _trail = new();

    /// <summary>Gap between vehicles along the track, metres (Mission Planner: 2).</summary>
    public double FollowDistance { get; set; } = 2;

    public int TrailCount
    {
        get
        {
            lock (_trail)
                return _trail.Count;
        }
    }

    public void ClearTrail()
    {
        lock (_trail)
            _trail.Clear();
    }

    /// <summary>Adds the leader's position to the track (skips it when the leader has not moved).</summary>
    public void Record(PointLatLngAlt p)
    {
        lock (_trail)
        {
            if (_trail.Count > 0 && _trail[^1].GetDistance(p) < 0.1 && Math.Abs(_trail[^1].Alt - p.Alt) < 0.1)
                return;
            _trail.Add(new PointLatLngAlt(p));
            if (_trail.Count > MaxTrail)
                _trail.RemoveRange(0, _trail.Count - MaxTrail);
        }
    }

    /// <summary>The places behind the leader along its track, nearest first.</summary>
    public List<PointLatLngAlt> PlanMove()
    {
        var result = new List<PointLatLngAlt>();
        lock (_trail)
        {
            if (_trail.Count == 0)
                return result;

            var current = _trail[^1];
            for (var i = _trail.Count - 2; i >= 0 && result.Count < MaxPlaces; i--)
            {
                if (current.GetDistance(_trail[i]) > FollowDistance)
                {
                    result.Add(_trail[i]);
                    current = _trail[i];
                }
            }
        }

        return result;
    }

    public override void Step(IReadOnlyList<ISwarmVehicle> vehicles)
    {
        if (Leader == null || !Leader.HasPosition())
            return;

        Record(Leader.Position());

        var places = PlanMove();
        var a = 0;
        foreach (var v in vehicles)
        {
            if (v == Leader)
                continue;
            if (a >= places.Count)
                break;
            var target = places[a++];
            try
            {
                v.GuidedWaypoint(target.Lat, target.Lng, target.Alt);
            }
            catch (Exception ex)
            {
                log4net.LogManager.GetLogger(typeof(FollowPathSwarm)).Warn("follow: " + v.Name, ex);
            }
        }
    }
}
