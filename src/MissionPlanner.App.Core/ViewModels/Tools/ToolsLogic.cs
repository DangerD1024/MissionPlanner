using System.Globalization;
using System.Text;
using MissionPlanner.Utilities;
using Newtonsoft.Json;

namespace MissionPlanner.App.Core.ViewModels.Tools;

/// <summary>The commands the Ctrl+F screen sends, with Mission Planner's magic numbers.</summary>
public static class ToolCommands
{
    /// <summary>PREFLIGHT_CALIBRATION param5 = 76: mark the accelerometers calibrated (after restoring parameters).</summary>
    public static readonly ViewModels.FlightData.VehicleCommand ForceAccelCalibrated =
        new(MAVLink.MAV_CMD.PREFLIGHT_CALIBRATION, P5: 76);

    /// <summary>PREFLIGHT_CALIBRATION param2 = 76: mark the compasses calibrated.</summary>
    public static readonly ViewModels.FlightData.VehicleCommand ForceCompassCalibrated =
        new(MAVLink.MAV_CMD.PREFLIGHT_CALIBRATION, P2: 76);

    /// <summary>FLASH_BOOTLOADER with the magic 290876: the autopilot writes the bootloader its firmware carries.</summary>
    public static readonly ViewModels.FlightData.VehicleCommand FlashBootloader =
        new(MAVLink.MAV_CMD.FLASH_BOOTLOADER, P5: 290876);

    /// <summary>PREFLIGHT_REBOOT_SHUTDOWN 42,24,71,93: deliberately hang the autopilot (watchdog test).</summary>
    public static readonly ViewModels.FlightData.VehicleCommand Lockup =
        new(MAVLink.MAV_CMD.PREFLIGHT_REBOOT_SHUTDOWN, 42, 24, 71, 93);

    /// <summary>PREFLIGHT_REBOOT_SHUTDOWN 42,24,71,99: reboot into the STM32 DFU bootloader.</summary>
    public static readonly ViewModels.FlightData.VehicleCommand RebootToDfu =
        new(MAVLink.MAV_CMD.PREFLIGHT_REBOOT_SHUTDOWN, 42, 24, 71, 99);
}

/// <summary>Ground pressure (QNH and the baro height nudge), as Mission Planner's temp screen.</summary>
public static class GroundPressure
{
    /// <summary>Pascals per metre near sea level, as Mission Planner (338.6 Pa = 100 ft).</summary>
    public const double PascalsPerMetre = 11.1;

    /// <summary>The ground-pressure parameter the vehicle has (newer firmware renamed it).</summary>
    public static string? ParamName(Func<string, bool> has) =>
        has("GND_ABS_PRESS") ? "GND_ABS_PRESS" : has("BARO1_GND_PRESS") ? "BARO1_GND_PRESS" : null;

    /// <summary>A typed QNH in pascals (101325) or hectopascals (1013.25); null when it makes no sense.</summary>
    public static double? ParseQnh(string text)
    {
        if (!double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return null;
        if (v is > 800 and < 1200)
            v *= 100;
        return v is >= 80000 and <= 120000 ? v : null;
    }

    /// <summary>The ground pressure that moves the reported altitude by <paramref name="metres"/> (higher = reads higher).</summary>
    public static double ForOffset(double basePascals, double metres) => basePascals + metres * PascalsPerMetre;
}

/// <summary>SET_MESSAGE_INTERVAL for one message, as Mission Planner's Message Interval tool.</summary>
public static class MessageIntervals
{
    /// <summary>Every MAVLink message name, sorted.</summary>
    public static IReadOnlyList<string> Names { get; } =
        Enum.GetNames<MAVLink.MAVLINK_MSG_ID>().Order(StringComparer.Ordinal).ToList();

    /// <summary>The interval parameter: microseconds for a rate in Hz; 0 = the default rate, -1 = stop sending.</summary>
    public static float IntervalFor(double rateHz) => rateHz <= 0 ? (float)rateHz : (float)(1e6 / rateHz);

    public static ViewModels.FlightData.VehicleCommand Command(MAVLink.MAVLINK_MSG_ID id, double rateHz) =>
        new(MAVLink.MAV_CMD.SET_MESSAGE_INTERVAL, (float)(int)id, IntervalFor(rateHz));
}

/// <summary>Turns typed bytes into a MAVLink packet and back into fields (Mission Planner's packet byte tools).</summary>
public static class PacketDecoder
{
    /// <summary>
    /// Bytes from text: "0xfd 0x05 …", "fd,05,…", "253 5 …" (decimal), or one run of hex digits
    /// "fd0500…". Hex is assumed when the text has 0x or a letter a–f.
    /// </summary>
    public static byte[] ParseBytes(string input)
    {
        var text = input.Trim();
        if (text.Length == 0)
            return [];

        var isHex = text.Contains("0x", StringComparison.OrdinalIgnoreCase) ||
                    text.ToLowerInvariant().Any(c => c is >= 'a' and <= 'f');
        var tokens = text.Replace("0x", ",", StringComparison.OrdinalIgnoreCase)
            .Split([',', ' ', '\t', '\r', '\n', ';', ':', '-'], StringSplitOptions.RemoveEmptyEntries);

        // one long run of hex digits: two characters per byte
        if (tokens.Length == 1 && tokens[0].Length > 3 && tokens[0].Length % 2 == 0 && tokens[0].All(Uri.IsHexDigit))
        {
            var run = tokens[0];
            return Enumerable.Range(0, run.Length / 2).Select(i => Convert.ToByte(run.Substring(i * 2, 2), 16)).ToArray();
        }

        return tokens.Select(t => isHex ? Convert.ToByte(t, 16) : (byte)int.Parse(t, CultureInfo.InvariantCulture)).ToArray();
    }

