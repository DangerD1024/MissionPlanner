using GMap.NET;
using MissionPlanner.App.Core.Mission;

namespace MissionPlanner.App.Core.ViewModels.FlightPlan;

/// <summary>Mission Planner's Auto WP shapes: waypoint circle, spline circle and circle survey.</summary>
public static class AutoWaypoints
{
    private const double EarthRadius = 6371000;

    /// <summary>The point <paramref name="metres"/> from <paramref name="c"/> on <paramref name="bearingDeg"/> (great circle, as Mission Planner).</summary>
    public static PointLatLng Offset(PointLatLng c, double bearingDeg, double metres)
    {
        var d = metres / EarthRadius;
        var lat1 = c.Lat * Math.PI / 180;
        var lng1 = c.Lng * Math.PI / 180;
        var b = bearingDeg * Math.PI / 180;
        var lat2 = Math.Asin(Math.Sin(lat1) * Math.Cos(d) + Math.Cos(lat1) * Math.Sin(d) * Math.Cos(b));
        var lng2 = lng1 + Math.Atan2(Math.Sin(b) * Math.Sin(d) * Math.Cos(lat1), Math.Cos(d) - Math.Sin(lat1) * Math.Sin(lat2));
        return new PointLatLng(lat2 * 180 / Math.PI, lng2 * 180 / Math.PI);
    }

    /// <summary>
    /// Create WP Circle: <paramref name="points"/> waypoints round the centre from the start angle,
    /// clockwise (1) or anticlockwise (-1), back to the first.
    /// </summary>
    public static List<PointLatLng> Circle(PointLatLng centre, double radius, int points, int direction, double startAngle)
    {
        var list = new List<PointLatLng>();
        if (points < 1 || radius <= 0)
            return list;
        var step = 360.0 / points * (direction < 0 ? -1 : 1);
        for (var i = 0; i <= points; i++)
            list.Add(Offset(centre, startAngle + i * step, radius));
        return list;
    }

    /// <summary>Create Spline Circle: an ROI on the centre, then a spline circle (every 90°) at each altitude from min to max.</summary>
    public static List<MissionItem> SplineCircle(PointLatLng centre, double radius, double minAlt, double maxAlt, double altStep, double startAngle,
        AltFrame frame)
    {
        var items = new List<MissionItem> { new() { Command = MAVLink.MAV_CMD.DO_SET_ROI, Lat = centre.Lat, Lng = centre.Lng, Frame = frame } };
        if (altStep <= 0)
            altStep = Math.Max(1, maxAlt - minAlt);
        for (var alt = minAlt; alt <= maxAlt + 1e-9; alt += altStep)
        {
            foreach (var p in Circle(centre, radius, 4, 1, startAngle))
                items.Add(new MissionItem { Command = MAVLink.MAV_CMD.SPLINE_WAYPOINT, Lat = p.Lat, Lng = p.Lng, Alt = (float)alt, Frame = frame });
        }

        return items;
    }

    /// <summary>
    /// Create Circle Survey (Mission Planner's CircleSurveyMission): an ROI on the centre, then on each
    /// altitude ring a waypoint (2 s pause) and a photo every 360/photos degrees.
    /// </summary>
    public static List<MissionItem> CircleSurvey(PointLatLng centre, double startAlt, double endAlt, double separation, double radius, int photos,
        double startHeading, AltFrame frame)
    {
        var items = new List<MissionItem> { new() { Command = MAVLink.MAV_CMD.DO_SET_ROI, Lat = centre.Lat, Lng = centre.Lng, Frame = frame } };
        if (photos < 1 || separation <= 0)
            return items;
        var step = 360.0 / photos;
        for (var alt = startAlt; alt <= endAlt + 1e-9; alt += separation)
        {
            for (var h = startHeading; h <= startHeading + 360 + 1e-9; h += step)
            {
                var p = Offset(centre, h, radius);
                items.Add(new MissionItem { Command = MAVLink.MAV_CMD.WAYPOINT, P1 = 2, Lat = p.Lat, Lng = p.Lng, Alt = (float)alt, Frame = frame });
                // DO_DIGICAM_CONTROL with param5 (the "lat" column) = 1: take a photo
                items.Add(new MissionItem { Command = MAVLink.MAV_CMD.DO_DIGICAM_CONTROL, Lat = 1, Frame = frame });
            }
        }

        return items;
    }
}
