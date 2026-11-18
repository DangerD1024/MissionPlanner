using GMap.NET;
using MissionPlanner.App.Core.Mission;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.FlightPlan;

public enum PlanMarkerKind
{
    Waypoint,
    Spline,
    Loiter,
    Takeoff,
    Land,
    LandStart,
    Roi,
    FenceInclusion,
    FenceExclusion,
    FenceReturn,
    Rally,
    Other,
}

/// <summary>A numbered, draggable point of a list on the map.</summary>
public sealed record PlanMarker(MissionItem Item, int Index, PointLatLng Position, PlanMarkerKind Kind, double Radius = 0)
{
    /// <summary>The number shown (1-based; home is 0).</summary>
    public int Number => Index + 1;
}

/// <summary>A polyline of the route; dashed for the legs to and from home.</summary>
public sealed record PlanLine(IReadOnlyList<PointLatLng> Points, bool Dashed);

public sealed record PlanPolygon(IReadOnlyList<PointLatLng> Points, bool Inclusion, FencePolygonGroup Group);

public sealed record PlanCircle(PointLatLng Center, double Radius, bool Inclusion, MissionItem Item, bool AroundHome);

public enum MidpointTarget
{
    Mission,
    FencePolygon,
    DrawPolygon,
}

/// <summary>"+" handle halfway along a leg or edge: clicking or dragging it inserts a point there.</summary>
public sealed class MidpointHandle(PointLatLng position, int insertIndex, MidpointTarget target, FencePolygonGroup group = default)
{
    public PointLatLng Position { get; } = position;

    /// <summary>Index the new point takes in its list.</summary>
    public int InsertIndex { get; } = insertIndex;

    public MidpointTarget Target { get; } = target;

    /// <summary>The fence polygon, for <see cref="MidpointTarget.FencePolygon"/>.</summary>
    public FencePolygonGroup Group { get; } = group;
}

/// <summary>The home marker, as a drag target.</summary>
public sealed class HomeHandle
{
    public static readonly HomeHandle Instance = new();

    private HomeHandle()
    {
    }
}

/// <summary>A vertex of the drawing polygon, as a drag target.</summary>
public sealed record PolygonVertexHandle(int Index);

/// <summary>
/// What the map draws for one list: route lines, numbered markers, fence shapes and insert handles.
/// Built from the store on every change (lists are small); mirrors Mission Planner's WPOverlay rules.
/// </summary>
public sealed class PlanGeometry
{
    public static readonly PlanGeometry Empty = new();

    public List<PlanLine> Lines { get; } = new();

    public List<PlanMarker> Markers { get; } = new();

    public List<PlanPolygon> Polygons { get; } = new();

    public List<PlanCircle> Circles { get; } = new();

    public List<MidpointHandle> Midpoints { get; } = new();

    /// <summary>Length of the drawn route including the legs to and from home, metres.</summary>
    public double TotalDistance { get; private set; }

    /// <summary>The last point the route flies to (home when there is none), for the "Prev" readout.</summary>
    public PointLatLng? LastPoint { get; private set; }

    /// <summary>Waypoint radius for the circles around waypoints, metres.</summary>
    public double WaypointRadius { get; private set; }

    /// <summary>Everything worth zooming to.</summary>
    public IEnumerable<PointLatLng> AllPoints() =>
        Markers.Select(m => m.Position)
            .Concat(Polygons.SelectMany(p => p.Points))
            .Concat(Circles.Select(c => c.Center));

    /// <summary>Navigation commands (the vehicle flies to them), as Mission Planner's map draws them.</summary>
    public static bool IsNavigation(MAVLink.MAV_CMD command) =>
        ((ushort)command < (ushort)MAVLink.MAV_CMD.LAST &&
         command != MAVLink.MAV_CMD.RETURN_TO_LAUNCH &&
         command != MAVLink.MAV_CMD.CONTINUE_AND_CHANGE_ALT &&
         command != MAVLink.MAV_CMD.DELAY &&
         command != MAVLink.MAV_CMD.GUIDED_ENABLE)
        || command is MAVLink.MAV_CMD.DO_SET_ROI or MAVLink.MAV_CMD.DO_LAND_START or MAVLink.MAV_CMD.DO_RETURN_PATH_START;

    private static bool IsLoiter(MAVLink.MAV_CMD c) =>
        c is MAVLink.MAV_CMD.LOITER_UNLIM or MAVLink.MAV_CMD.LOITER_TURNS or MAVLink.MAV_CMD.LOITER_TIME
            or MAVLink.MAV_CMD.LOITER_TO_ALT;

    private static readonly Dictionary<MAVLink.MAV_CMD, bool> HasLocationCache = new();

    /// <summary><see cref="Locationwp.isLocationCommand"/>, cached (it reflects over the enum on every call).</summary>
    public static bool HasLocation(MAVLink.MAV_CMD command)
    {
        lock (HasLocationCache)
        {
            if (!HasLocationCache.TryGetValue(command, out var has))
            {
                has = Locationwp.isLocationCommand((ushort)command);
                HasLocationCache[command] = has;
            }

            return has;
        }
    }

