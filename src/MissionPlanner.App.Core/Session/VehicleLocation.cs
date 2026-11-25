namespace MissionPlanner.App.Core.Session;

/// <summary>
/// Whether a vehicle's reported position is a real place. Without a GPS fix (and no EKF origin) the
/// autopilot starts from 0,0 and dead-reckons from there, so it reports a position that drifts
/// around "null island" in the Gulf of Guinea. The map and 3D view would jump there. Near 0,0 a
/// position counts only with a GPS fix; elsewhere a dead-reckoned position after losing GPS is
/// still the best estimate and is kept.
/// </summary>
public static class VehicleLocation
{
    /// <summary>Degrees around 0,0 (about 110 km) that need a GPS fix: hours of dead reckoning.</summary>
    public const double NullIslandRadius = 1.0;

    public static bool IsKnown(double lat, double lng, bool gpsFix)
    {
        if (!double.IsFinite(lat) || !double.IsFinite(lng) || (lat == 0 && lng == 0))
            return false;
        return gpsFix || Math.Abs(lat) > NullIslandRadius || Math.Abs(lng) > NullIslandRadius;
    }

    /// <summary>A 2D fix or better on either GPS.</summary>
    public static bool HasGpsFix(CurrentState cs) => cs.gpsstatus >= 2 || cs.gpsstatus2 >= 2;

    public static bool IsKnown(CurrentState cs) => IsKnown(cs.lat, cs.lng, HasGpsFix(cs));
}
