using System.Globalization;
using System.Text.RegularExpressions;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Comms;

/// <summary>
/// One radio setting from an ATI5 reply: <c>S3:NETID=25</c>, or the RFD query form
/// <c>S2:AIR_SPEED(L)[4..1000]=125{4,64,125,250,500,1000,}</c> that also carries the range and choices.
/// </summary>
public sealed record SikParam(string Designator, string Name, int Value, int? Min = null, int? Max = null,
    IReadOnlyList<int>? Choices = null);

/// <summary>What ATI/ATI2/ATI3/ATI5/ATI7 said about one radio.</summary>
public sealed record SikRadioInfo(
    string Version,
    int? BoardId,
    int? FrequencyCode,
    IReadOnlyList<SikParam> Parameters,
    string Rssi = "",
    string? AesKey = null)
{
    public string BoardName => SikParser.BoardName(BoardId);

    public string FrequencyName => SikParser.FrequencyName(FrequencyCode);

    /// <summary>RFD multipoint firmware answers "[n] ..." and has its own parameter set.</summary>
    public bool IsMultipoint { get; init; }

    public bool IsRfd900X => BoardId is 0x83 or 0x84 or 0x85 or 0x88;

    public bool IsRfd => BoardId is 0x42 or 0x43 or 0x81 or 0x82 or 0x83 or 0x84 or 0x85 or 0x88;

    public SikParam? Find(string name) => Parameters.FirstOrDefault(p => p.Name == name);
}

/// <summary>Parses the SiK / RFD AT command replies.</summary>
public static partial class SikParser
{
    [GeneratedRegex(@"^\s*(?:\[\d+\]\s*)?(?<des>[A-Z&]+\d*)\s*:\s*(?<name>[A-Za-z0-9_/]+)\s*(?:\((?<type>[^)]*)\))?\s*(?:\[(?<min>-?\d+)\.\.(?<max>-?\d+)\])?\s*=\s*(?<value>-?\d+)\s*(?:\{(?<opts>[^}]*)\})?",
        RegexOptions.CultureInvariant)]
    private static partial Regex ParamLine();

    [GeneratedRegex(@"^\s*\[(\d+)\]\s*", RegexOptions.CultureInvariant)]
    private static partial Regex MultipointPrefix();

    [GeneratedRegex(@"SiK\s+(.*)\s+on\s+(.*)", RegexOptions.CultureInvariant)]
    private static partial Regex SikBanner();

    /// <summary>Settings in an ATI5 (or ATI5?) reply, in the radio's order; "RESERVED" slots are skipped.</summary>
    public static IReadOnlyList<SikParam> ParseParameters(string ati5)
    {
        var result = new List<SikParam>();
        foreach (var raw in ati5.Split('\n', '\r'))
        {
            var m = ParamLine().Match(raw);
            if (!m.Success)
                continue;
            var name = m.Groups["name"].Value;
            if (name == "RESERVED" || result.Any(p => p.Name == name))
                continue;
            if (!int.TryParse(m.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                continue;

            int? min = null, max = null;
            if (m.Groups["min"].Success && m.Groups["max"].Success)
            {
                min = int.Parse(m.Groups["min"].Value, CultureInfo.InvariantCulture);
                max = int.Parse(m.Groups["max"].Value, CultureInfo.InvariantCulture);
            }

            IReadOnlyList<int>? choices = null;
            if (m.Groups["opts"].Success)
            {
                choices = m.Groups["opts"].Value
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : (int?)null)
                    .Where(v => v != null)
                    .Select(v => v!.Value)
                    .ToList();
                if (choices.Count == 0)
                    choices = null;
            }

            result.Add(new SikParam(m.Groups["des"].Value, name, value, min, max, choices));
        }

        return result;
    }

    /// <summary>True when replies start with "[node]", as RFD multipoint firmware does.</summary>
    public static bool HasMultipointPrefix(string reply) => MultipointPrefix().IsMatch(reply);

    public static string StripMultipointPrefix(string reply) => MultipointPrefix().Replace(reply.Trim(), "").Trim();

    /// <summary>ATI2 / ATI3 print a number, decimal on SiK, sometimes "0x..".</summary>
    public static int? ParseNumber(string reply)
    {
        var text = StripMultipointPrefix(reply).Trim();
        if (text.Length == 0)
            return null;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) ? h : null;
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    /// <summary>An ATI reply from a SiK or RFD radio ("SiK 2.0 on HM-TRP", "RFD SiK 2.65 on RFD900X R1.3").</summary>
    public static bool IsVersionBanner(string reply) => SikBanner().IsMatch(reply);

    public static string BoardName(int? id) => id switch
    {
        null => "",
        0x4d => "RF50",
        0x4e => "HM-TRP",
        0x42 => "RFD900",
        0x43 => "RFD900A",
        0x50 => "HB1060",
        0x81 => "RFD900U",
        0x82 => "RFD900P",
        0x83 => "RFD900X",
        0x84 => "RFD900X2",
        0x85 => "RFD900UX2",
        0x88 => "RFD900UX",
        _ => $"0x{id.Value:X2}",
    };

    public static string FrequencyName(int? code) => code switch
    {
        null => "",
        0x43 => "433 MHz",
        0x47 => "470 MHz",
        0x86 => "868 MHz",
        0x91 => "915 MHz",
        0xf0 => "none",
        _ => $"0x{code.Value:X2}",
    };

    /// <summary>The encryption key reply to AT&amp;E?: hex digits, or null for ERROR / no encryption support.</summary>
    public static string? ParseAesKey(string reply)
    {
        var text = StripMultipointPrefix(reply).Trim();
        if (text.Length == 0 || text.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || text == "OK")
            return null;
        return text.All(Uri.IsHexDigit) ? text : null;
    }
}

public enum SikEditorKind
{
    /// <summary>On/off (ECC, OPPRESEND...).</summary>
    Flag,

