using MissionPlanner.App.Core.Mission;
using MissionPlanner.ArduPilot;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.FlightData;

/// <summary>Which icon the map draws for the vehicle.</summary>
public enum VehicleIconKind
{
    Multirotor,
    Plane,
    Rover,
    Boat,
}

public static class VehicleIcons
{
    /// <summary>
    /// From the heartbeat's MAV_TYPE, like Mission Planner's map markers (FIXED_WING and the VTOL
    /// types are planes); a vehicle that has not said falls back to the firmware.
    /// </summary>
    public static VehicleIconKind For(MAVLink.MAV_TYPE type, Firmwares firmware)
    {
        switch (type)
        {
            case MAVLink.MAV_TYPE.FIXED_WING:
            case >= MAVLink.MAV_TYPE.VTOL_DUOROTOR and <= MAVLink.MAV_TYPE.VTOL_RESERVED5:
                return VehicleIconKind.Plane;
            case MAVLink.MAV_TYPE.GROUND_ROVER:
                return VehicleIconKind.Rover;
            case MAVLink.MAV_TYPE.SURFACE_BOAT:
            case MAVLink.MAV_TYPE.SUBMARINE:
                return VehicleIconKind.Boat;
            case MAVLink.MAV_TYPE.QUADROTOR:
            case MAVLink.MAV_TYPE.HEXAROTOR:
            case MAVLink.MAV_TYPE.OCTOROTOR:
            case MAVLink.MAV_TYPE.TRICOPTER:
            case MAVLink.MAV_TYPE.COAXIAL:
            case MAVLink.MAV_TYPE.HELICOPTER:
            case MAVLink.MAV_TYPE.DODECAROTOR:
            case MAVLink.MAV_TYPE.DECAROTOR:
                return VehicleIconKind.Multirotor;
        }

        return firmware switch
        {
            Firmwares.ArduPlane => VehicleIconKind.Plane,
            Firmwares.ArduRover => VehicleIconKind.Rover,
            Firmwares.ArduSub => VehicleIconKind.Boat,
            _ => VehicleIconKind.Multirotor,
        };
    }
}

public enum MissionMarkerKind
{
    Waypoint,
    /// <summary>A located command that is not part of the route (ROI, land start...).</summary>
    Other,
    FenceInclusion,
    FenceExclusion,
    FenceReturn,
    Rally,
}

/// <summary>A numbered point of the planned mission, fence or rally list.</summary>
public sealed record MissionMarker(double Lat, double Lng, string Label, MissionMarkerKind Kind);

public sealed record FenceCircle(double Lat, double Lng, double RadiusMetres, bool Inclusion);

public sealed record FencePolygon(IReadOnlyList<TrackPoint> Points, bool Inclusion);

/// <summary>
/// The planned mission as map geometry, built from <see cref="MissionStore"/> when it changes
/// (never per frame): route through the navigation items, legs to and from home, markers,
/// fence polygons and circles, rally points.
/// </summary>
public sealed class MissionOverlay
{
    public static readonly MissionOverlay Empty = new();

    public TrackPoint? Home { get; init; }

    public IReadOnlyList<TrackPoint> Route { get; init; } = [];

    public IReadOnlyList<MissionMarker> Markers { get; init; } = [];

    public IReadOnlyList<FencePolygon> FencePolygons { get; init; } = [];

    public IReadOnlyList<FenceCircle> FenceCircles { get; init; } = [];

    public bool IsEmpty => Route.Count == 0 && Markers.Count == 0 && FencePolygons.Count == 0 && FenceCircles.Count == 0;

    public static MissionOverlay Build(MissionStore store, TrackPoint? vehicleHome)
    {
        TrackPoint? home = vehicleHome ?? (store.HomeSet && (store.HomeLat != 0 || store.HomeLng != 0)
            ? new TrackPoint(store.HomeLat, store.HomeLng)
            : null);
        return Build(home, store.Mission.Select(Plain).ToList(), store.Fence.Select(Plain).ToList(), store.Rally.Select(Plain).ToList());
    }

