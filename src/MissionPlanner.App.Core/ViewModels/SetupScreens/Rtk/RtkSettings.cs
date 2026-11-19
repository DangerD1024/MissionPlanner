using System.Globalization;
using System.Text.Json;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Rtk;

/// <summary>The RTK page's choices, kept in the app settings under "next_rtk_*".</summary>
public sealed record RtkSettings
{
    public RtcmSourceKind Source { get; init; } = RtcmSourceKind.Ntrip;

    public string NtripHost { get; init; } = "";
    public int NtripPort { get; init; } = 2101;
    public string NtripMountpoint { get; init; } = "";
    public string NtripUsername { get; init; } = "";
    public string NtripPassword { get; init; } = "";
    public bool NtripTls { get; init; }
    public bool NtripV1 { get; init; }
    public bool SendGga { get; init; } = true;

    public string SerialPort { get; init; } = "";
    public int SerialBaud { get; init; } = 115200;
    public bool UbloxConfigure { get; init; }
    public bool UbloxMsm4 { get; init; }
    public int SurveyInSeconds { get; init; } = 60;
    public double SurveyInAccuracyM { get; init; } = 2.0;
    public bool UseFixedPosition { get; init; }
    public double FixedLat { get; init; }
    public double FixedLng { get; init; }
    public double FixedAlt { get; init; }

    public string TcpHost { get; init; } = "127.0.0.1";
    public int TcpPort { get; init; } = 2101;
    public int UdpPort { get; init; } = 9000;

    public bool LogRaw { get; init; } = true;

    private const string P = "next_rtk_";

    public static RtkSettings Load(Settings s)
    {
        var d = new RtkSettings();
        return new RtkSettings
        {
            Source = Enum.TryParse<RtcmSourceKind>(s[P + "source"], out var src) ? src : d.Source,
            NtripHost = s.GetString(P + "ntrip_host", d.NtripHost),
            NtripPort = s.GetInt32(P + "ntrip_port", d.NtripPort),
            NtripMountpoint = s.GetString(P + "ntrip_mount", d.NtripMountpoint),
            NtripUsername = s.GetString(P + "ntrip_user", d.NtripUsername),
            NtripPassword = s.GetString(P + "ntrip_pass", d.NtripPassword),
            NtripTls = s.GetBoolean(P + "ntrip_tls", d.NtripTls),
            NtripV1 = s.GetBoolean(P + "ntrip_v1", d.NtripV1),
            SendGga = s.GetBoolean(P + "ntrip_gga", d.SendGga),
            SerialPort = s.GetString(P + "serial_port", d.SerialPort),
            SerialBaud = s.GetInt32(P + "serial_baud", d.SerialBaud),
            UbloxConfigure = s.GetBoolean(P + "ublox", d.UbloxConfigure),
            UbloxMsm4 = s.GetBoolean(P + "ublox_msm4", d.UbloxMsm4),
            SurveyInSeconds = s.GetInt32(P + "svin_time", d.SurveyInSeconds),
            SurveyInAccuracyM = Dbl(s, P + "svin_acc", d.SurveyInAccuracyM),
            UseFixedPosition = s.GetBoolean(P + "fixed", d.UseFixedPosition),
            FixedLat = Dbl(s, P + "fixed_lat", 0),
            FixedLng = Dbl(s, P + "fixed_lng", 0),
            FixedAlt = Dbl(s, P + "fixed_alt", 0),
            TcpHost = s.GetString(P + "tcp_host", d.TcpHost),
            TcpPort = s.GetInt32(P + "tcp_port", d.TcpPort),
            UdpPort = s.GetInt32(P + "udp_port", d.UdpPort),
            LogRaw = s.GetBoolean(P + "lograw", d.LogRaw),
        };
    }

    public void Save(Settings s)
    {
        var inv = CultureInfo.InvariantCulture;
        s[P + "source"] = Source.ToString();
        s[P + "ntrip_host"] = NtripHost;
        s[P + "ntrip_port"] = NtripPort.ToString(inv);
        s[P + "ntrip_mount"] = NtripMountpoint;
        s[P + "ntrip_user"] = NtripUsername;
        s[P + "ntrip_pass"] = NtripPassword;
        s[P + "ntrip_tls"] = NtripTls.ToString();
        s[P + "ntrip_v1"] = NtripV1.ToString();
        s[P + "ntrip_gga"] = SendGga.ToString();
        s[P + "serial_port"] = SerialPort;
        s[P + "serial_baud"] = SerialBaud.ToString(inv);
        s[P + "ublox"] = UbloxConfigure.ToString();
        s[P + "ublox_msm4"] = UbloxMsm4.ToString();
        s[P + "svin_time"] = SurveyInSeconds.ToString(inv);
        s[P + "svin_acc"] = SurveyInAccuracyM.ToString("R", inv);
        s[P + "fixed"] = UseFixedPosition.ToString();
        s[P + "fixed_lat"] = FixedLat.ToString("R", inv);
        s[P + "fixed_lng"] = FixedLng.ToString("R", inv);
        s[P + "fixed_alt"] = FixedAlt.ToString("R", inv);
        s[P + "tcp_host"] = TcpHost;
        s[P + "tcp_port"] = TcpPort.ToString(inv);
        s[P + "udp_port"] = UdpPort.ToString(inv);
        s[P + "lograw"] = LogRaw.ToString();
    }

    private static double Dbl(Settings s, string key, double fallback) =>
        double.TryParse(s[key], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}

/// <summary>A base position the user named and kept (Mission Planner's base position list).</summary>
public sealed record SavedBasePosition(string Name, double Lat, double Lng, double Alt);

/// <summary>Saved base positions in "next_rtk_bases.json" in the data folder.</summary>
public static class SavedBasePositions
{
    public static string FilePath => Path.Combine(Settings.GetUserDataDirectory(), "next_rtk_bases.json");

    public static List<SavedBasePosition> Load(string? path = null)
    {
        try
        {
            path ??= FilePath;
            if (!File.Exists(path))
                return new List<SavedBasePosition>();
            return JsonSerializer.Deserialize<List<SavedBasePosition>>(File.ReadAllText(path)) ?? new List<SavedBasePosition>();
        }
        catch (Exception)
        {
            return new List<SavedBasePosition>();
        }
    }

    public static void Save(IEnumerable<SavedBasePosition> list, string? path = null)
    {
        path ??= FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(list.ToList(), new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>Bytes per second from a growing total, averaged over the last second or two.</summary>
public sealed class RateTracker
{
    private long _lastTotal;
    private DateTime _lastTime;
    private bool _started;

    public double Rate { get; private set; }

    public double Update(long total, DateTime now)
    {
        if (!_started || total < _lastTotal)
        {
            _started = true;
            _lastTotal = total;
            _lastTime = now;
            Rate = 0;
            return Rate;
        }

        var dt = (now - _lastTime).TotalSeconds;
        if (dt >= 1.0)
        {
            Rate = (total - _lastTotal) / dt;
            _lastTotal = total;
            _lastTime = now;
        }

        return Rate;
    }

    public void Reset()
    {
        _started = false;
        Rate = 0;
    }
}
