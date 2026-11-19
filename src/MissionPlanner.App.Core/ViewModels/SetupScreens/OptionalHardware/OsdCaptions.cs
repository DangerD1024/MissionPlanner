namespace MissionPlanner.App.Core.ViewModels.SetupScreens.OptionalHardware;

/// <summary>
/// Sample text for OSD elements on the layout editor, about as wide as ArduPilot draws them
/// (Mission Planner's OsdItemCaptionProvider, with plain characters instead of the MAX7456 font).
/// </summary>
public static class OsdCaptions
{
    private static readonly Dictionary<string, string> Samples = new(StringComparer.Ordinal)
    {
        ["ALTITUDE"] = " 11m",
        ["ASPEED"] = "A 10m/s",
        ["ASPD1"] = "A 10m/s",
        ["ASPD2"] = "A 10m/s",
        ["ACRVOLT"] = "12.6V",
        ["ATEMP"] = "25°C",
        ["AVGCELLV"] = "▮3.85V",
        ["BAT2_VLT"] = "▮11.8V",
        ["BAT2USED"] = "125mAh",
        ["BATTBAR"] = "[■■■■■□□]",
        ["BATUSED"] = "125mAh",
        ["BAT_VOLT"] = "▮11.8V",
        ["BTEMP"] = "25°C",
        ["CALLSIGN"] = "TOPGUN",
        ["CELLVOLT"] = "▮3.85V",
        ["CLIMBEFF"] = "↑ 2.1m/Ah",
        ["CLK"] = "◷12:00",
        ["COMPASS"] = "N-+-E-+-S",
        ["CRSSHAIR"] = "-+-",
        ["CURRENT"] = "8.3A",
        ["CURRENT2"] = "8.3A",
        ["DIST"] = "↔ 1.2km",
        ["EFF"] = "100mAh/km",
        ["ESCAMPS"] = "8.3A",
        ["ESCRPM"] = "10kRPM",
        ["ESCTEMP"] = "25°C",
        ["FENCE"] = "⌂F",
        ["FLTIME"] = "✈03:00",
        ["FLTMODE"] = "STAB*",
        ["GPSLAT"] = "LAT  50.3534305",
        ["GPSLONG"] = "LON  30.5003901",
        ["GSPEED"] = "G→ 17km/h",
        ["HDOP"] = "HD 10.2",
        ["HEADING"] = "32°",
        ["HOME"] = "H→ 101m",
        ["HOMEDIR"] = "H→",
        ["HOMEDIST"] = "H 101m",
        ["HORIZON"] = "---<+>---",
        ["LINK_Q"] = "99LQ",
        ["MESSAGE"] = "Messages appear here",
        ["PITCH"] = "P 10°",
        ["PLUSCODE"] = "MP97+Q6H",
        ["POWER"] = "100W",
        ["RC_ANT"] = "ANT1",
        ["RC_LQ"] = "LQ 99",
        ["RC_PWR"] = "25mW",
        ["RC_SNR"] = "SNR 9",
        ["RESTVOLT"] = "▮11.8V",
        ["RNGF"] = "▼ 1.23m",
        ["ROLL"] = "R  3°",
        ["RPM"] = "8000RPM",
        ["RSSI"] = "RS93",
        ["RSSIDBM"] = "-70dBm",
        ["SATS"] = "SAT13",
        ["SIDEBARS"] = "|            |",
        ["STATS"] = "Flight stats",
        ["TEMP"] = "25°C",
        ["TER_HGT"] = "T 25m",
        ["THROTTLE"] = "  0%",
        ["VSPEED"] = "↑ 0m/s",
        ["VTX_PWR"] = "25mW",
        ["WAYPOINT"] = "WP 1 120m",
        ["WIND"] = "W→ 5m/s",
        ["XTRACK"] = "XTE",
        ["ARMING"] = "ARMED",
        ["HGT_ABVTERR"] = "T 25m",
    };

    /// <summary>Text shown for the element: a sample of its output, or its name.</summary>
    public static string For(string name, bool names = false)
    {
        if (names)
            return name;
        if (Samples.TryGetValue(name, out var s))
            return s;
        if (name.StartsWith("PARAM", StringComparison.Ordinal))
            return name + " 0.00";
        return name;
    }
}