    /// <summary>Route and markers of a mission. <paramref name="home"/> starts the route when set.</summary>
    public static PlanGeometry ForMission(PointLatLng? home, IReadOnlyList<MissionItem> items, double waypointRadius,
        double loiterRadius)
    {
        var g = new PlanGeometry { WaypointRadius = waypointRadius };

        // located point per WP number (0 = home), for DO_JUMP
        var byNumber = new List<PointLatLng?> { home };
        var route = new List<PointLatLng>();
        var firstSegment = true;
        if (home is { } h)
            route.Add(h);

        void Flush(bool returnsHome)
        {
            g.AddSegment(route, home, firstSegment && home != null, returnsHome);
            firstSegment = false;
            route = new List<PointLatLng>();
        }

        for (var a = 0; a < items.Count; a++)
        {
            var item = items[a];
            var cmd = item.Command;
            var located = item.Lat != 0 || item.Lng != 0;
            var p = new PointLatLng(item.Lat, item.Lng);

            if ((ushort)cmd == 0)
            {
                byNumber.Add(null);
                continue;
            }

            if (IsNavigation(cmd))
            {
                if (!located)
                {
                    // takeoff, land, loiter... at the current position: nothing to draw
                    byNumber.Add(null);
                    continue;
                }

                switch (cmd)
                {
                    case MAVLink.MAV_CMD.DO_SET_ROI:
                        // not flown to
                        g.Markers.Add(new PlanMarker(item, a, p, PlanMarkerKind.Roi));
                        byNumber.Add(null);
                        continue;

                    case MAVLink.MAV_CMD.DO_LAND_START:
                        route.Add(p);
                        g.Markers.Add(new PlanMarker(item, a, p, PlanMarkerKind.LandStart, waypointRadius));
                        Flush(returnsHome: false);
                        route.Add(p);
                        break;

                    case MAVLink.MAV_CMD.LAND or MAVLink.MAV_CMD.VTOL_LAND:
                        route.Add(p);
                        g.Markers.Add(new PlanMarker(item, a, p, PlanMarkerKind.Land, waypointRadius));
                        Flush(returnsHome: false);
                        break;

                    default:
                        if (IsLoiter(cmd))
                        {
                            var radius = cmd == MAVLink.MAV_CMD.LOITER_TO_ALT ? item.P2
                                : cmd is MAVLink.MAV_CMD.LOITER_TURNS or MAVLink.MAV_CMD.LOITER_UNLIM ? item.P3 : 0;
                            if (radius == 0)
                                radius = (float)loiterRadius;
                            g.Markers.Add(new PlanMarker(item, a, p, PlanMarkerKind.Loiter, Math.Abs(radius)));
                        }
                        else
                        {
                            var kind = cmd switch
                            {
                                MAVLink.MAV_CMD.SPLINE_WAYPOINT => PlanMarkerKind.Spline,
                                MAVLink.MAV_CMD.TAKEOFF or MAVLink.MAV_CMD.VTOL_TAKEOFF => PlanMarkerKind.Takeoff,
                                _ => PlanMarkerKind.Waypoint,
                            };
                            g.Markers.Add(new PlanMarker(item, a, p, kind, waypointRadius));
                        }

                        route.Add(p);
                        break;
                }

                byNumber.Add(p);
                g.LastPoint = p;
            }
            else if (cmd == MAVLink.MAV_CMD.DO_JUMP)
            {
                byNumber.Add(null);
                var target = (int)Math.Max(item.P1, 0);
                var repeat = Math.Min((int)item.P2, 10);
                for (var r = 0; r < repeat; r++)
                {
                    for (var no = target; no <= a && no < byNumber.Count; no++)
                    {
                        if (byNumber[no] is { } jp)
                            route.Add(jp);
                    }
                }
            }
            else
            {
                // a located DO command (DO_SET_HOME, DO_REPOSITION...) is shown so it can be dragged
                if (located && HasLocation(cmd))
                    g.Markers.Add(new PlanMarker(item, a, p, PlanMarkerKind.Other));
                byNumber.Add(null);
            }
        }

        Flush(returnsHome: true);
        g.LastPoint ??= home;

        // insert handles on each leg the route flies, home's first leg included
        PointLatLng? prev = home;
        foreach (var m in g.Markers)
        {
            if (m.Kind is PlanMarkerKind.Roi or PlanMarkerKind.Other)
                continue;
            if (prev is { } pp && pp != m.Position)
                g.Midpoints.Add(new MidpointHandle(Mid(pp, m.Position), m.Index, MidpointTarget.Mission));
            prev = m.Position;
        }

        return g;
    }