    /// <summary>The packet's name and fields as JSON, or why it could not be decoded.</summary>
    public static (bool Ok, string Text) Decode(string input)
    {
        byte[] bytes;
        try
        {
            bytes = ParseBytes(input);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            return (false, "Not a list of bytes: " + ex.Message);
        }

        if (bytes.Length == 0)
            return (false, "Enter the bytes of a MAVLink packet.");
        if (bytes[0] != MAVLink.MAVLINK_STX && bytes[0] != MAVLink.MAVLINK_STX_MAVLINK1)
            return (false, $"A MAVLink packet starts with 0xFD (MAVLink 2) or 0xFE (MAVLink 1), not 0x{bytes[0]:X2}.");

        MAVLink.MAVLinkMessage? packet;
        try
        {
            packet = new MAVLink.MavlinkParse().ReadPacket(new MemoryStream(bytes));
        }
        catch (Exception ex)
        {
            return (false, "Could not decode the packet: " + ex.Message);
        }

        if (packet == null)
            return (false, "Could not decode the packet (bad length or CRC).");

        var sb = new StringBuilder();
        sb.AppendLine($"{packet.msgtypename}  (id {packet.msgid}, sysid {packet.sysid}, compid {packet.compid}, seq {packet.seq}, " +
                      (packet.ismavlink2 ? "MAVLink 2" : "MAVLink 1") + ")");
        sb.Append(packet.data?.ToJSON(Formatting.Indented) ?? "(no payload)");
        return (true, sb.ToString());
    }
}

/// <summary>Decodes typed device-ID numbers (COMPASS_DEV_ID, INS_ACC_ID, …) into bus, address and type.</summary>
public static class DeviceIdDecoder
{
    /// <summary>One line per number found; a word before the number (a parameter name) picks the device kind.</summary>
    public static string Decode(string input)
    {
        var sb = new StringBuilder();
        foreach (var line in input.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var words = line.Split([' ', '\t', ',', '='], StringSplitOptions.RemoveEmptyEntries);
            var name = words.FirstOrDefault(w => !uint.TryParse(w, out _)) ?? "";
            foreach (var word in words)
            {
                if (!uint.TryParse(word, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                    continue;
                var dev = new SetupScreens.Calibration.DeviceId(value);
                var kind = name.Length > 0 ? dev.DevTypeName(name) : dev.DevType.ToString(CultureInfo.InvariantCulture);
                sb.AppendLine($"{(name.Length > 0 ? name + " " : "")}{value} = bus type {dev.BusTypeName}, bus {dev.Bus}, " +
                              $"address {dev.Address} (0x{dev.Address:X2}), device type {kind}");
            }
        }

        return sb.Length == 0 ? "Enter one or more device ID numbers (optionally after the parameter name)." : sb.ToString().TrimEnd();
    }
}

/// <summary>
/// Mission Planner's Param Restore: write a saved parameter file to a vehicle, *ENABLE* parameters
/// first (they create others), skipping values that already match, and writing 0 before any *_ID
/// parameter so the autopilot re-detects the device.
/// </summary>
public static class ParamRestorePlan
{
    private static readonly System.Text.RegularExpressions.Regex DeviceIdName =
        new("_ID[0-9]*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>A detected-device ID (COMPASS_DEV_ID2, INS_ACC_ID), not e.g. THR_IDLE.</summary>
    public static bool IsDeviceId(string name) => DeviceIdName.IsMatch(name);

    public sealed record Step(string Name, double Value, bool ZeroFirst);

    public static (IReadOnlyList<Step> Steps, int AlreadySet) Build(IReadOnlyDictionary<string, double> file, Func<string, double?> current)
    {
        var steps = new List<Step>();
        var already = 0;
        foreach (var (name, value) in file
                     .OrderByDescending(p => p.Key.Contains("ENABLE", StringComparison.OrdinalIgnoreCase))
                     .ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            if (current(name) is { } now && Math.Abs(now - value) < 1e-6 * Math.Max(1, Math.Abs(value)))
            {
                already++;
                continue;
            }

            steps.Add(new Step(name, value, IsDeviceId(name)));
        }

        return (steps, already);
    }
}
