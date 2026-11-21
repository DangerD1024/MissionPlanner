using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using GMap.NET;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.Mission;

/// <summary>A shape read from a KML or shapefile: points, a line or a closed polygon, in lat/lng (alt when known).</summary>
public sealed record PlanShape(string Kind, IReadOnlyList<(double Lat, double Lng, double? Alt)> Points)
{
    public bool IsPolygon => Kind == "Polygon";
}

/// <summary>Mission Planner's other plan files: .fen, .ral, .plan/.mission (JSON), KML and ESRI shapefiles.</summary>
public static class PlanFiles
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---- .fen: "#" comments, then the return point, then the polygon (closed by repeating the first vertex)

    public static (PointLatLng? Return, List<PointLatLng> Polygon) ReadFence(IEnumerable<string> lines)
    {
        PointLatLng? ret = null;
        var poly = new List<PointLatLng>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var items = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (items.Length < 2 || !double.TryParse(items[0], NumberStyles.Float, Inv, out var lat) ||
                !double.TryParse(items[1], NumberStyles.Float, Inv, out var lng))
                continue;
            if (ret == null)
                ret = new PointLatLng(lat, lng);
            else
                poly.Add(new PointLatLng(lat, lng));
        }

        if (poly.Count > 1 && poly[0] == poly[^1])
            poly.RemoveAt(poly.Count - 1);
        return (ret, poly);
    }

    public static string WriteFence(PointLatLng returnPoint, IReadOnlyList<PointLatLng> polygon)
    {
        var sb = new StringBuilder("#saved by Mission Planner 3\n");
        void Line(PointLatLng p) => sb.Append(p.Lat.ToString("0.0000000", Inv)).Append(' ').Append(p.Lng.ToString("0.0000000", Inv)).Append('\n');
        Line(returnPoint);
        foreach (var p in polygon)
            Line(p);
        if (polygon.Count > 0)
            Line(polygon[0]);
        return sb.ToString();
    }

    // ---- .ral: RALLY lat lng alt break_alt land_dir flags

    public static List<(double Lat, double Lng, double Alt)> ReadRally(IEnumerable<string> lines)
    {
        var list = new List<(double, double, double)>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var items = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (items.Length < 4 || !items[0].StartsWith("RALLY", StringComparison.OrdinalIgnoreCase) ||
                !double.TryParse(items[1], NumberStyles.Float, Inv, out var lat) ||
                !double.TryParse(items[2], NumberStyles.Float, Inv, out var lng) ||
                !double.TryParse(items[3], NumberStyles.Float, Inv, out var alt))
                continue;
            list.Add((lat, lng, alt));
        }

        return list;
    }

    public static string WriteRally(IEnumerable<(double Lat, double Lng, double Alt)> points, double breakAlt = 40)
    {
        var sb = new StringBuilder("#saved by Mission Planner 3\n");
        foreach (var (lat, lng, alt) in points)
            sb.Append(string.Create(Inv, $"RALLY\t{lat:0.0000000}\t{lng:0.0000000}\t{alt:0.##}\t{breakAlt:0.##}\t0\t0\n"));
        return sb.ToString();
    }

    // ---- .plan / .mission: the JSON Mission Planner and QGroundControl share

    /// <summary>Home and the mission items of a .plan / .mission file.</summary>
    public static List<Locationwp> ReadPlan(string path) => MissionFile.ConvertToLocationwps(MissionFile.ReadFile(path));

    public static void WritePlan(string path, Locationwp home, IEnumerable<Locationwp> mission)
    {
        var list = new List<Locationwp> { home };
        list.AddRange(mission);
        var root = MissionFile.ConvertFromLocationwps(list);
        // what QGroundControl (and the library's own reader) require, which the library's conversion leaves out
        root.fileType = "Plan";
        root.groundStation = "MissionPlanner";
        var seq = 1;
        foreach (var item in root.mission.items)
        {
            item.type = "SimpleItem";
            item.autoContinue = true;
            item.doJumpId = seq++;
        }

        root.mission.firmwareType = 3; // ArduPilot
        root.mission.vehicleType = 2; // QGC only uses it for defaults
        root.mission.cruiseSpeed = 15;
        root.mission.hoverSpeed = 5;
        root.mission.version = 2;
        root.geoFence ??= new MissionFile.GeoFence { circles = [], polygons = [], version = 2 };
        root.rallyPoints ??= new MissionFile.RallyPoints { points = [], version = 2 };
        MissionFile.WriteFile(path, root);
    }

    // ---- KML

    /// <summary>The points, lines and polygons of a KML document (any namespace version).</summary>
    public static List<PlanShape> ReadKml(string xml)
    {
        var doc = XDocument.Parse(xml);
        var shapes = new List<PlanShape>();
        foreach (var el in doc.Descendants())
        {
            var kind = el.Name.LocalName;
            if (kind is not ("Point" or "LineString" or "Polygon" or "LinearRing"))
                continue;
            if (kind == "LinearRing")
            {
                // a polygon's ring is read with its polygon (outer boundary only)
                if (el.Parent?.Name.LocalName is "outerBoundaryIs" or "innerBoundaryIs")
                    continue;
            }

            var coords = (kind == "Polygon"
                    ? el.Descendants().FirstOrDefault(e => e.Name.LocalName == "outerBoundaryIs")?.Descendants()
                        .FirstOrDefault(e => e.Name.LocalName == "coordinates")
                    : el.Elements().FirstOrDefault(e => e.Name.LocalName == "coordinates"))?.Value;
            if (coords == null)
                continue;
            var pts = ParseKmlCoordinates(coords);
            if (pts.Count == 0)
                continue;
            if (kind is "Polygon" or "LinearRing" && pts.Count > 1 && pts[0].Lat == pts[^1].Lat && pts[0].Lng == pts[^1].Lng)
                pts.RemoveAt(pts.Count - 1);
            shapes.Add(new PlanShape(kind == "LinearRing" ? "Polygon" : kind, pts));
        }

        return shapes;
    }

    private static List<(double Lat, double Lng, double? Alt)> ParseKmlCoordinates(string text)
    {
        var list = new List<(double, double, double?)>();
        foreach (var tuple in text.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var p = tuple.Split(',');
            if (p.Length < 2 || !double.TryParse(p[0], NumberStyles.Float, Inv, out var lng) ||
                !double.TryParse(p[1], NumberStyles.Float, Inv, out var lat))
                continue;
            double? alt = p.Length > 2 && double.TryParse(p[2], NumberStyles.Float, Inv, out var a) ? a : null;
            list.Add((lat, lng, alt));
        }

        return list;
    }

    // ---- ESRI shapefile (.shp): points, polylines and polygons in geographic coordinates

    /// <summary>
    /// The shapes of a .shp file. Coordinates must be WGS84 longitude/latitude (a .prj next to it
    /// saying otherwise is reported by <see cref="ShapefileProjectionProblem"/>).
    /// </summary>
    public static List<PlanShape> ReadShapefile(byte[] shp)
    {
        if (shp.Length < 100 || BinaryPrimitives.ReadInt32BigEndian(shp.AsSpan(0, 4)) != 9994)
            throw new InvalidDataException("Not an ESRI shapefile.");
        var shapes = new List<PlanShape>();
        var pos = 100;
        while (pos + 8 <= shp.Length)
        {
            var contentWords = BinaryPrimitives.ReadInt32BigEndian(shp.AsSpan(pos + 4, 4));
            var start = pos + 8;
            var end = start + contentWords * 2;
            if (end > shp.Length || contentWords < 2)
                break;
            var type = BinaryPrimitives.ReadInt32LittleEndian(shp.AsSpan(start, 4));
            switch (type)
            {
                case 1 or 11 or 21: // point, pointZ, pointM
                {
                    var x = BitConverter.ToDouble(shp, start + 4);
                    var y = BitConverter.ToDouble(shp, start + 12);
                    double? z = type == 11 && end - start >= 28 ? BitConverter.ToDouble(shp, start + 20) : null;
                    shapes.Add(new PlanShape("Point", [(y, x, z)]));
                    break;
                }

                case 3 or 5 or 13 or 15 or 23 or 25: // polyline / polygon (plain, Z, M)
                {
                    var numParts = BinaryPrimitives.ReadInt32LittleEndian(shp.AsSpan(start + 36, 4));
                    var numPoints = BinaryPrimitives.ReadInt32LittleEndian(shp.AsSpan(start + 40, 4));
                    var parts = new int[numParts];
                    for (var i = 0; i < numParts; i++)
                        parts[i] = BinaryPrimitives.ReadInt32LittleEndian(shp.AsSpan(start + 44 + i * 4, 4));
                    var pointsAt = start + 44 + numParts * 4;
                    var polygon = type is 5 or 15 or 25;
                    for (var part = 0; part < numParts; part++)
                    {
                        var from = parts[part];
                        var to = part + 1 < numParts ? parts[part + 1] : numPoints;
                        var pts = new List<(double, double, double?)>();
                        for (var i = from; i < to; i++)
                        {
                            var x = BitConverter.ToDouble(shp, pointsAt + i * 16);
                            var y = BitConverter.ToDouble(shp, pointsAt + i * 16 + 8);
                            pts.Add((y, x, null));
                        }

                        if (polygon && pts.Count > 1 && pts[0] == pts[^1])
                            pts.RemoveAt(pts.Count - 1);
                        if (pts.Count > 0)
                            shapes.Add(new PlanShape(polygon ? "Polygon" : "LineString", pts));
                    }

                    break;
                }
            }

            pos = end;
        }

        return shapes;
    }

    /// <summary>Why a shapefile's projection cannot be used (it is not geographic WGS84), or null.</summary>
    public static string? ShapefileProjectionProblem(string? prjText)
    {
        if (string.IsNullOrWhiteSpace(prjText))
            return null;
        return prjText.TrimStart().StartsWith("PROJCS", StringComparison.OrdinalIgnoreCase)
            ? "The shapefile is in a projected coordinate system; save it as WGS84 (EPSG:4326) longitude/latitude first."
            : null;
    }

    // ---- polygon offset

    /// <summary>The polygon grown (positive) or shrunk (negative) by <paramref name="metres"/>, corners mitred.</summary>
    public static List<PointLatLng> Offset(IReadOnlyList<PointLatLng> polygon, double metres)
    {
        if (polygon.Count < 3)
            return polygon.ToList();
        const double earth = 6378137.0;
        var lat0 = polygon.Average(p => p.Lat) * Math.PI / 180;
        var lng0 = polygon.Average(p => p.Lng);
        var latc = polygon.Average(p => p.Lat);
        var pts = polygon.Select(p => ((p.Lng - lng0) * Math.PI / 180 * earth * Math.Cos(lat0), (p.Lat - latc) * Math.PI / 180 * earth)).ToList();

        // outward is to the left of a clockwise ring: use the signed area to know which way round it is
        double area = 0;
        for (var i = 0; i < pts.Count; i++)
        {
            var (x1, y1) = pts[i];
            var (x2, y2) = pts[(i + 1) % pts.Count];
            area += x1 * y2 - x2 * y1;
        }

        var sign = area > 0 ? 1 : -1; // counter-clockwise: the outward normal is on the right of each edge
        var lines = new List<(double Px, double Py, double Dx, double Dy)>();
        for (var i = 0; i < pts.Count; i++)
        {
            var (x1, y1) = pts[i];
            var (x2, y2) = pts[(i + 1) % pts.Count];
            var (dx, dy) = (x2 - x1, y2 - y1);
            var len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9)
                continue;
            var (nx, ny) = (dy / len * sign, -dx / len * sign);
            lines.Add((x1 + nx * metres, y1 + ny * metres, dx, dy));
        }

        var result = new List<PointLatLng>();
        for (var i = 0; i < lines.Count; i++)
        {
            var a = lines[(i + lines.Count - 1) % lines.Count];
            var b = lines[i];
            var den = a.Dx * b.Dy - a.Dy * b.Dx;
            double x, y;
            if (Math.Abs(den) < 1e-9)
            {
                (x, y) = (b.Px, b.Py);
            }
            else
            {
                var t = ((b.Px - a.Px) * b.Dy - (b.Py - a.Py) * b.Dx) / den;
                (x, y) = (a.Px + a.Dx * t, a.Py + a.Dy * t);
            }

            result.Add(new PointLatLng(latc + y / earth * 180 / Math.PI, lng0 + x / (earth * Math.Cos(lat0)) * 180 / Math.PI));
        }

        return result;
    }
}
