using System.Globalization;
using System.Text.RegularExpressions;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Firmware;

/// <summary>A USB vendor/product id pair, parsed from any of the spellings Mission Planner meets.</summary>
public readonly partial record struct UsbId(ushort Vid, ushort Pid)
{
    /// <summary>
    /// Parses Windows hardware ids ("USB\VID_1209&amp;PID_5740", with or without a trailing
    /// "&amp;REV_0200" / "&amp;MI_00"), the form ArduPilot's Linux port list produces (no trailing
    /// "&amp;", which APFirmware's own regex misses), manifest USBID entries ("0x1209/0x5740") and
    /// lsusb's "1209:5740". Returns null for anything else.
    /// </summary>
    public static UsbId? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var m = HardwareIdRegex().Match(text);
        if (!m.Success)
            m = ManifestRegex().Match(text);
        if (!m.Success)
            m = ColonRegex().Match(text);
        if (!m.Success)
            return null;

        if (ushort.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var vid) &&
            ushort.TryParse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var pid))
            return new UsbId(vid, pid);
        return null;
    }

    /// <summary>The Windows form, without the trailing '&amp;' (what <c>Linux.GetAllCOMPorts</c> produces).</summary>
    public string HardwareId => $"USB\\VID_{Vid:X4}&PID_{Pid:X4}";

    public override string ToString() => $"{Vid:X4}:{Pid:X4}";

    [GeneratedRegex(@"VID_([0-9a-f]{1,4})&PID_([0-9a-f]{1,4})", RegexOptions.IgnoreCase)]
    private static partial Regex HardwareIdRegex();

    [GeneratedRegex(@"^\s*0x([0-9a-f]{1,4})\s*/\s*0x([0-9a-f]{1,4})\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ManifestRegex();

    [GeneratedRegex(@"^\s*([0-9a-f]{4}):([0-9a-f]{4})\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ColonRegex();
}
