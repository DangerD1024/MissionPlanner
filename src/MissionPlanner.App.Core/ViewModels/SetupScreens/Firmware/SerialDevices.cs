using System.Runtime.InteropServices;
using MissionPlanner.ArduPilot;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Firmware;

/// <summary>A serial port and, when the OS tells us, the USB device behind it.</summary>
public sealed record SerialDevice(string PortName, string? Product = null, string? Manufacturer = null, UsbId? Usb = null,
    string? SerialNumber = null)
{
    /// <summary>The USB product string; ArduPilot boards report their board name ("MatekH743", "CubeOrange-BL").</summary>
    public string Board => Product ?? "";

    public string HardwareId => Usb?.HardwareId ?? "";

    /// <summary>ArduPilot (and PX4) bootloaders add "-BL" to the board's USB product string.</summary>
    public bool LooksLikeBootloader => Board.EndsWith("-BL", StringComparison.OrdinalIgnoreCase) ||
                                       Board.EndsWith(" BL", StringComparison.OrdinalIgnoreCase);

    /// <summary>What ArduPilot's board-detection code expects.</summary>
    public DeviceInfo ToDeviceInfo() => new()
    {
        name = PortName,
        description = Product ?? PortName,
        board = Product ?? "",
        hardwareid = HardwareId,
    };

    public string Description
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Product))
                parts.Add(Product);
            if (!string.IsNullOrEmpty(Manufacturer) && Manufacturer != Product)
                parts.Add(Manufacturer);
            if (Usb is { } u)
                parts.Add(u.ToString());
            return parts.Count == 0 ? "unknown device" : string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// Lists serial ports with the USB product string, vendor and product ids where the OS exposes them.
/// Only reads directory listings and sysfs attributes: no port is opened.
/// </summary>
public static class SerialDeviceScanner
{
    /// <summary>
    /// Linux: every tty backed by a USB device under /sys/class/tty (the same sysfs data
    /// ArduPilot's <c>Linux.GetAllCOMPorts</c> reads, but keeping the /dev path, which that method
    /// replaces with the product string, and without shelling out to bash). Other ports the
    /// Comms port list knows are added without USB details; elsewhere that list is all there is.
    /// </summary>
    public static IReadOnlyList<SerialDevice> Scan()
    {
        var result = new List<SerialDevice>();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            result.AddRange(ScanSysfs("/sys/class/tty", "/dev"));

        var known = new HashSet<string>(result.Select(d => RealPath(d.PortName)), StringComparer.Ordinal);
        foreach (var name in ProbeCandidates(SafePortNames(), RealPath))
        {
            if (known.Add(RealPath(name)))
                result.Add(new SerialDevice(name));
        }

        return result.OrderBy(d => d.Usb == null).ThenBy(d => d.PortName, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Ports worth probing for a bootloader, one name per physical port: /dev/serial/by-id links
    /// collapse onto their target, macOS tty.* onto cu.*, and Bluetooth/rfcomm ports (opening them
    /// starts a radio connection) and usbmon nodes are left out.
    /// </summary>
    public static IReadOnlyList<string> ProbeCandidates(IEnumerable<string> names, Func<string, string> realPath)
    {
        var all = names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();

        // prefer the plain device name over a link to it
        foreach (var name in all.OrderBy(n => n.Contains("/by-id/", StringComparison.Ordinal) || n.Contains("/by-path/", StringComparison.Ordinal))
                     .ThenBy(n => n, StringComparer.Ordinal))
        {
            var lower = name.ToLowerInvariant();
            if (lower.Contains("bluetooth") || lower.Contains("rfcomm") || lower.Contains("usbmon"))
                continue;

            // macOS lists each port twice; the call-out device does not wait for carrier detect
            if (name.StartsWith("/dev/tty.", StringComparison.Ordinal) && all.Contains("/dev/cu." + name["/dev/tty.".Length..]))
                continue;

            if (seen.Add(realPath(name)))
                result.Add(name);
        }

        return result;
    }

    /// <summary>The ports to probe right now.</summary>
    public static IReadOnlyList<string> ProbePorts() => ProbeCandidates(SafePortNames(), RealPath);

    /// <summary>Reads USB tty devices from a sysfs tree (a parameter so tests can build one).</summary>
    internal static IReadOnlyList<SerialDevice> ScanSysfs(string sysClassTty, string devDir)
    {
        var result = new List<SerialDevice>();
        if (!Directory.Exists(sysClassTty))
            return result;

        IEnumerable<string> ttys;
        try
        {
            ttys = Directory.EnumerateFileSystemEntries(sysClassTty).ToList();
        }
        catch (Exception)
        {
            return result;
        }

        foreach (var tty in ttys)
        {
            try
            {
                var device = Path.Combine(tty, "device");
                if (!Directory.Exists(device))
                    continue;

                // walk up from the interface to the USB device, which carries idVendor/idProduct
                var dir = ResolveDirectory(device);
                string? usbDir = null;
                for (var i = 0; i < 4 && dir != null; i++)
                {
                    if (File.Exists(Path.Combine(dir, "idVendor")))
                    {
                        usbDir = dir;
                        break;
                    }

                    dir = Path.GetDirectoryName(dir);
                }

                if (usbDir == null)
                    continue;

                var vid = ReadAttr(usbDir, "idVendor");
                var pid = ReadAttr(usbDir, "idProduct");
                var usb = vid != null && pid != null ? UsbId.Parse(vid + ":" + pid) : null;
                result.Add(new SerialDevice(
                    Path.Combine(devDir, Path.GetFileName(tty)),
                    ReadAttr(usbDir, "product"),
                    ReadAttr(usbDir, "manufacturer"),
                    usb,
                    ReadAttr(usbDir, "serial")));
            }
            catch (Exception)
            {
                // a device unplugged mid-scan
            }
        }

        return result.OrderBy(d => d.PortName, StringComparer.Ordinal).ToList();
    }

    private static string? ResolveDirectory(string path)
    {
        try
        {
            var target = new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true);
            return target?.FullName ?? Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ReadAttr(string dir, string name)
    {
        try
        {
            var p = Path.Combine(dir, name);
            if (!File.Exists(p))
                return null;
            var text = File.ReadAllText(p).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Follows symbolic links (e.g. /dev/serial/by-id/...) to the device node.</summary>
    public static string RealPath(string path)
    {
        try
        {
            if (!path.StartsWith('/'))
                return path;
            var info = new FileInfo(path);
            if (info.LinkTarget == null)
                return path;
            return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
        }
        catch (Exception)
        {
            return path;
        }
    }

    private static IEnumerable<string> SafePortNames()
    {
        try
        {
            return MissionPlanner.Comms.SerialPort.GetPortNames().Where(UartPresent).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Leaves out what the Comms port list picks up that is no serial port (the controlling
    /// terminal, consoles) and the 32 /dev/ttyS* nodes Linux creates whatever the hardware
    /// (sysfs type 0 means no UART behind one).
    /// </summary>
    internal static bool UartPresent(string name)
    {
        if (name is "/dev/tty" or "/dev/console" or "/dev/ptmx" ||
            (name.StartsWith("/dev/tty", StringComparison.Ordinal) && name.Length > 8 && name[8..].All(char.IsAsciiDigit)))
            return false;
        if (!name.StartsWith("/dev/ttyS", StringComparison.Ordinal))
            return true;
        try
        {
            var type = Path.Combine("/sys/class/tty", Path.GetFileName(name), "type");
            return !File.Exists(type) || File.ReadAllText(type).Trim() != "0";
        }
        catch (Exception)
        {
            return true;
        }
    }
}