    /// <summary>Fence polygons, circles and the return point.</summary>
    public static PlanGeometry ForFence(PointLatLng? home, IReadOnlyList<MissionItem> items)
    {
        var g = new PlanGeometry();
        foreach (var group in MissionEditing.FencePolygonGroups(items))
        {
            var pts = new List<PointLatLng>();
            for (var k = group.Start; k < group.Start + group.Count; k++)
            {
                var it = items[k];
                var p = new PointLatLng(it.Lat, it.Lng);
                pts.Add(p);
                g.Markers.Add(new PlanMarker(it, k, p, group.Inclusion ? PlanMarkerKind.FenceInclusion : PlanMarkerKind.FenceExclusion));
            }

            g.Polygons.Add(new PlanPolygon(pts, group.Inclusion, group));
            for (var k = 0; k < pts.Count && pts.Count > 1; k++)
            {
                var next = (k + 1) % pts.Count;
                if (pts.Count == 2 && next == 0)
                    break;
                g.Midpoints.Add(new MidpointHandle(Mid(pts[k], pts[next]), group.Start + k + 1, MidpointTarget.FencePolygon, group));
            }
        }

        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            var p = new PointLatLng(it.Lat, it.Lng);
            switch (it.Command)
            {
                case MAVLink.MAV_CMD.FENCE_CIRCLE_INCLUSION or MAVLink.MAV_CMD.FENCE_CIRCLE_EXCLUSION:
                    var inclusion = it.Command == MAVLink.MAV_CMD.FENCE_CIRCLE_INCLUSION;
                    g.Circles.Add(new PlanCircle(p, Math.Abs(it.P1), inclusion, it, false));
                    g.Markers.Add(new PlanMarker(it, i, p, inclusion ? PlanMarkerKind.FenceInclusion : PlanMarkerKind.FenceExclusion));
                    break;
                case MAVLink.MAV_CMD.FENCE_HOME_CIRCLE_INCLUSION:
                    if (home is { } h && it.P1 > 0)
                        g.Circles.Add(new PlanCircle(h, it.P1, true, it, true));
                    break;
                case MAVLink.MAV_CMD.FENCE_RETURN_POINT:
                    g.Markers.Add(new PlanMarker(it, i, p, PlanMarkerKind.FenceReturn));
                    break;
            }
        }

        g.Markers.Sort((x, y) => x.Index.CompareTo(y.Index));
        return g;
    }

    /// <summary>Rally points.</summary>
    public static PlanGeometry ForRally(IReadOnlyList<MissionItem> items)
    {
        var g = new PlanGeometry();
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it.Lat != 0 || it.Lng != 0)
                g.Markers.Add(new PlanMarker(it, i, new PointLatLng(it.Lat, it.Lng), PlanMarkerKind.Rally));
        }

        return g;
    }

    /// <summary>Insert handles on the edges of the drawing polygon (closing edge included).</summary>
    public static List<MidpointHandle> PolygonMidpoints(IReadOnlyList<PointLatLng> points)
    {
        var list = new List<MidpointHandle>();
        if (points.Count < 2)
            return list;
        for (var k = 0; k < points.Count; k++)
        {
            var next = (k + 1) % points.Count;
            if (points.Count == 2 && next == 0)
                break;
            list.Add(new MidpointHandle(Mid(points[k], points[next]), k + 1, MidpointTarget.DrawPolygon));
        }

        return list;
    }

    public static double Distance(PointLatLng a, PointLatLng b) =>
        new PointLatLngAlt(a.Lat, a.Lng).GetDistance(new PointLatLngAlt(b.Lat, b.Lng));

    public static double Bearing(PointLatLng a, PointLatLng b) =>
        new PointLatLngAlt(a.Lat, a.Lng).GetBearing(new PointLatLngAlt(b.Lat, b.Lng));

    private static PointLatLng Mid(PointLatLng a, PointLatLng b) => new((a.Lat + b.Lat) / 2, (a.Lng + b.Lng) / 2);

    /// <summary>
    /// One stretch of route. A stretch that starts at home and returns there gets dashed legs
    /// home→first and last→home (Mission Planner's "home route"); one that ends in a landing is solid.
    /// </summary>
    private void AddSegment(List<PointLatLng> points, PointLatLng? home, bool startsAtHome, bool returnsHome)
    {
        if (points.Count == 0)
            return;

        if (returnsHome && home is { } h)
        {
            var body = startsAtHome ? points.Skip(1).ToList() : points;
            if (body.Count == 0)
                return;
            if (startsAtHome)
                AddLine([h, body[0]], dashed: true);
            if (body.Count > 1)
                AddLine(body, dashed: false);
            AddLine([body[^1], h], dashed: true);
            return;
        }

        if (points.Count > 1)
            AddLine(points, dashed: false);
    }

    private void AddLine(List<PointLatLng> points, bool dashed)
    {
        if (points.Count < 2)
            return;
        Lines.Add(new PlanLine(points, dashed));
        for (var i = 1; i < points.Count; i++)
            TotalDistance += Distance(points[i - 1], points[i]);
    }
}
