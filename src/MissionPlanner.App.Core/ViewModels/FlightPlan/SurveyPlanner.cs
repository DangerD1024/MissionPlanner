using GMap.NET;
using MissionPlanner.App.Core.Mission;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.FlightPlan;

/// <summary>Figures for the generated survey. Distances in metres.</summary>
public sealed record SurveyStats(double AreaM2, double DistanceM, int Lanes, int Images, TimeSpan FlightTime)
{
    public static readonly SurveyStats Empty = new(0, 0, 0, 0, TimeSpan.Zero);
}

/// <summary>What the dialog's map draws: lanes, the turns between them and the legs to and from home.</summary>
public sealed class SurveyPreview
{
    public static readonly SurveyPreview Empty = new();

    /// <summary>Camera sections of the lanes (SM→ME).</summary>
    public List<(PointLatLng A, PointLatLng B)> Lanes { get; } = new();

    /// <summary>Lead-ins and overshoots (S→SM, ME→E).</summary>
    public List<(PointLatLng A, PointLatLng B)> Extensions { get; } = new();

    /// <summary>Turns from one lane end to the next lane start.</summary>
    public List<(PointLatLng A, PointLatLng B)> Transits { get; } = new();

    /// <summary>Home to the first point and the last point back to home.</summary>
    public List<(PointLatLng A, PointLatLng B)> HomeLegs { get; } = new();

    /// <summary>Camera trigger positions ("M").</summary>
    public List<PointLatLng> Triggers { get; } = new();

    public PointLatLng? Start { get; set; }

    public PointLatLng? End { get; set; }

    /// <summary>Everything worth fitting on screen (polygon and route).</summary>
    public List<PointLatLng> Extent { get; } = new();
}

/// <summary>
/// The pure logic of the Survey (Grid) dialog: grid generation through <see cref="Grid"/>, its
/// conversion to mission items as Mission Planner's GridUI does on Accept, and the statistics.
/// </summary>
public static class SurveyPlanner
{
    /// <summary>Above this many lanes (or trigger points) generation is refused: the generator is quadratic in lanes.</summary>
    public const int MaxLanes = 2000;

    public const int MaxTriggers = 100_000;

    /// <summary>Mission Planner estimates flight time at 80 % of the set speed, for the turns.</summary>
    public const double SpeedFactor = 0.8;

    // Grid.StartPointLatLngAlt is a static that the crossgrid pass relies on
    private static readonly object GridLock = new();

    /// <summary>
    /// Generates the grid (plus the crossgrid pass when enabled). Points are tagged S / SM / M / ME / E
    /// and carry the survey altitude. Blocks; run it off the UI thread.
    /// </summary>
    public static List<PointLatLngAlt> Generate(IReadOnlyList<PointLatLngAlt> polygon, SurveyOptions o, PointLatLngAlt home)
    {
        if (polygon.Count < 3)
            return new List<PointLatLngAlt>();

        var check = CheckSize(polygon, o);
        if (check != null)
            throw new SurveyTooLargeException(check);

        var poly = polygon.Select(p => new PointLatLngAlt(p.Lat, p.Lng, 0)).ToList();
        var spacing = o.UsesCamera ? o.TriggerDistance : 0;
        var angle = EffectiveAngle(polygon, o);

        lock (GridLock)
        {
            var grid = Grid.CreateGrid(poly, o.Altitude, o.LaneSpacing, spacing, angle, o.Overshoot1, o.Overshoot2,
                o.StartFrom, false, (float)o.MinLaneSeparation, (float)o.LeadIn1, (float)o.LeadIn2, home);

            if (o.Crossgrid && grid.Count > 0)
            {
                Grid.StartPointLatLngAlt = grid[^1];
                grid.AddRange(Grid.CreateGrid(poly, o.Altitude, o.LaneSpacing, spacing, angle + 90, o.Overshoot1, o.Overshoot2,
                    Grid.StartPosition.Point, false, (float)o.MinLaneSeparation, (float)o.LeadIn1, (float)o.LeadIn2, home));
            }

            return grid;
        }
    }

    /// <summary>The angle the lanes use: the longest edge's when <see cref="SurveyOptions.AutoAngle"/> is set.</summary>
    public static double EffectiveAngle(IReadOnlyList<PointLatLngAlt> polygon, SurveyOptions o) =>
        o.AutoAngle ? AutoAngle(polygon) : o.Angle;

