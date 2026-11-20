namespace MissionPlanner.App.Core.Synthetic;

/// <summary>
/// A flat local frame around a point, in metres: east and north (equirectangular, accurate to a few
/// centimetres over the ±25 km the 3D view uses). Heights are metres above mean sea level.
/// </summary>
public readonly record struct LocalFrame(double Lat, double Lng)
{
    public const double EarthRadius = 6378137.0;

    private double MetresPerDegLat => EarthRadius * Math.PI / 180;

    private double MetresPerDegLng => EarthRadius * Math.PI / 180 * Math.Cos(Lat * Math.PI / 180);

    public (double East, double North) ToLocal(double lat, double lng) =>
        ((lng - Lng) * MetresPerDegLng, (lat - Lat) * MetresPerDegLat);

    public (double Lat, double Lng) ToGeo(double east, double north) =>
        (Lat + north / MetresPerDegLat, Lng + east / MetresPerDegLng);
}

/// <summary>Web Mercator tiles (the XYZ scheme of map providers and Terrarium elevation tiles).</summary>
public static class Tiles
{
    public const int Size = 256;

    /// <summary>Fractional tile column of a longitude at a zoom.</summary>
    public static double X(double lng, int zoom) => (lng + 180.0) / 360.0 * (1 << zoom);

    /// <summary>Fractional tile row of a latitude at a zoom (clamped to the Mercator range).</summary>
    public static double Y(double lat, int zoom)
    {
        lat = Math.Clamp(lat, -85.05112878, 85.05112878);
        var r = lat * Math.PI / 180;
        return (1 - Math.Log(Math.Tan(r) + 1 / Math.Cos(r)) / Math.PI) / 2 * (1 << zoom);
    }

    public static double Lng(double x, int zoom) => x / (1 << zoom) * 360.0 - 180.0;

    public static double Lat(double y, int zoom)
    {
        var n = Math.PI - 2 * Math.PI * y / (1 << zoom);
        return 180 / Math.PI * Math.Atan(Math.Sinh(n));
    }

    /// <summary>Ground metres per tile at a latitude.</summary>
    public static double MetresPerTile(double lat, int zoom) =>
        2 * Math.PI * LocalFrame.EarthRadius * Math.Cos(lat * Math.PI / 180) / (1 << zoom);

    /// <summary>Terrarium encoding (AWS elevation tiles): metres = R·256 + G + B/256 − 32768.</summary>
    public static float Terrarium(byte r, byte g, byte b) => r * 256f + g + b / 256f - 32768f;
}
