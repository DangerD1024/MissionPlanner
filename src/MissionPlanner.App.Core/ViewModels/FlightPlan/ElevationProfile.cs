using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using GMap.NET;
using MissionPlanner.App.Core.Mission;

namespace MissionPlanner.App.Core.ViewModels.FlightPlan;

/// <summary>A point of the elevation graph: distance along the route, ground and planned altitude (m AMSL).</summary>
public readonly record struct ProfileSample(double Distance, double? Ground, double Path);

/// <summary>A waypoint on the graph: its number, distance and planned altitude (m AMSL).</summary>
public readonly record struct ProfileWaypoint(int Seq, double Distance, double Path);

/// <summary>
/// Mission Planner's Elevation Graph: the planned altitude along the route from home against the
/// terrain under it (SRTM), each altitude made absolute from its frame.
/// </summary>
public static class ElevationProfile
{
    /// <summary>Samples every <paramref name="step"/> metres (or fewer, for very long routes).</summary>
    public static (List<ProfileSample> Samples, List<ProfileWaypoint> Waypoints) Build(PointLatLng? home, double homeAlt,
        IEnumerable<MissionItem> mission, Func<double, double, double?> ground, double step = 25)
    {
        var route = new List<(int Seq, PointLatLng P, double Alt)>();
        if (home is { } h)
            route.Add((0, h, homeAlt));
        foreach (var item in mission.Where(i => i.HasLocation && (i.Lat != 0 || i.Lng != 0)))
        {
            var p = new PointLatLng(item.Lat, item.Lng);
            var alt = item.Frame switch
            {
                AltFrame.Absolute => item.Alt,
                AltFrame.Terrain => (ground(item.Lat, item.Lng) ?? homeAlt) + item.Alt,
                _ => homeAlt + item.Alt,
            };
            route.Add((item.Seq, p, alt));
        }

        var samples = new List<ProfileSample>();
        var wps = new List<ProfileWaypoint>();
        if (route.Count == 0)
            return (samples, wps);

        var total = 0.0;
        for (var i = 1; i < route.Count; i++)
            total += Metres(route[i - 1].P, route[i].P);
        step = Math.Max(step, total / 2000);

        var dist = 0.0;
        wps.Add(new ProfileWaypoint(route[0].Seq, 0, route[0].Alt));
        samples.Add(new ProfileSample(0, ground(route[0].P.Lat, route[0].P.Lng), route[0].Alt));
        for (var i = 1; i < route.Count; i++)
        {
            var (a, b) = (route[i - 1], route[i]);
            var leg = Metres(a.P, b.P);
            var n = Math.Max(1, (int)Math.Ceiling(leg / step));
            for (var s = 1; s <= n; s++)
            {
                var f = (double)s / n;
                var lat = a.P.Lat + (b.P.Lat - a.P.Lat) * f;
                var lng = a.P.Lng + (b.P.Lng - a.P.Lng) * f;
                samples.Add(new ProfileSample(dist + leg * f, ground(lat, lng), a.Alt + (b.Alt - a.Alt) * f));
            }

            dist += leg;
            wps.Add(new ProfileWaypoint(b.Seq, dist, b.Alt));
        }

        return (samples, wps);
    }

    /// <summary>The lowest height of the route above the ground (null without terrain data).</summary>
    public static (double Clearance, double Distance)? LowestClearance(IEnumerable<ProfileSample> samples)
    {
        (double, double)? best = null;
        foreach (var s in samples)
        {
            if (s.Ground is { } g && (best == null || s.Path - g < best.Value.Item1))
                best = (s.Path - g, s.Distance);
        }

        return best;
    }

    private static double Metres(PointLatLng a, PointLatLng b) =>
        new MissionPlanner.Utilities.PointLatLngAlt(a.Lat, a.Lng).GetDistance(new MissionPlanner.Utilities.PointLatLngAlt(b.Lat, b.Lng));
}

/// <summary>The Elevation Graph window.</summary>
public sealed partial class ElevationGraphViewModel : PageViewModel
{
    private readonly Func<Task<(List<ProfileSample>, List<ProfileWaypoint>)>> _build;

    public ElevationGraphViewModel(Func<Task<(List<ProfileSample>, List<ProfileWaypoint>)>> build)
        : base("Elevation graph")
    {
        _build = build;
    }

    [ObservableProperty]
    private IReadOnlyList<ProfileSample> _samples = [];

    [ObservableProperty]
    private IReadOnlyList<ProfileWaypoint> _waypoints = [];

    [ObservableProperty]
    private string _summary = "Reading the terrain…";

    public override void OnShown()
    {
        base.OnShown();
        _ = LoadAsync();
    }

    public async Task LoadAsync()
    {
        var (samples, wps) = await _build();
        Samples = samples;
        Waypoints = wps;
        Summary = Describe(samples, wps);
    }

    public static string Describe(IReadOnlyList<ProfileSample> samples, IReadOnlyList<ProfileWaypoint> wps)
    {
        if (wps.Count < 2)
            return "The mission needs home and at least one waypoint.";
        var length = MissionEditing.FormatDistance(samples[^1].Distance);
        var known = samples.Count(s => s.Ground != null);
        if (known == 0)
            return $"Route {length}. No terrain data here yet (it downloads in the background; try again shortly).";
        var low = ElevationProfile.LowestClearance(samples)!.Value;
        var text = $"Route {length} · lowest height above ground {(Math.Round(low.Clearance) + 0.0).ToString("0", CultureInfo.InvariantCulture)} m at {MissionEditing.FormatDistance(low.Distance)}";
        if (known < samples.Count)
            text += $" · terrain known for {known * 100 / samples.Count}% of the route";
        return low.Clearance < -0.5 ? text + " — BELOW THE GROUND" : text;
    }
}