    /// <summary>The pure part, for tests: items as (command, p1, lat, lng).</summary>
    public static MissionOverlay Build(TrackPoint? home, IReadOnlyList<PlanItem> mission, IReadOnlyList<PlanItem> fence,
        IReadOnlyList<PlanItem> rally)
    {
        var route = new List<TrackPoint>();
        var markers = new List<MissionMarker>();
        var polygons = new List<FencePolygon>();
        var circles = new List<FenceCircle>();

        if (home is { } h)
            route.Add(h);

        for (var i = 0; i < mission.Count; i++)
        {
            var item = mission[i];
            if (!HasPosition(item))
                continue;
            var label = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (IsRouteCommand(item.Command))
            {
                route.Add(new TrackPoint(item.Lat, item.Lng));
                markers.Add(new MissionMarker(item.Lat, item.Lng, label, MissionMarkerKind.Waypoint));
            }
            else if (IsLocationCommand(item.Command))
            {
                markers.Add(new MissionMarker(item.Lat, item.Lng, label, MissionMarkerKind.Other));
            }
        }

        // a lone home point is not a route
        if (route.Count == 1 && home != null)
            route.Clear();

        List<TrackPoint>? poly = null;
        var polyInclusion = true;
        for (var i = 0; i < fence.Count; i++)
        {
            var item = fence[i];
            var label = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            switch (item.Command)
            {
                case MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION:
                case MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_EXCLUSION:
                {
                    var inclusion = item.Command == MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION;
                    if (poly != null && polyInclusion != inclusion)
                    {
                        // a malformed list switched polygon type mid-way: close what we have
                        polygons.Add(new FencePolygon(poly, polyInclusion));
                        poly = null;
                    }

                    poly ??= new List<TrackPoint>();
                    polyInclusion = inclusion;
                    poly.Add(new TrackPoint(item.Lat, item.Lng));
                    markers.Add(new MissionMarker(item.Lat, item.Lng, label,
                        inclusion ? MissionMarkerKind.FenceInclusion : MissionMarkerKind.FenceExclusion));
                    // p1 is the polygon's vertex count
                    if (poly.Count >= Math.Max(3, (int)item.P1))
                    {
                        polygons.Add(new FencePolygon(poly, inclusion));
                        poly = null;
                    }

                    break;
                }
                case MAVLink.MAV_CMD.FENCE_CIRCLE_INCLUSION:
                case MAVLink.MAV_CMD.FENCE_CIRCLE_EXCLUSION:
                    if (HasPosition(item) && item.P1 > 0)
                    {
                        var inclusion = item.Command == MAVLink.MAV_CMD.FENCE_CIRCLE_INCLUSION;
                        circles.Add(new FenceCircle(item.Lat, item.Lng, item.P1, inclusion));
                        markers.Add(new MissionMarker(item.Lat, item.Lng, label,
                            inclusion ? MissionMarkerKind.FenceInclusion : MissionMarkerKind.FenceExclusion));
                    }

                    break;
                case MAVLink.MAV_CMD.FENCE_RETURN_POINT:
                    if (HasPosition(item))
                        markers.Add(new MissionMarker(item.Lat, item.Lng, label, MissionMarkerKind.FenceReturn));
                    break;
            }
        }

        if (poly is { Count: >= 3 })
            polygons.Add(new FencePolygon(poly, polyInclusion));

        for (var i = 0; i < rally.Count; i++)
        {
            var item = rally[i];
            if (HasPosition(item))
                markers.Add(new MissionMarker(item.Lat, item.Lng, "R" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    MissionMarkerKind.Rally));
        }

        return new MissionOverlay
        {
            Home = home,
            Route = route,
            Markers = markers,
            FencePolygons = polygons,
            FenceCircles = circles,
        };
    }

    /// <summary>Navigation commands (below MAV_CMD.LAST) with a position are flown through, like Mission Planner's route.</summary>
    public static bool IsRouteCommand(MAVLink.MAV_CMD cmd) =>
        (ushort)cmd < (ushort)MAVLink.MAV_CMD.LAST && cmd != MAVLink.MAV_CMD.RETURN_TO_LAUNCH && IsLocationCommand(cmd);

    private static readonly Dictionary<MAVLink.MAV_CMD, bool> LocationCache = new();

    /// <summary>The library's hasLocation attribute lookup, cached (it reflects on every call).</summary>
    public static bool IsLocationCommand(MAVLink.MAV_CMD cmd)
    {
        lock (LocationCache)
        {
            if (!LocationCache.TryGetValue(cmd, out var has))
            {
                has = Locationwp.isLocationCommand((ushort)cmd);
                LocationCache[cmd] = has;
            }

            return has;
        }
    }

    private static bool HasPosition(PlanItem item) =>
        (item.Lat != 0 || item.Lng != 0) && Math.Abs(item.Lat) <= 90 && Math.Abs(item.Lng) <= 180;

    private static PlanItem Plain(MissionItem m) => new(m.Command, m.P1, m.Lat, m.Lng);
}

/// <summary>The parts of a mission item the map needs.</summary>
public readonly record struct PlanItem(MAVLink.MAV_CMD Command, float P1, double Lat, double Lng);

/// <summary>
/// Everything the Flight Data map layers draw, refreshed by the view-model at up to 10 Hz.
/// Plain fields (UI thread only); <see cref="Version"/> moves when anything changed.
/// </summary>
public sealed class FlightMapModel
{
    public long Version;

    public bool HasPosition;
    public double Lat;
    public double Lng;
    public double Heading;
    public double GroundCourse;
    public double NavBearing;
    public double TargetBearing;
    public bool ShowNavLines;
    public VehicleIconKind Kind;

    /// <summary>Label of the waypoint being flown to (MISSION_CURRENT), "" if none.</summary>
    public string CurrentWp = "";

    public bool HasHome;
    public double HomeLat;
    public double HomeLng;

    public bool HasGuided;
    public double GuidedLat;
    public double GuidedLng;
    public double GuidedAlt;

    public TrackBuffer Track { get; } = new();

    public MissionOverlay Mission = MissionOverlay.Empty;
}
