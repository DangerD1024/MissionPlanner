using System.Globalization;
using System.IO.Compression;
using GMap.NET;
using MissionPlanner.App.Core.Mission;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.FlightPlan;

/// <summary>PLAN's file formats beyond QGC WPL, and the polygon tools (Mission Planner's File and Polygon menus).</summary>
public sealed partial class FlightPlanViewModel
{
    private static readonly FileFilter[] AnyPlanFilters =
    [
        new FileFilter("Mission files", "waypoints", "txt", "plan", "mission", "kml", "kmz", "shp"),
        new FileFilter("Waypoints (QGC WPL)", "waypoints", "txt"),
        new FileFilter("Plan / mission (JSON)", "plan", "mission"),
        new FileFilter("Google Earth", "kml", "kmz"),
        new FileFilter("Shapefile", "shp"),
        new FileFilter("All files", "*"),
    ];

    private static readonly FileFilter[] FenceFilters = [new FileFilter("Fence", "fen"), new FileFilter("All files", "*")];
    private static readonly FileFilter[] RallyFilters = [new FileFilter("Rally", "ral"), new FileFilter("All files", "*")];

    /// <summary>File > Load WP File (any format) and Load and Append.</summary>
    public async Task LoadAnyFileAsync(bool append)
    {
        var path = await Services.Dialogs.OpenFileAsync(append ? "Load and append" : "Load WP file", AnyPlanFilters);
        if (path != null)
            await LoadAnyFileFromAsync(path, append);
    }

    public async Task<bool> LoadAnyFileFromAsync(string path, bool append)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        List<MissionItem> items;
        try
        {
            switch (ext)
            {
                case ".plan" or ".mission" or ".json":
                {
                    var wps = PlanFiles.ReadPlan(path);
                    if (wps.Count > 0 && !append && (wps[0].lat != 0 || wps[0].lng != 0))
                        Store.SetHome(wps[0].lat, wps[0].lng, wps[0].alt);
                    items = wps.Skip(1).Select(MissionItem.FromLocationwp).ToList();
                    break;
                }

                case ".kml" or ".kmz":
                    items = ShapesToItems(PlanFiles.ReadKml(ReadKmlText(path)), out var kmlPolygon);
                    if (kmlPolygon != null)
                        SetPolygon(kmlPolygon);
                    break;
                case ".shp":
                    items = ShapesToItems(ReadShapes(path), out var shpPolygon);
                    if (shpPolygon != null)
                        SetPolygon(shpPolygon);
                    break;
                default:
                    if (!append)
                        return await LoadFileFromAsync(path);
                    var wpl = WaypointFile.ReadWaypointFile(path);
                    items = wpl.Skip(1).Select(MissionItem.FromLocationwp).ToList();
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or FormatException or
                                       System.Xml.XmlException or Newtonsoft.Json.JsonException or NullReferenceException)
        {
            await Services.Dialogs.ShowMessageAsync("Load file", "Could not load the file: " + ex.Message, MessageKind.Error);
            return false;
        }

        ShowList(MAVLink.MAV_MISSION_TYPE.MISSION);
        if (!append)
            Store.Mission.Clear();
        foreach (var item in items)
            Store.Mission.Add(item);
        SelectedItem = null;
        AdoptFrameFromList();
        Status = $"{(append ? "Appended" : "Loaded")} {items.Count} items from {Path.GetFileName(path)}";
        ZoomToMission();
        return true;
    }