    /// <summary>Pick from a list (air speed, serial speed, MAVLink framing...).</summary>
    Choice,

    /// <summary>A number in a range (NETID, frequencies...).</summary>
    Number,

    /// <summary>Shown, never written (FORMAT, the EEPROM layout version).</summary>
    ReadOnly,
}

public sealed record SikChoice(int Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>How the page shows and edits one setting.</summary>
public sealed record SikParamInfo(string Label, string Description, SikEditorKind Kind, IReadOnlyList<SikChoice> Choices,
    int Min, int Max, int Step = 1);

/// <summary>
/// Labels, help text and the value lists Mission Planner's SiK page offers, per board and band
/// (Radio/Sikradio.cs); ranges and choices the radio reports itself take precedence.
/// </summary>
public static class SikCatalog
{
    /// <summary>Settings Mission Planner's "Copy required to remote" copies: these must match for the radios to link.</summary>
    public static readonly IReadOnlyList<string> MustMatch =
        ["AIR_SPEED", "NETID", "ECC", "MAVLINK", "MIN_FREQ", "MAX_FREQ", "NUM_CHANNELS", "MAX_WINDOW", "ENCRYPTION_LEVEL"];

    private static readonly HashSet<string> Flags =
        ["ECC", "OPPRESEND", "MANCHESTER", "RTSCTS", "GPI1_1R/CIN", "GPO1_1R/COUT", "GPO1_3STATLED", "GPI1_2AUXIN", "GPO1_3AUXOUT", "GPO1_0TXEN485"];

    private static readonly Dictionary<string, (string Label, string Help)> Text = new()
    {
        ["FORMAT"] = ("Format", "EEPROM format version of the firmware (read only)."),
        ["SERIAL_SPEED"] = ("Serial speed", "Serial baud rate in rounded kbps: 57 means 57600."),
        ["AIR_SPEED"] = ("Air speed", "Inter-radio data rate in rounded kbps. Values below 16 make frequency hopping and TDM sync slow."),
        ["NETID"] = ("Net ID", "Network ID: seeds the hopping sequence and tells your radios' packets apart. Use one nobody else nearby uses."),
        ["TXPOWER"] = ("Tx power", "Transmit power in dBm (20 dBm = 100 mW). Lower it for short range testing."),
        ["ECC"] = ("ECC", "Golay error correction: halves the air bandwidth but corrects up to 3 bit errors per 12 bits. Use for long range with a lower air speed."),
        ["MAVLINK"] = ("MAVLink", "Framing: raw data, MAVLink (packets aligned, RADIO_STATUS injected) or low latency."),
        ["OPPRESEND"] = ("Op resend", "Opportunistic resend: send packets twice when the serial buffer is nearly empty."),
        ["MIN_FREQ"] = ("Min freq", "Lowest frequency in kHz."),
        ["MAX_FREQ"] = ("Max freq", "Highest frequency in kHz."),
        ["NUM_CHANNELS"] = ("# of channels", "Number of frequency hopping channels."),
        ["DUTY_CYCLE"] = ("Duty cycle", "Percentage of time the radio may transmit."),
        ["LBT_RSSI"] = ("LBT RSSI", "Listen-before-talk threshold; 0 disables."),
        ["MANCHESTER"] = ("Manchester", "Manchester encoding."),
        ["RTSCTS"] = ("RTS/CTS", "Hardware flow control on the serial port."),
        ["MAX_WINDOW"] = ("Max window (ms)", "Longest transmit window in milliseconds; lower values reduce latency."),
        ["ENCRYPTION_LEVEL"] = ("Encryption", "AES encryption level (0 = off). Both radios need the same level and key."),
        ["NODEID"] = ("Node ID", "Multipoint: this radio's node ID."),
        ["DESTID"] = ("Dest ID", "Multipoint: node to send to."),
        ["TX_ENCAP_METHOD"] = ("Tx encapsulation", "Multipoint: transmit encapsulation method."),
        ["RX_ENCAP_METHOD"] = ("Rx encapsulation", "Multipoint: receive encapsulation method."),
        ["MAX_DATA"] = ("Max data", "Multipoint: largest data packet."),
        ["MAX_RETRIES"] = ("Max retries", "Multipoint: retries per packet."),
        ["GLOBAL_RETRIES"] = ("Global retries", "Multipoint: retries for broadcast packets."),
        ["SER_BRK_DETMS"] = ("Serial break (ms)", "Serial break detection time."),
        ["FSFRAMELOSS"] = ("Failsafe frame loss", "Missing SBUS/PPM output frames before failsafe."),
        ["GPI1_1R/CIN"] = ("R/C in (PPM)", "Use GPIO 1.1 as R/C (PPM) input."),
        ["GPO1_1R/COUT"] = ("R/C out (PPM)", "Use GPIO 1.1 as R/C (PPM) output."),
        ["GPO1_3STATLED"] = ("Status LED out", "Drive GPIO 1.3 as a status LED."),
        ["GPI1_2AUXIN"] = ("Aux in", "GPIO 1.2 auxiliary input."),
        ["GPO1_3AUXOUT"] = ("Aux out", "GPIO 1.3 auxiliary output."),
        ["GPO1_0TXEN485"] = ("RS-485 Tx enable", "GPIO 1.0 drives an RS-485 transmit enable."),
        ["ANT_MODE"] = ("Antenna mode", "Antenna diversity mode."),
        ["TARGET_RSSI"] = ("Target RSSI", "Adaptive power control target."),
        ["HYSTERESIS_RSSI"] = ("Hysteresis RSSI", "Adaptive power control hysteresis."),
    };

    private static IReadOnlyList<SikChoice> Plain(IEnumerable<int> values) => values.Select(v => new SikChoice(v, v.ToString(CultureInfo.InvariantCulture))).ToList();

    private static IReadOnlyList<SikChoice> Bauds(IEnumerable<int> kbps) =>
        kbps.Select(v => new SikChoice(v, v switch
        {
            1 => "1200",
            2 => "2400",
            4 => "4800",
            9 => "9600",
            19 => "19200",
            38 => "38400",
            57 => "57600",
            111 => "111100",
            115 => "115200",
            230 => "230400",
            460 => "460800",
            _ => v + "k",
        })).ToList();

    public static string Label(string name) => Text.TryGetValue(name, out var t) ? t.Label : name;

    public static string Description(string name) => Text.TryGetValue(name, out var t) ? t.Help : "";

    /// <summary>The editor for <paramref name="param"/> on a radio described by <paramref name="radio"/>.</summary>
    public static SikParamInfo Describe(SikParam param, SikRadioInfo radio)
    {
        var name = param.Name;
        var label = Label(name);
        var help = Description(name);

        if (name == "FORMAT")
            return new SikParamInfo(label, help, SikEditorKind.ReadOnly, [], param.Value, param.Value);

        // the radio told us its choices (RFD query form)
        if (param.Choices is { Count: > 0 } reported)
        {
            var list = name switch
            {
                "SERIAL_SPEED" => Bauds(reported),
                "MAVLINK" => MavlinkChoices(radio).Where(c => reported.Contains(c.Value)).ToList() is { Count: > 0 } named
                    ? named
                    : Plain(reported),
                _ => Plain(reported),
            };
            return new SikParamInfo(label, help, SikEditorKind.Choice, WithValue(list, param.Value), reported.Min(), reported.Max());
        }

        if (Flags.Contains(name) || param is { Min: 0, Max: 1 })
            return new SikParamInfo(label, help, SikEditorKind.Flag, [], 0, 1);

        if (param is { Min: { } rmin, Max: { } rmax })
            return new SikParamInfo(label, help, SikEditorKind.Number, [], rmin, rmax, StepFor(name));

        switch (name)
        {
            case "SERIAL_SPEED":
                return Choice(radio.IsRfd900X ? Bauds([1, 2, 4, 9, 19, 38, 57, 115, 230, 460]) : Bauds([1, 2, 4, 9, 19, 38, 57, 111, 115]));
            case "AIR_SPEED":
                return Choice(radio.IsRfd900X ? Plain([4, 64, 125, 250, 500]) : Plain([2, 4, 8, 16, 19, 24, 32, 48, 64, 96, 128, 192, 250]));
            case "TXPOWER":
                return radio.IsRfd ? Number(0, 30) : Choice(Plain([1, 2, 5, 8, 11, 14, 17, 20]));
            case "MAVLINK":
                return Choice(MavlinkChoices(radio));
            case "NETID":
                return radio.IsRfd900X ? Number(0, radio.Version.Contains("ASYNC") ? 255 : 65535) : Number(0, 499);
            case "MIN_FREQ":
            case "MAX_FREQ":
            {
                var (lo, hi, step) = FrequencyRange(radio, name == "MAX_FREQ");
                return Number(lo, hi, step);
            }
            case "NUM_CHANNELS":
                return Number(1, 50);
            case "DUTY_CYCLE":
                return Number(10, 100, 10);
            case "LBT_RSSI":
                return radio.IsRfd900X ? Number(0, 220, 25) : Number(0, 255);
            case "MAX_WINDOW":
                return radio.IsRfd900X ? Number(20, 400) : Number(33, 131);
            case "ENCRYPTION_LEVEL":
                return Choice([new SikChoice(0, "Off"), new SikChoice(1, "AES-128"), new SikChoice(2, "AES-256")]);
            default:
                return Number(0, 65535);
        }

        SikParamInfo Choice(IReadOnlyList<SikChoice> choices) =>
            new(label, help, SikEditorKind.Choice, WithValue(choices, param.Value), choices.Min(c => c.Value), choices.Max(c => c.Value));

        SikParamInfo Number(int min, int max, int step = 1) =>
            new(label, help, SikEditorKind.Number, [], Math.Min(min, param.Value), Math.Max(max, param.Value), step);
    }

    private static IReadOnlyList<SikChoice> MavlinkChoices(SikRadioInfo radio) =>
        radio.IsMultipoint
            ? [new SikChoice(0, "Raw data"), new SikChoice(1, "MAVLink")]
            : [new SikChoice(0, "Raw data"), new SikChoice(1, "MAVLink"), new SikChoice(2, "Low latency")];

    private static int StepFor(string name) => name is "MIN_FREQ" or "MAX_FREQ" ? 50 : 1;

    private static (int Lo, int Hi, int Step) FrequencyRange(SikRadioInfo radio, bool max) => radio.FrequencyCode switch
    {
        0x43 => (414000, 460000, 50),
        0x47 => (470000, 510000, 50),
        0x86 => (849000, 889000, 1000),
        0x91 => max ? (903000, 928000, 1000) : (902000, 927000, 1000),
        _ => (0, 1_000_000, 1),
    };

    /// <summary>Choices with the radio's current value added if the standard list lacks it, so it is never lost.</summary>
    private static IReadOnlyList<SikChoice> WithValue(IReadOnlyList<SikChoice> choices, int value)
    {
        if (choices.Any(c => c.Value == value))
            return choices;
        return choices.Append(new SikChoice(value, value.ToString(CultureInfo.InvariantCulture)))
            .OrderBy(c => c.Value).ToList();
    }
}
