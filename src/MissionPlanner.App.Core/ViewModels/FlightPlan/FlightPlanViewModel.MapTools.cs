using System.Globalization;
using GMap.NET;
using MissionPlanner.App.Core.Mission;

namespace MissionPlanner.App.Core.ViewModels.FlightPlan;

/// <summary>Mission edits of the PLAN map menu that act on the whole list.</summary>
public static class MissionTransforms
{
    /// <summary>Reverse WPs: the mission in the opposite order (Mission Planner keeps every item).</summary>
    public static void Reverse(IList<MissionItem> list)
    {
        var items = list.ToList();
        items.Reverse();
        list.Clear();
        foreach (var i in items)
            list.Add(i);
    }

    /// <summary>Modify Alt: adds <paramref name="delta"/> metres to every item with a location (not landings at 0).</summary>
    public static int ShiftAltitudes(IEnumerable<MissionItem> list, double delta)
    {
        var n = 0;
        foreach (var item in list.Where(i => i.HasLocation && !(i.Command == MAVLink.MAV_CMD.LAND && i.Alt == 0)))
        {
            item.Alt = (float)(item.Alt + delta);
            n++;
        }

        return n;
    }
}

public sealed partial class FlightPlanViewModel
{
    private PointLatLng? _measureFrom;

    /// <summary>Insert Wp > At Current Position: a waypoint where the vehicle is.</summary>
    public async Task InsertWaypointAtVehicleAsync()
    {
        if (VehiclePosition is not { } pos)
        {
            Status = "No vehicle position (connect to a vehicle with GPS)";
            return;
        }

        await InsertWaypointAsync(pos, spline: false);
    }

    /// <summary>Jump: DO_JUMP to a waypoint number (Start = 1), repeated a number of times.</summary>
    public async Task AddJumpAsync(bool toStart)
    {
        double target = 1;
        if (!toStart)
        {
            if (await PromptNumberAsync("Jump", "Jump to waypoint number", "1") is not { } wp)
                return;
            target = wp;
        }

        if (await PromptNumberAsync("Jump", "Number of times to repeat (-1 = forever)", "1") is not { } repeat)
            return;
        AddMissionItem(new MissionItem { Command = MAVLink.MAV_CMD.DO_JUMP, P1 = (float)target, P2 = (float)repeat, Frame = DefaultFrame });
    }

    /// <summary>DO_SET_ROI at a point: the camera points there for the following waypoints.</summary>
    public void AddRoiHere(PointLatLng position) =>
        AddMissionItem(Store.NewWaypoint(position.Lat, position.Lng, MAVLink.MAV_CMD.DO_SET_ROI));

    public void ReverseWaypoints()
    {
        ShowList(MAVLink.MAV_MISSION_TYPE.MISSION);
        MissionTransforms.Reverse(Store.Mission);
        Status = "Waypoints reversed";
    }

    public async Task ModifyAltitudesAsync()
    {
        if (await PromptNumberAsync("Modify Alt", "Altitude change for every waypoint (m, negative lowers)", "10") is not { } delta)
            return;
        var n = MissionTransforms.ShiftAltitudes(Store.Mission, delta);
        Store.Recalculate();
        Status = $"{n} altitudes changed by {delta.ToString("0.##", CultureInfo.InvariantCulture)} m";
    }

    public void ZoomToVehicle()
    {
        if (VehiclePosition is { } p)
            RequestZoom([p]);
        else
            Status = "No vehicle position";
    }

    public void ZoomToHome()
    {
        if (Home is { } h)
            RequestZoom([h]);
        else
            Status = "No home position";
    }

    /// <summary>Enter UTM Coord: a waypoint at a typed UTM position ("55S 694000 6084000").</summary>
    public async Task AddUtmWaypointAsync()
    {
        var answer = await Services.Dialogs.PromptAsync("Enter UTM Coord", "Zone and hemisphere (or band), easting, northing — e.g. 55S 694000 6084000");
        if (answer == null)
            return;
        if (Utm.Parse(answer) is not { } utm)
        {
            await Services.Dialogs.ShowMessageAsync("Enter UTM Coord", $"\"{answer}\" is not a UTM position.", MessageKind.Error);
            return;
        }

        var (lat, lng) = Utm.ToLatLng(utm);
        AddMissionItem(Store.NewWaypoint(lat, lng));
        RequestZoom([new PointLatLng(lat, lng)]);
    }

    /// <summary>Measure Distance: the first use marks a point, the second shows the distance and bearing to it.</summary>
    public async Task MeasureDistanceAsync(PointLatLng position)
    {
        if (_measureFrom is not { } from)
        {
            _measureFrom = position;
            Status = "Measure: right-click the second point > Measure Distance";
            return;
        }

        _measureFrom = null;
        var a = new MissionPlanner.Utilities.PointLatLngAlt(from.Lat, from.Lng);
        var b = new MissionPlanner.Utilities.PointLatLngAlt(position.Lat, position.Lng);
        var text = $"Distance {MissionEditing.FormatDistance(a.GetDistance(b))}, bearing {a.GetBearing(b).ToString("0", CultureInfo.InvariantCulture)}°";
        Status = text;
        await Services.Dialogs.ShowMessageAsync("Measure Distance", text);
    }

    /// <summary>Whether Measure Distance has its first point.</summary>
    public bool IsMeasuring => _measureFrom != null;
}