    /// <summary>
    /// Bearing of the polygon's longest edge (the closing edge included), in [0, 180): lanes along
    /// it need the fewest turns. As Mission Planner's getAngleOfLongestSide, but measured on the
    /// UTM grid the generator lays the lanes on (which is turned from true north away from the
    /// zone's central meridian), so the lanes come out parallel to the edge.
    /// </summary>
    public static double AutoAngle(IReadOnlyList<PointLatLngAlt> polygon)
    {
        if (polygon.Count < 2)
            return 0;
        var zone = polygon[0].GetUTMZone();
        var utm = PointLatLngAlt.ToUTM(zone, polygon.Select(p => new PointLatLngAlt(p.Lat, p.Lng)).ToList());
        double best = -1, angle = 0;
        var last = utm[^1];
        foreach (var p in utm)
        {
            double dx = p[0] - last[0], dy = p[1] - last[1];
            var d = dx * dx + dy * dy;
            if (d > best)
            {
                best = d;
                angle = Math.Atan2(dx, dy) * 180 / Math.PI;
            }

            last = p;
        }

        angle %= 180;
        if (angle < 0)
            angle += 180;
        return Math.Round(angle, 2) % 180;
    }

    /// <summary>Mean of the vertices: the stand-in home when neither a planned nor a vehicle home is known.</summary>
    public static PointLatLngAlt Centroid(IReadOnlyList<PointLatLngAlt> polygon) =>
        polygon.Count == 0
            ? new PointLatLngAlt()
            : new PointLatLngAlt(polygon.Average(p => p.Lat), polygon.Average(p => p.Lng), 0);

    /// <summary>Null when the inputs are workable, else why not (too many lanes or trigger points).</summary>
    public static string? CheckSize(IReadOnlyList<PointLatLngAlt> polygon, SurveyOptions o)
    {
        if (o.LaneSpacing <= 0)
            return "Lane spacing must be above 0.";
        var diagonal = Diagonal(polygon);
        var lanes = diagonal / o.LaneSpacing * (o.Crossgrid ? 2 : 1);
        if (lanes > MaxLanes)
            return $"Lane spacing is too small for this area (about {lanes:0} lanes).";
        if (o.UsesCamera)
        {
            var triggers = Area(polygon) / (o.LaneSpacing * o.TriggerDistance) * (o.Crossgrid ? 2 : 1);
            if (triggers > MaxTriggers)
                return $"Trigger distance is too small for this area (about {triggers:0} photos).";
        }

        return null;
    }

    public static double Area(IReadOnlyList<PointLatLngAlt> polygon) =>
        MissionEditing.PolygonArea(polygon.Select(p => new PointLatLng(p.Lat, p.Lng)).ToList());

    /// <summary>
    /// Mission items for a generated grid, following GridUI's Accept for its "Trigger distance" mode
    /// without stop/start: optional TAKEOFF and DO_CHANGE_SPEED; the first point; every lane start (S)
    /// and end (E); with the camera, DO_SET_CAM_TRIGG_DIST after the first waypoint, the camera
    /// section ends (ME) as waypoints and the trigger off at the end; the speed back to
    /// <paramref name="restoreSpeed"/>; then RTL or LAND at home. A waypoint at the position of the
    /// previous one is skipped.
    /// </summary>
    public static List<MissionItem> ToMissionItems(IReadOnlyList<PointLatLngAlt> grid, SurveyOptions o,
        PointLatLngAlt home, double? restoreSpeed = null)
    {
        var items = new List<MissionItem>();
        if (grid.Count == 0)
            return items;

        void Add(MAVLink.MAV_CMD cmd, double p1 = 0, double p2 = 0, double p3 = 0, double lat = 0, double lng = 0, double alt = 0) =>
            items.Add(new MissionItem
            {
                Command = cmd,
                P1 = (float)p1,
                P2 = (float)p2,
                P3 = (float)p3,
                Lat = lat,
                Lng = lng,
                Alt = (float)alt,
                Frame = o.Frame,
            });

        PointLatLngAlt? lastWp = null;

        void Waypoint(PointLatLngAlt p)
        {
            if (lastWp != null && lastWp.Lat == p.Lat && lastWp.Lng == p.Lng && lastWp.Alt == p.Alt)
                return;
            var spline = o.UseSpline && p.Tag is "S" or "SM";
            Add(spline ? MAVLink.MAV_CMD.SPLINE_WAYPOINT : MAVLink.MAV_CMD.WAYPOINT, lat: p.Lat, lng: p.Lng, alt: p.Alt);
            lastWp = p;
        }

        if (o.AddTakeoff)
            Add(MAVLink.MAV_CMD.TAKEOFF, p1: 20, alt: o.TakeoffAltitude);

        if (o.ChangeSpeed && o.Speed > 0)
            Add(MAVLink.MAV_CMD.DO_CHANGE_SPEED, p2: o.Speed);

        var camera = o.UsesCamera;
        var triggerStarted = false;
        for (var i = 0; i < grid.Count; i++)
        {
            var p = grid[i];
            if (i == 0)
            {
                Waypoint(p);
                continue;
            }

            if (p.Tag == "M")
                continue;

            if (p.Tag is "S" or "E")
                Waypoint(p);

            if (!camera)
                continue;

            if (!triggerStarted)
            {
                Add(MAVLink.MAV_CMD.DO_SET_CAM_TRIGG_DIST, p1: o.TriggerDistance, p3: 1);
                triggerStarted = true;
            }
            else if (p.Tag == "ME")
            {
                Waypoint(p);
            }
        }

        if (camera)
            Add(MAVLink.MAV_CMD.DO_SET_CAM_TRIGG_DIST, p1: 0, p3: 1);

        if (o.ChangeSpeed && restoreSpeed is > 0)
            Add(MAVLink.MAV_CMD.DO_CHANGE_SPEED, p2: restoreSpeed.Value);

        switch (o.Finish)
        {
            case SurveyFinish.ReturnToLaunch:
                Add(MAVLink.MAV_CMD.RETURN_TO_LAUNCH);
                break;
            case SurveyFinish.LandAtHome:
                Add(MAVLink.MAV_CMD.LAND, lat: home.Lat, lng: home.Lng, alt: 0);
                break;
        }

        return items;
    }

