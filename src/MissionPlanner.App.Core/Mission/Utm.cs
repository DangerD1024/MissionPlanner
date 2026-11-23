using System.Globalization;

namespace MissionPlanner.App.Core.Mission;

/// <summary>A UTM position: zone 1-60, hemisphere, easting and northing in metres.</summary>
public readonly record struct UtmPoint(int Zone, bool North, double Easting, double Northing)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Zone}{(North ? "N" : "S")} {Easting:0} {Northing:0}");
}

/// <summary>WGS84 latitude/longitude to and from UTM (the USGS series, under a millimetre within a zone).</summary>
public static class Utm
{
    private const double A = 6378137.0;
    private const double F = 1 / 298.257223563;
    private const double K0 = 0.9996;
    private static readonly double E2 = F * (2 - F);
    private static readonly double Ep2 = E2 / (1 - E2);

    public static int ZoneFor(double lng) => Math.Clamp((int)Math.Floor((lng + 180) / 6) + 1, 1, 60);

    private static double CentralMeridian(int zone) => (zone - 1) * 6 - 180 + 3;

    public static UtmPoint FromLatLng(double lat, double lng)
    {
        var zone = ZoneFor(lng);
        var phi = lat * Math.PI / 180;
        var lam = (lng - CentralMeridian(zone)) * Math.PI / 180;
        var n = A / Math.Sqrt(1 - E2 * Math.Sin(phi) * Math.Sin(phi));
        var t = Math.Tan(phi) * Math.Tan(phi);
        var c = Ep2 * Math.Cos(phi) * Math.Cos(phi);
        var a = Math.Cos(phi) * lam;
        var m = Meridian(phi);
        var x = K0 * n * (a + (1 - t + c) * Math.Pow(a, 3) / 6 + (5 - 18 * t + t * t + 72 * c - 58 * Ep2) * Math.Pow(a, 5) / 120) + 500000;
        var y = K0 * (m + n * Math.Tan(phi) * (a * a / 2 + (5 - t + 9 * c + 4 * c * c) * Math.Pow(a, 4) / 24 +
                                               (61 - 58 * t + t * t + 600 * c - 330 * Ep2) * Math.Pow(a, 6) / 720));
        var north = lat >= 0;
        if (!north)
            y += 10000000;
        return new UtmPoint(zone, north, x, y);
    }

    public static (double Lat, double Lng) ToLatLng(UtmPoint p)
    {
        var x = p.Easting - 500000;
        var y = p.North ? p.Northing : p.Northing - 10000000;
        var m = y / K0;
        var mu = m / (A * (1 - E2 / 4 - 3 * E2 * E2 / 64 - 5 * E2 * E2 * E2 / 256));
        var e1 = (1 - Math.Sqrt(1 - E2)) / (1 + Math.Sqrt(1 - E2));
        var phi1 = mu + (3 * e1 / 2 - 27 * Math.Pow(e1, 3) / 32) * Math.Sin(2 * mu) + (21 * e1 * e1 / 16 - 55 * Math.Pow(e1, 4) / 32) * Math.Sin(4 * mu) +
                   151 * Math.Pow(e1, 3) / 96 * Math.Sin(6 * mu) + 1097 * Math.Pow(e1, 4) / 512 * Math.Sin(8 * mu);
        var n1 = A / Math.Sqrt(1 - E2 * Math.Sin(phi1) * Math.Sin(phi1));
        var t1 = Math.Tan(phi1) * Math.Tan(phi1);
        var c1 = Ep2 * Math.Cos(phi1) * Math.Cos(phi1);
        var r1 = A * (1 - E2) / Math.Pow(1 - E2 * Math.Sin(phi1) * Math.Sin(phi1), 1.5);
        var d = x / (n1 * K0);
        var lat = phi1 - n1 * Math.Tan(phi1) / r1 * (d * d / 2 - (5 + 3 * t1 + 10 * c1 - 4 * c1 * c1 - 9 * Ep2) * Math.Pow(d, 4) / 24 +
                                                      (61 + 90 * t1 + 298 * c1 + 45 * t1 * t1 - 252 * Ep2 - 3 * c1 * c1) * Math.Pow(d, 6) / 720);
        var lng = (d - (1 + 2 * t1 + c1) * Math.Pow(d, 3) / 6 + (5 - 2 * c1 + 28 * t1 - 3 * c1 * c1 + 8 * Ep2 + 24 * t1 * t1) * Math.Pow(d, 5) / 120) /
                  Math.Cos(phi1);
        return (lat * 180 / Math.PI, CentralMeridian(p.Zone) + lng * 180 / Math.PI);
    }

    /// <summary>"55S 694000 6084000" or "55 S 694000 6084000"; null when it does not read as UTM.</summary>
    public static UtmPoint? Parse(string text)
    {
        var parts = text.Trim().Replace(",", " ").Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count == 4 && parts[1].Length == 1)
        {
            parts[0] += parts[1];
            parts.RemoveAt(1);
        }

        if (parts.Count != 3 || parts[0].Length < 2)
            return null;
        var band = char.ToUpperInvariant(parts[0][^1]);
        if (!int.TryParse(parts[0][..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var zone) || zone is < 1 or > 60)
            return null;
        // N/S as the hemisphere, or a latitude band letter (C-M south, N-X north)
        bool north;
        if (band is 'N' or 'S')
            north = band == 'N';
        else if (band is >= 'C' and <= 'X' and not 'I' and not 'O')
            north = band >= 'N';
        else
            return null;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var e) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            return null;
        return new UtmPoint(zone, north, e, n);
    }

    private static double Meridian(double phi) =>
        A * ((1 - E2 / 4 - 3 * E2 * E2 / 64 - 5 * Math.Pow(E2, 3) / 256) * phi -
             (3 * E2 / 8 + 3 * E2 * E2 / 32 + 45 * Math.Pow(E2, 3) / 1024) * Math.Sin(2 * phi) +
             (15 * E2 * E2 / 256 + 45 * Math.Pow(E2, 3) / 1024) * Math.Sin(4 * phi) - 35 * Math.Pow(E2, 3) / 3072 * Math.Sin(6 * phi));
}
