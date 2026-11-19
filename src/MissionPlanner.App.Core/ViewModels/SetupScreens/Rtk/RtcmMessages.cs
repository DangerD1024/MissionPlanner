using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Rtk;

public enum GnssSystem
{
    Gps,
    Glonass,
    Galileo,
    Sbas,
    Qzss,
    BeiDou,
    NavIc,
}

/// <summary>Base station antenna position from RTCM 1005/1006.</summary>
public sealed record RtcmBasePosition(double Lat, double Lng, double Alt, int StationId, int MessageType);

/// <summary>One satellite's signal strength from an observation message.</summary>
public sealed record RtcmSatellite(GnssSystem System, int Prn, double Snr);

/// <summary>What the page shows about RTCM 3 message numbers; decoding reuses rtcm3.cs.</summary>
public static class RtcmMessages
{
    public static char Letter(GnssSystem system) => system switch
    {
        GnssSystem.Gps => 'G',
        GnssSystem.Glonass => 'R',
        GnssSystem.Galileo => 'E',
        GnssSystem.Sbas => 'S',
        GnssSystem.Qzss => 'J',
        GnssSystem.BeiDou => 'C',
        GnssSystem.NavIc => 'I',
        _ => '?',
    };

    public static string Name(GnssSystem system) => system switch
    {
        GnssSystem.Gps => "GPS",
        GnssSystem.Glonass => "GLONASS",
        GnssSystem.Galileo => "Galileo",
        GnssSystem.Sbas => "SBAS",
        GnssSystem.Qzss => "QZSS",
        GnssSystem.BeiDou => "BeiDou",
        GnssSystem.NavIc => "NavIC",
        _ => system.ToString(),
    };

    /// <summary>The constellation whose observations a message carries, if any.</summary>
    public static GnssSystem? ObservationSystem(int type) => type switch
    {
        >= 1001 and <= 1004 => GnssSystem.Gps,
        >= 1009 and <= 1012 => GnssSystem.Glonass,
        _ => MsmSystem(type),
    };

    /// <summary>Constellation of a Multiple Signal Message (MSM1-7), or null.</summary>
    public static GnssSystem? MsmSystem(int type)
    {
        if (type < 1071 || type > 1137 || type % 10 == 0 || type % 10 > 7)
            return null;
        return (type / 10) switch
        {
            107 => GnssSystem.Gps,
            108 => GnssSystem.Glonass,
            109 => GnssSystem.Galileo,
            110 => GnssSystem.Sbas,
            111 => GnssSystem.Qzss,
            112 => GnssSystem.BeiDou,
            113 => GnssSystem.NavIc,
            _ => null,
        };
    }

    public static bool IsBasePosition(int type) => type is 1005 or 1006 or 4072;

    public static string Describe(int type)
    {
        if (MsmSystem(type) is { } msm)
            return $"{Name(msm)} MSM{type % 10}";

        return type switch
        {
            1001 => "GPS L1 observations",
            1002 => "GPS L1 observations (extended)",
            1003 => "GPS L1/L2 observations",
            1004 => "GPS L1/L2 observations (extended)",
            1005 => "Base station position",
            1006 => "Base station position and height",
            1007 => "Antenna descriptor",
            1008 => "Antenna descriptor and serial",
            1009 => "GLONASS L1 observations",
            1010 => "GLONASS L1 observations (extended)",
            1011 => "GLONASS L1/L2 observations",
            1012 => "GLONASS L1/L2 observations (extended)",
            1013 => "System parameters",
            1019 => "GPS ephemeris",
            1020 => "GLONASS ephemeris",
            1033 => "Receiver and antenna descriptor",
            1042 => "BeiDou ephemeris",
            1044 => "QZSS ephemeris",
            1045 => "Galileo F/NAV ephemeris",
            1046 => "Galileo I/NAV ephemeris",
            1230 => "GLONASS code-phase biases",
            4072 => "u-blox moving base",
            _ when type >= 4001 && type <= 4095 => "Proprietary",
            _ => "RTCM " + type,
        };
    }

    /// <summary>
    /// Satellites in an MSM frame: the 64-bit satellite mask after the 73-bit MSM header
    /// (bit 1 = PRN 1). Works for every MSM level and constellation.
    /// </summary>
    public static IReadOnlyList<int> MsmSatellites(byte[] frame)
    {
        const int maskBit = 24 + 73;
        if (frame.Length < (maskBit + 64) / 8 + 1 + 3)
            return Array.Empty<int>();

        var prns = new List<int>();
        for (var j = 0; j < 64; j++)
        {
            if (GetBits(frame, maskBit + j, 1) != 0)
                prns.Add(j + 1);
        }

        return prns;
    }

