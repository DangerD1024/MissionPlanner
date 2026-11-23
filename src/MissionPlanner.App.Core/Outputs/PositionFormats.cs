using System.Globalization;
using System.Text;
using GeoidHeightsDotNet;

namespace MissionPlanner.App.Core.Outputs;

/// <summary>What the position outputs need from the vehicle, in metres, m/s and degrees.</summary>
public sealed record VehicleFix(
    double Lat,
    double Lng,
    double AltMsl,
    double GroundSpeed,
    double Course,
    double Roll,
    double Pitch,
    double Yaw,
    double ClimbRate,
    int FixType,
    int Satellites,
    double Hdop,
    double PressTemp = 0,
    double MagX = 0,
    double MagY = 0,
    double MagZ = 0)
{
    public static VehicleFix From(CurrentState cs) => new(
        cs.lat, cs.lng,
        cs.altasl / CurrentState.multiplieralt,
        cs.groundspeed / CurrentState.multiplierspeed,
        cs.groundcourse, cs.roll, cs.pitch, cs.yaw,
        cs.climbrate / CurrentState.multiplierspeed,
        (int)cs.gpsstatus, (int)cs.satcount, cs.gpshdop,
        cs.press_temp, cs.mx, cs.my, cs.mz);
}

/// <summary>
/// NMEA 0183 sentences of the vehicle position (Mission Planner's NMEA output): GGA, GLL, HDG, VTG,
/// RMC, plus Mission Planner's own $GPRPY (attitude) and $GPHOM (home, every 20th round).
/// </summary>
public static class NmeaOutput
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static IReadOnlyList<string> Sentences(VehicleFix fix, (double Lat, double Lng, double Alt)? home, DateTime utc, int round)
    {
        var lat = Coordinate(fix.Lat, 2);
        var lng = Coordinate(fix.Lng, 3);
        var ns = fix.Lat < 0 ? "S" : "N";
        var ew = fix.Lng < 0 ? "W" : "E";
        var knots = fix.GroundSpeed * 1.943844;
        var kmh = fix.GroundSpeed * 3.6;
        var course = Normalise(fix.Course);
        var heading = Normalise(fix.Yaw);
        var valid = fix.FixType >= 3;
        var time = utc.ToString("HHmmss.ff", Inv);

        var list = new List<string>
        {
            Wrap($"GPGGA,{time},{lat},{ns},{lng},{ew},{(valid ? 1 : 0)},{fix.Satellites:00},{F(fix.Hdop, 1)},{F(fix.AltMsl, 1)},M,{F(GeoidHeights.undulation(fix.Lat, fix.Lng), 1)},M,,"),
            Wrap($"GPGLL,{lat},{ns},{lng},{ew},{time},{(valid ? "A" : "V")},{(valid ? "A" : "N")}"),
            Wrap($"GPHDG,{F(heading, 1)},,,,"),
            Wrap($"GPVTG,{F(course, 1)},T,,M,{F(knots, 2)},N,{F(kmh, 2)},K,{(valid ? "A" : "N")}"),
            Wrap($"GPRMC,{time},{(valid ? "A" : "V")},{lat},{ns},{lng},{ew},{F(knots, 2)},{F(course, 1)},{utc.ToString("ddMMyy", Inv)},,,{(valid ? "A" : "N")}"),
        };

        if (round % 20 == 0 && home is { } h && (h.Lat != 0 || h.Lng != 0))
            list.Add(Wrap($"GPHOM,{time},{Coordinate(h.Lat, 2)},{(h.Lat < 0 ? "S" : "N")},{Coordinate(h.Lng, 3)},{(h.Lng < 0 ? "W" : "E")},{F(h.Alt, 1)},M,"));

        list.Add(Wrap($"GPRPY,{F(fix.Roll, 5)},{F(fix.Pitch, 5)},{F(fix.Yaw, 5)},"));
        return list;
    }

    /// <summary>"$" + body + "*" + checksum (XOR of the body), CR LF.</summary>
    public static string Wrap(string body) => "$" + body + "*" + Checksum(body) + "\r\n";

    public static string Checksum(string body)
    {
        var sum = 0;
        foreach (var c in body)
            sum ^= c;
        return sum.ToString("X2", Inv);
    }

    /// <summary>ddmm.mmmmm (latitude, 2 degree digits) or dddmm.mmmmm (longitude, 3).</summary>
    public static string Coordinate(double degrees, int degreeDigits)
    {
        var abs = Math.Abs(degrees);
        var d = Math.Floor(abs);
        var minutes = (abs - d) * 60;
        if (minutes >= 59.999995)
        {
            d += 1;
            minutes = 0;
        }

        return d.ToString(new string('0', degreeDigits), Inv) + minutes.ToString("00.00000", Inv);
    }

    private static double Normalise(double deg) => ((deg % 360) + 360) % 360;

    private static string F(double v, int decimals) => v.ToString("F" + decimals, Inv);
}

