using System.Globalization;
using System.Text;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Rtk;

/// <summary>Position reported to an NTRIP caster (VRS and nearest-base casters need it).</summary>
/// <param name="Quality">NMEA GGA fix quality: 1 GPS, 2 DGPS, 4 RTK fixed, 5 RTK float.</param>
public sealed record GgaPosition(double Lat, double Lng, double AltMsl, int Quality = 1, int Satellites = 10, double Hdop = 1.0);

/// <summary>Builds NMEA GGA sentences for the caster.</summary>
public static class NmeaGga
{
    public static string Build(GgaPosition p, DateTime utc)
    {
        var inv = CultureInfo.InvariantCulture;
        var (latDeg, latMin) = DegMin(p.Lat);
        var (lngDeg, lngMin) = DegMin(p.Lng);
        var body = string.Join(",",
            "GPGGA",
            utc.ToString("HHmmss.ff", inv),
            latDeg.ToString("00", inv) + latMin.ToString("00.00000", inv),
            p.Lat < 0 ? "S" : "N",
            lngDeg.ToString("000", inv) + lngMin.ToString("00.00000", inv),
            p.Lng < 0 ? "W" : "E",
            Math.Clamp(p.Quality, 0, 9).ToString(inv),
            Math.Clamp(p.Satellites, 0, 99).ToString("00", inv),
            p.Hdop.ToString("0.0", inv),
            p.AltMsl.ToString("0.0", inv),
            "M",
            "0.0",
            "M",
            "",
            "");
        return "$" + body + "*" + Checksum(body).ToString("X2", inv);
    }

    /// <summary>XOR of the characters between '$' and '*'.</summary>
    public static byte Checksum(string body)
    {
        byte sum = 0;
        foreach (var c in body)
        {
            if (c is '$')
                continue;
            if (c is '*')
                break;
            sum ^= (byte)c;
        }

        return sum;
    }

    /// <summary>Whole degrees and decimal minutes of |value|, rounding minutes so they never print as 60.</summary>
    internal static (int Degrees, double Minutes) DegMin(double value)
    {
        var abs = Math.Abs(value);
        var deg = (int)Math.Floor(abs);
        var min = Math.Round((abs - deg) * 60, 5);
        if (min >= 60)
        {
            deg++;
            min = 0;
        }

        return (deg, min);
    }

    /// <summary>GGA fix quality for a MAVLink GPS fix type (CurrentState.gpsstatus).</summary>
    public static int QualityFromFixType(int fixType) => fixType switch
    {
        4 => 2,
        5 => 5,
        6 => 4,
        >= 3 => 1,
        _ => 0,
    };

    public static byte[] ToBytes(string sentence) => Encoding.ASCII.GetBytes(sentence + "\r\n");
}
