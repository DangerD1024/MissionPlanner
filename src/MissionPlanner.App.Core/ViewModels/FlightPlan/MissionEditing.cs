using System.Collections.ObjectModel;
using System.Globalization;
using GMap.NET;
using MissionPlanner.App.Core.Mission;

namespace MissionPlanner.App.Core.ViewModels.FlightPlan;

/// <summary>Consecutive fence vertices that form one polygon: <see cref="Count"/> items from <see cref="Start"/>.</summary>
public readonly record struct FencePolygonGroup(int Start, int Count, MAVLink.MAV_CMD Command)
{
    public bool Contains(int index) => index >= Start && index < Start + Count;

    public bool Inclusion => Command == MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION;
}

/// <summary>
/// List edits of the Flight Plan screen that are more than one collection call: moving rows,
/// keeping fence polygon vertex counts right, choosing where an inserted waypoint goes, and the
/// drawing polygon (area, .poly files). Pure logic, unit tested.
/// </summary>
public static class MissionEditing
{
    private const double EarthRadius = 6378137.0;

    public static bool IsFenceVertex(MAVLink.MAV_CMD command) =>
        command is MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION or MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_EXCLUSION;

    /// <summary>Moves a row up (delta -1) or down (+1). Returns false at either end.</summary>
    public static bool Move(ObservableCollection<MissionItem> list, MissionItem item, int delta)
    {
        var index = list.IndexOf(item);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= list.Count)
            return false;
        list.Move(index, target);
        return true;
    }

    /// <summary>
    /// Splits a fence list into its polygons, as ArduPilot reads them: consecutive vertices of the
    /// same command, <c>p1</c> of the first being the vertex count. A run that ends early (another
    /// command, end of list) is a polygon of what it has.
    /// </summary>
    public static List<FencePolygonGroup> FencePolygonGroups(IReadOnlyList<MissionItem> items)
    {
        var groups = new List<FencePolygonGroup>();
        var i = 0;
        while (i < items.Count)
        {
            var cmd = items[i].Command;
            if (!IsFenceVertex(cmd))
            {
                i++;
                continue;
            }

            var wanted = Math.Max(1, (int)Math.Round(items[i].P1));
            var count = 0;
            while (i + count < items.Count && count < wanted && items[i + count].Command == cmd)
                count++;
            groups.Add(new FencePolygonGroup(i, count, cmd));
            i += count;
        }

        return groups;
    }

    /// <summary>Removes a row; a fence vertex takes its polygon's vertex count down with it.</summary>
    public static void Delete(ObservableCollection<MissionItem> list, MissionItem item)
    {
        var index = list.IndexOf(item);
        if (index < 0)
            return;

        FencePolygonGroup? group = null;
        if (IsFenceVertex(item.Command))
            group = FencePolygonGroups(list).FirstOrDefault(g => g.Contains(index));

        list.RemoveAt(index);

        if (group is { } g && g.Count > 1)
        {
            for (var k = g.Start; k < g.Start + g.Count - 1; k++)
                list[k].P1 = g.Count - 1;
        }
    }

    /// <summary>Adds a vertex to a fence polygon at <paramref name="insertIndex"/> and updates the polygon's vertex count.</summary>
    public static MissionItem InsertFenceVertex(ObservableCollection<MissionItem> list, FencePolygonGroup group, int insertIndex,
        double lat, double lng)
    {
        insertIndex = Math.Clamp(insertIndex, group.Start, group.Start + group.Count);
        var item = new MissionItem
        {
            Command = group.Command,
            Lat = lat,
            Lng = lng,
            Frame = AltFrame.Absolute,
            P1 = group.Count + 1,
        };
        list.Insert(insertIndex, item);
        for (var k = group.Start; k <= group.Start + group.Count; k++)
            list[k].P1 = group.Count + 1;
        return item;
    }

    /// <summary>Fence vertices for a drawn polygon (one polygon: p1 = vertex count).</summary>
    public static List<MissionItem> FencePolygon(IReadOnlyList<PointLatLng> points, bool inclusion)
    {
        var cmd = inclusion ? MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION : MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_EXCLUSION;
        return points.Select(p => new MissionItem
        {
            Command = cmd,
            P1 = points.Count,
            Lat = p.Lat,
            Lng = p.Lng,
            Frame = AltFrame.Absolute,
        }).ToList();
    }

    /// <summary>True for items the route flies through (a leg can be split by inserting before them).</summary>
    public static bool IsRoutePoint(MissionItem item)
    {
        if (item.Lat == 0 && item.Lng == 0)
            return false;
        return PlanGeometry.IsNavigation(item.Command) && item.Command != MAVLink.MAV_CMD.DO_SET_ROI;
    }

    /// <summary>
    /// Where a waypoint inserted at <paramref name="point"/> belongs: the index of the item that ends the
    /// nearest leg of the route (home to the first point counts), or the list's end when the point lies
    /// beyond the last one.
    /// </summary>
    public static int InsertIndexForPoint(PointLatLng? home, IReadOnlyList<MissionItem> items, PointLatLng point)
    {
        var stops = new List<(PointLatLng Pos, int Index)>();
        if (home is { } h)
            stops.Add((h, -1));
        for (var i = 0; i < items.Count; i++)
        {
            if (IsRoutePoint(items[i]))
                stops.Add((new PointLatLng(items[i].Lat, items[i].Lng), i));
        }

        if (stops.Count < 2)
            return items.Count;

        var best = double.MaxValue;
        var bestIndex = items.Count;
        for (var s = 1; s < stops.Count; s++)
        {
            var (d, t) = DistanceToSegment(point, stops[s - 1].Pos, stops[s].Pos);
            if (d < best)
            {
                best = d;
                // beyond the end of the last leg: append
                bestIndex = s == stops.Count - 1 && t >= 1 ? items.Count : stops[s].Index;
            }
        }

        return bestIndex;
    }

    /// <summary>Ground distance (m) from a point to a segment, and where along it (0..1) the nearest point is.</summary>
    public static (double Distance, double T) DistanceToSegment(PointLatLng p, PointLatLng a, PointLatLng b)
    {
        // local flat projection around p is plenty for picking a leg
        var cos = Math.Cos(p.Lat * Math.PI / 180);
        double X(PointLatLng q) => (q.Lng - p.Lng) * Math.PI / 180 * EarthRadius * cos;
        double Y(PointLatLng q) => (q.Lat - p.Lat) * Math.PI / 180 * EarthRadius;

        double ax = X(a), ay = Y(a), bx = X(b), by = Y(b);
        double dx = bx - ax, dy = by - ay;
        var len2 = dx * dx + dy * dy;
        var t = len2 <= 1e-9 ? 0 : -(ax * dx + ay * dy) / len2;
        var tc = Math.Clamp(t, 0, 1);
        double nx = ax + tc * dx, ny = ay + tc * dy;
        return (Math.Sqrt(nx * nx + ny * ny), t);
    }

    /// <summary>Area of a polygon in square metres (flat projection around its centre; fine for survey sizes).</summary>
    public static double PolygonArea(IReadOnlyList<PointLatLng> points)
    {
        if (points.Count < 3)
            return 0;
        var lat0 = points.Average(p => p.Lat);
        var lng0 = points.Average(p => p.Lng);
        var cos = Math.Cos(lat0 * Math.PI / 180);
        double sum = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            double ax = (a.Lng - lng0) * cos, ay = a.Lat - lat0;
            double bx = (b.Lng - lng0) * cos, by = b.Lat - lat0;
            sum += ax * by - bx * ay;
        }

        var scale = Math.PI / 180 * EarthRadius;
        return Math.Abs(sum) / 2 * scale * scale;
    }

    public static string FormatArea(double squareMetres) =>
        squareMetres >= 100_000
            ? (squareMetres / 1_000_000).ToString("0.00", CultureInfo.InvariantCulture) + " km²"
            : squareMetres >= 10_000
                ? (squareMetres / 10_000).ToString("0.00", CultureInfo.InvariantCulture) + " ha"
                : squareMetres.ToString("0", CultureInfo.InvariantCulture) + " m²";

    public static string FormatDistance(double metres) =>
        metres >= 1000
            ? (metres / 1000).ToString("0.00", CultureInfo.InvariantCulture) + " km"
            : metres.ToString("0", CultureInfo.InvariantCulture) + " m";

    // ---- .poly files (Mission Planner's polygon format) -------------------------------------

    /// <summary>Writes "lat lng" per line after a comment, closing the loop with the first point, as Mission Planner does.</summary>
    public static void WritePolygonFile(string path, IReadOnlyList<PointLatLng> points)
    {
        var inv = CultureInfo.InvariantCulture;
        using var w = new StreamWriter(path);
        w.WriteLine("#saved by Mission Planner");
        foreach (var p in points)
            w.WriteLine(p.Lat.ToString(inv) + " " + p.Lng.ToString(inv));
        if (points.Count > 0)
            w.WriteLine(points[0].Lat.ToString(inv) + " " + points[0].Lng.ToString(inv));
    }

    /// <summary>Reads a .poly file; comment lines start with #, the closing duplicate is dropped.</summary>
    public static List<PointLatLng> ReadPolygonFile(string path)
    {
        var points = new List<PointLatLng>();
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var parts = line.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 ||
                !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lng))
                continue;
            points.Add(new PointLatLng(lat, lng));
        }

        if (points.Count > 1 && points[0] == points[^1])
            points.RemoveAt(points.Count - 1);
        return points;
    }
}