/// <summary>
/// MicroDrone's telemetry lines (Mission Planner's MicroDrone output): #1…#9 with an additive
/// checksum, at 10 Hz.
/// </summary>
public static class MicroDroneOutput
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly DateTime GpsEpoch = new(1980, 1, 6, 0, 0, 0, DateTimeKind.Utc);

    public static IReadOnlyList<string> Lines(VehicleFix fix, DateTime utc, int round)
    {
        var since = utc - GpsEpoch;
        var week = (int)since.TotalDays / 7;
        var seconds = (int)(utc - GpsEpoch.AddDays(week * 7)).TotalSeconds;
        var (x, y, z) = Ecef(fix.Lat, fix.Lng, fix.AltMsl);
        const double d2r = Math.PI / 180;
        string[] lines =
        [
            "#1,28,07,2,1,1,1,2,16000,0,2,",
            string.Format(Inv, "#4,{0},{1},{2},{3},", round / 10, seconds, week, 25),
            string.Format(Inv, "#5,{0},{1},{2},{3},{4},", x * 100, y * 100, z * 100, fix.Hdop + 0.01, fix.Satellites),
            string.Format(Inv, "#6,{0},{1},{2},{3},", fix.GroundSpeed * Math.Sin(fix.Course * d2r), fix.GroundSpeed * Math.Cos(fix.Course * d2r), fix.ClimbRate, 2),
            string.Format(Inv, "#7,{0},{1},{2},", fix.Roll * d2r, fix.Pitch * d2r, fix.Yaw * d2r),
            string.Format(Inv, "#8,{0},{1},{2},", fix.AltMsl, fix.AltMsl, fix.PressTemp),
            string.Format(Inv, "#9,{0},{1},{2},", fix.MagX, fix.MagY, fix.MagZ),
        ];
        return lines.Select(l => l + Checksum(l).ToString(Inv) + "\r\n").ToList();
    }

    /// <summary>Sum of the characters, inverted, as MicroDrone expects.</summary>
    public static byte Checksum(string line)
    {
        byte sum = 0;
        foreach (var c in line)
            sum += (byte)c;
        return (byte)(sum ^ 0xff);
    }

    /// <summary>Earth-centred coordinates as Mission Planner's MicroDrone output computes them.</summary>
    internal static (double X, double Y, double Z) Ecef(double lat, double lng, double alt)
    {
        const double a = 6378137;
        const double f = 1.0 / 298.257223563;
        var d2r = Math.PI / 180;
        var esq = 2 * f - f * f;
        alt *= 0.0001; // as Mission Planner
        var clat = Math.Cos(lat * d2r);
        return ((a + alt) * clat * Math.Cos(lng * d2r), (a + alt) * clat * Math.Sin(lng * d2r), ((1 - esq) * a + alt) * Math.Sin(lat * d2r));
    }
}

/// <summary>Cursor on Target (TAK) events for every vehicle on the link, built by Mission Planner's own CotRound.</summary>
public sealed class CotOutput
{
    private readonly MissionPlanner.Controls.CotRound _round = new();

    public string Type { get; init; } = "a-f-A-M-F-Q";

    public string Callsign { get; init; } = "";

    /// <summary>UID prefix; each vehicle is &lt;prefix&gt;&lt;sysid&gt; (Mission Planner uses NOsysid).</summary>
    public string UidPrefix { get; init; } = "MP-sysid";

    public double IntervalSeconds { get; init; } = 1;

    public IReadOnlyList<string> Round(IEnumerable<MissionPlanner.Controls.CotVehicle> vehicles, DateTime utc)
    {
        var events = _round.Build(vehicles, v => new MissionPlanner.Controls.CotIdentity
            {
                Uid = UidPrefix + v.SysId.ToString(Inv),
                Callsign = Callsign.Length == 0 ? "" : vehiclesCallsign(v),
            }, Type, details: true, utc,
            MissionPlanner.Controls.CotVehicles.StaleSeconds(IntervalSeconds));
        return events.Select(e => MissionPlanner.Utilities.CoT.CotXml.Serialize(e, false).Replace("\r", "") + "\n").ToList();

        string vehiclesCallsign(MissionPlanner.Controls.CotVehicle v) => Callsign + (v.SysId == 1 ? "" : "-" + v.SysId.ToString(Inv));
    }

    /// <summary>Already-stale events for every track, sent when the output stops so TAK greys them at once.</summary>
    public IReadOnlyList<string> Expire(DateTime utc) =>
        _round.ExpireAll(utc).Select(e => MissionPlanner.Utilities.CoT.CotXml.Serialize(e, false).Replace("\r", "") + "\n").ToList();

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
}