    /// <summary>
    /// Area of the polygon; the route length from home through every grid point and back to home
    /// (as GridUI); lanes; photos (trigger points, when the camera is on); flight time at
    /// <see cref="SpeedFactor"/> of the speed.
    /// </summary>
    public static SurveyStats ComputeStats(IReadOnlyList<PointLatLngAlt> polygon, IReadOnlyList<PointLatLngAlt> grid,
        PointLatLngAlt home, SurveyOptions o)
    {
        var area = Area(polygon);
        if (grid.Count == 0)
            return SurveyStats.Empty with { AreaM2 = area };

        var distance = home.GetDistance(grid[0]) + grid[^1].GetDistance(home);
        for (var i = 1; i < grid.Count; i++)
            distance += grid[i - 1].GetDistance(grid[i]);

        var lanes = grid.Count(p => p.Tag == "S");
        var images = o.UsesCamera ? grid.Count(p => p.Tag == "M") : 0;
        var time = o.Speed > 0 ? TimeSpan.FromSeconds(distance / (o.Speed * SpeedFactor)) : TimeSpan.Zero;
        return new SurveyStats(area, distance, lanes, images, time);
    }

    /// <summary>Lines and markers for the map.</summary>
    public static SurveyPreview BuildPreview(IReadOnlyList<PointLatLngAlt> polygon, IReadOnlyList<PointLatLngAlt> grid,
        PointLatLngAlt? home)
    {
        var preview = new SurveyPreview();
        preview.Extent.AddRange(polygon.Select(p => new PointLatLng(p.Lat, p.Lng)));
        if (grid.Count == 0)
            return preview;

        static PointLatLng P(PointLatLngAlt p) => new(p.Lat, p.Lng);

        PointLatLngAlt? prev = null;
        foreach (var p in grid)
        {
            if (p.Tag == "M")
            {
                preview.Triggers.Add(P(p));
                continue;
            }

            if (prev != null && (prev.Lat != p.Lat || prev.Lng != p.Lng))
            {
                var segment = (P(prev), P(p));
                switch (prev.Tag, p.Tag)
                {
                    case ("SM", "ME"):
                        preview.Lanes.Add(segment);
                        break;
                    case ("S", "SM") or ("ME", "E"):
                        preview.Extensions.Add(segment);
                        break;
                    default:
                        preview.Transits.Add(segment);
                        break;
                }
            }

            prev = p;
            preview.Extent.Add(P(p));
        }

        preview.Start = P(grid[0]);
        preview.End = P(grid[^1]);
        if (home != null && (home.Lat != 0 || home.Lng != 0))
        {
            preview.HomeLegs.Add((P(home), P(grid[0])));
            preview.HomeLegs.Add((P(grid[^1]), P(home)));
        }

        return preview;
    }

    private static double Diagonal(IReadOnlyList<PointLatLngAlt> polygon)
    {
        if (polygon.Count < 2)
            return 0;
        var sw = new PointLatLngAlt(polygon.Min(p => p.Lat), polygon.Min(p => p.Lng));
        var ne = new PointLatLngAlt(polygon.Max(p => p.Lat), polygon.Max(p => p.Lng));
        return sw.GetDistance(ne);
    }
}

/// <summary>The inputs would make an unreasonably large grid.</summary>
public sealed class SurveyTooLargeException(string message) : Exception(message);
