using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.FlightPlan;

/// <summary>Terrain height from the SRTM tiles Mission Planner downloads in the background.</summary>
public static class PlanTerrain
{
    /// <summary>
    /// Ground altitude (m AMSL), or null while unknown: <c>srtm.getAltitude</c> answers "invalid"
    /// until the tile has downloaded. Can read a tile from disk; call off the UI thread.
    /// </summary>
    public static double? Altitude(double lat, double lng)
    {
        try
        {
            var r = srtm.getAltitude(lat, lng);
            return r.currenttype switch
            {
                srtm.tiletype.valid => r.alt,
                srtm.tiletype.ocean => 0,
                _ => null,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }
}