    private static string ReadKmlText(string path)
    {
        if (!path.EndsWith(".kmz", StringComparison.OrdinalIgnoreCase))
            return File.ReadAllText(path);
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".kml", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException("The KMZ has no KML inside.");
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static List<PlanShape> ReadShapes(string path)
    {
        var prj = Path.ChangeExtension(path, ".prj");
        if (PlanFiles.ShapefileProjectionProblem(File.Exists(prj) ? File.ReadAllText(prj) : null) is { } problem)
            throw new InvalidDataException(problem);
        return PlanFiles.ReadShapefile(File.ReadAllBytes(path));
    }

    /// <summary>Points and lines become waypoints (their altitude, or the default); the first polygon is returned for drawing.</summary>
    private List<MissionItem> ShapesToItems(IEnumerable<PlanShape> shapes, out List<PointLatLng>? polygon)
    {
        polygon = null;
        var items = new List<MissionItem>();
        foreach (var shape in shapes)
        {
            if (shape.IsPolygon)
            {
                polygon ??= shape.Points.Select(p => new PointLatLng(p.Lat, p.Lng)).ToList();
                continue;
            }

            foreach (var (lat, lng, alt) in shape.Points)
            {
                var wp = Store.NewWaypoint(lat, lng);
                if (alt is { } a && a > 0)
                    wp.Alt = (float)a;
                items.Add(wp);
            }
        }

        return items;
    }

    private void SetPolygon(IReadOnlyList<PointLatLng> points)
    {
        Polygon.Clear();
        foreach (var p in points)
            Polygon.Add(p);
    }

    /// <summary>File > Save as .plan / .mission (JSON); .waypoints otherwise.</summary>
    public async Task SaveAnyFileAsync()
    {
        var path = await Services.Dialogs.SaveFileAsync("Save mission", "mission.plan",
            new FileFilter("Plan (QGroundControl, Mission Planner)", "plan"), new FileFilter("Mission (JSON)", "mission"),
            new FileFilter("Waypoints (QGC WPL)", "waypoints"));
        if (path == null)
            return;
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".plan" or ".mission" or ".json")
            {
                var home = new Locationwp { id = (ushort)MAVLink.MAV_CMD.WAYPOINT, lat = Store.HomeLat, lng = Store.HomeLng, alt = (float)Store.HomeAlt };
                PlanFiles.WritePlan(path, home, Store.Mission.Select(i => i.ToLocationwp()));
            }
            else
            {
                Store.SaveWaypointFile(path, MAVLink.MAV_MISSION_TYPE.MISSION);
            }

            Status = $"Saved {Path.GetFileName(path)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Services.Dialogs.ShowMessageAsync("Save file", "Could not save the file: " + ex.Message, MessageKind.Error);
        }
    }

    // ---- fence and rally files

    /// <summary>Fence > Load from file (.fen): the return point and an inclusion polygon.</summary>
    public async Task LoadFenceFileAsync()
    {
        var path = await Services.Dialogs.OpenFileAsync("Load fence", FenceFilters);
        if (path == null)
            return;
        var (ret, poly) = PlanFiles.ReadFence(File.ReadLines(path));
        ShowList(MAVLink.MAV_MISSION_TYPE.FENCE);
        Store.Fence.Clear();
        if (ret is { } r)
            Store.Fence.Add(new MissionItem { Command = MAVLink.MAV_CMD.FENCE_RETURN_POINT, Lat = r.Lat, Lng = r.Lng, Frame = AltFrame.Relative });
        if (poly.Count >= 3)
        {
            foreach (var item in MissionEditing.FencePolygon(poly, inclusion: true))
                Store.Fence.Add(item);
        }

        Status = $"Loaded fence: {poly.Count} points";
        ZoomToMission();
    }

    /// <summary>Fence > Save to file (.fen): the return point (or home) and the first inclusion polygon.</summary>
    public async Task SaveFenceFileAsync()
    {
        var groups = MissionEditing.FencePolygonGroups(Store.Fence);
        var inclusion = groups.FirstOrDefault(g => g.Command == MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION);
        if (inclusion.Count < 3)
        {
            await Services.Dialogs.ShowMessageAsync("Save fence", "A .fen file holds one inclusion polygon; the fence has none.", MessageKind.Warning);
            return;
        }

        var path = await Services.Dialogs.SaveFileAsync("Save fence", "fence.fen", FenceFilters);
        if (path == null)
            return;
        var ret = Store.Fence.FirstOrDefault(i => i.Command == MAVLink.MAV_CMD.FENCE_RETURN_POINT) is { } rp
            ? new PointLatLng(rp.Lat, rp.Lng)
            : new PointLatLng(Store.HomeLat, Store.HomeLng);
        var pts = Store.Fence.Skip(inclusion.Start).Take(inclusion.Count).Select(i => new PointLatLng(i.Lat, i.Lng)).ToList();
        await File.WriteAllTextAsync(path, PlanFiles.WriteFence(ret, pts));
        Status = $"Saved {Path.GetFileName(path)}";
    }

    public async Task LoadRallyFileAsync()
    {
        var path = await Services.Dialogs.OpenFileAsync("Load rally points", RallyFilters);
        if (path == null)
            return;
        ShowList(MAVLink.MAV_MISSION_TYPE.RALLY);
        Store.Rally.Clear();
        foreach (var (lat, lng, alt) in PlanFiles.ReadRally(File.ReadLines(path)))
        {
            var item = NewRallyPoint(new PointLatLng(lat, lng));
            item.Alt = (float)alt;
            Store.Rally.Add(item);
        }

        Status = $"Loaded {Store.Rally.Count} rally points";
        ZoomToMission();
    }

    public async Task SaveRallyFileAsync()
    {
        var path = await Services.Dialogs.SaveFileAsync("Save rally points", "rally.ral", RallyFilters);
        if (path == null)
            return;
        await File.WriteAllTextAsync(path, PlanFiles.WriteRally(Store.Rally.Select(i => (i.Lat, i.Lng, (double)i.Alt))));
        Status = $"Saved {Path.GetFileName(path)}";
    }

    // ---- polygon tools

    /// <summary>Polygon > From SHP: the first polygon of a shapefile.</summary>
    public async Task PolygonFromShapefileAsync()
    {
        var path = await Services.Dialogs.OpenFileAsync("Polygon from SHP", new FileFilter("Shapefile", "shp"));
        if (path == null)
            return;
        try
        {
            var shape = ReadShapes(path).FirstOrDefault(s => s.IsPolygon)
                        ?? throw new InvalidDataException("The shapefile has no polygon.");
            SetPolygon(shape.Points.Select(p => new PointLatLng(p.Lat, p.Lng)).ToList());
            Status = $"Polygon of {Polygon.Count} points from {Path.GetFileName(path)}";
            RequestZoom(Polygon.ToList());
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            await Services.Dialogs.ShowMessageAsync("Polygon from SHP", ex.Message, MessageKind.Error);
        }
    }

    /// <summary>Polygon > From Current Waypoints.</summary>
    public void PolygonFromWaypoints()
    {
        var pts = Store.Mission.Where(i => i.HasLocation && (i.Lat != 0 || i.Lng != 0)).Select(i => new PointLatLng(i.Lat, i.Lng)).ToList();
        if (pts.Count < 3)
        {
            Status = "At least 3 waypoints are needed for a polygon";
            return;
        }

        SetPolygon(pts);
        Status = $"Polygon of {pts.Count} points from the waypoints";
    }

    /// <summary>Polygon > Offset: grows (or with a negative distance shrinks) the drawn polygon.</summary>
    public async Task OffsetPolygonAsync()
    {
        if (Polygon.Count < 3)
        {
            Status = "Draw a polygon first";
            return;
        }

        if (await PromptNumberAsync("Offset Polygon", "Distance in metres (positive grows the polygon, negative shrinks it)", "10") is not { } d)
            return;
        SetPolygon(PlanFiles.Offset(Polygon.ToList(), d));
        Status = $"Polygon offset by {d.ToString("0.##", CultureInfo.InvariantCulture)} m";
    }
}