    /// <summary>Satellite count from the header of a legacy observation message (1001-1004, 1009-1012).</summary>
    public static int LegacySatelliteCount(byte[] frame, int type)
    {
        // GPS epoch time is 30 bits, GLONASS 27
        var bit = type is >= 1009 and <= 1012 ? 24 + 12 + 12 + 27 + 1 : 24 + 12 + 12 + 30 + 1;
        if (frame.Length * 8 < bit + 5 + 24)
            return 0;
        return (int)GetBits(frame, bit, 5);
    }

    /// <summary>Base position from a 1005/1006 frame, converted from ECEF with rtcm3.ecef2pos.</summary>
    public static RtcmBasePosition? TryGetBasePosition(RtcmFrame frame)
    {
        try
        {
            double[] ecef;
            int station;
            if (frame.MessageType == 1005)
            {
                var m = new rtcm3.type1005();
                m.Read(frame.Bytes);
                ecef = m.ecefposition;
                station = m.staid;
            }
            else if (frame.MessageType == 1006)
            {
                var m = new rtcm3.type1006();
                m.Read(frame.Bytes);
                ecef = m.ecefposition;
                station = m.staid;
            }
            else
            {
                return null;
            }

            if (ecef[0] == 0 && ecef[1] == 0 && ecef[2] == 0)
                return null;

            var llh = new double[3];
            rtcm3.ecef2pos(ecef, ref llh);
            return new RtcmBasePosition(llh[0] * rtcm3.R2D, llh[1] * rtcm3.R2D, llh[2], station, frame.MessageType);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Per-satellite signal strength from the observation messages rtcm3.cs decodes (1002, 1004,
    /// 1012 and MSM4/MSM7 of GPS, GLONASS, Galileo, QZSS and BeiDou). Other types give none.
    /// </summary>
    public static IReadOnlyList<RtcmSatellite> TryGetSignalStrengths(RtcmFrame frame)
    {
        try
        {
            List<rtcm3.ob>? obs = frame.MessageType switch
            {
                1002 => Read(new rtcm3.type1002(), frame.Bytes),
                1004 => Read(new rtcm3.type1004(), frame.Bytes),
                1012 => Read(new rtcm3.type1012(), frame.Bytes),
                1074 => Read(new rtcm3.type1074(), frame.Bytes),
                1077 => Read(new rtcm3.type1077(), frame.Bytes),
                1084 => Read(new rtcm3.type1084(), frame.Bytes),
                1087 => Read(new rtcm3.type1087(), frame.Bytes),
                1094 => Read(new rtcm3.type1094(), frame.Bytes),
                1097 => Read(new rtcm3.type1097(), frame.Bytes),
                1114 => Read(new rtcm3.type1114(), frame.Bytes),
                1117 => Read(new rtcm3.type1117(), frame.Bytes),
                1124 => Read(new rtcm3.type1124(), frame.Bytes),
                1127 => Read(new rtcm3.type1127(), frame.Bytes),
                _ => null,
            };
            if (obs == null)
                return Array.Empty<RtcmSatellite>();

            var system = ObservationSystem(frame.MessageType) ?? GnssSystem.Gps;
            return obs.Where(o => o.prn > 0).Select(o => new RtcmSatellite(system, o.prn, o.snr)).ToList();
        }
        catch (Exception)
        {
            // a frame rtcm3.cs cannot decode still goes to the vehicle; it just has no bars
            return Array.Empty<RtcmSatellite>();
        }
    }

    private static List<rtcm3.ob> Read(rtcm3.type1002 m, byte[] b) { m.Read(b); return m.obs; }
    private static List<rtcm3.ob> Read(rtcm3.type1004 m, byte[] b) { m.Read(b); return m.obs; }
    private static List<rtcm3.ob> Read(rtcm3.type1012 m, byte[] b) { m.Read(b); return m.obs; }
    private static List<rtcm3.ob> Read(rtcm3.type1074 m, byte[] b) { m.Read(b); return m.obs; }
    private static List<rtcm3.ob> Read(rtcm3.type1077 m, byte[] b) { m.Read(b); return m.obs; }

    internal static ulong GetBits(byte[] buf, int pos, int len)
    {
        ulong bits = 0;
        for (var i = pos; i < pos + len; i++)
            bits = (bits << 1) | (uint)((buf[i >> 3] >> (7 - (i & 7))) & 1);
        return bits;
    }
}
