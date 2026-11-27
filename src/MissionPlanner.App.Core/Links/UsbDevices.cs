using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace MissionPlanner.App.Core.Links;

/// <summary>
/// Which USB device a serial port belongs to. One flight controller can show up as two USB serial ports
/// (a MatekH743 has "…-if00" and "…-if02", both answering MAVLink): the user's Disconnect is for that
/// flight controller, so auto-connect must leave all of its ports alone, not just the one it was on.
/// </summary>
public static partial class UsbDevices
{
    /// <summary>A key shared by the ports of one USB device, or null when it can't be told (not USB, unknown system).</summary>
    public static string? DeviceOf(string port)
    {
        try
        {
            if (OperatingSystem.IsLinux())
                return LinuxDevice(port, "/sys");
            if (OperatingSystem.IsMacOS())
                return MacDevice(port);
            if (OperatingSystem.IsWindows())
                return WindowsDevice(port);
        }
        catch (Exception)
        {
            // not a USB port, or the system would not say
        }

        return null;
    }

    /// <summary>
    /// Linux: the port's tty in sysfs leads to its USB interface (<c>1-2:1.0</c>), whose parent is the device
    /// (<c>1-2</c>, the directory with <c>idVendor</c>); both of a composite device's ports lead to it.
    /// </summary>
    public static string? LinuxDevice(string port, string sysRoot)
    {
        var tty = Path.GetFileName(RealPath(port));
        var dir = RealPath(Path.Combine(sysRoot, "class", "tty", tty, "device"));
        for (var i = 0; i < 4 && !string.IsNullOrEmpty(dir) && dir != "/"; i++)
        {
            if (File.Exists(Path.Combine(dir, "idVendor")))
                return "usb:" + dir;
            dir = Path.GetDirectoryName(dir) ?? "";
        }

        return null;
    }

    /// <summary>macOS: a composite device's ports differ in the last digit only (usbmodem14101, usbmodem14103).</summary>
    public static string? MacDevice(string port)
    {
        var m = MacModemRegex().Match(Path.GetFileName(port));
        return m.Success ? "usbmodem:" + m.Groups[1].Value : null;
    }

    /// <summary>
    /// Windows: the port's entry under HKLM\SYSTEM\CurrentControlSet\Enum\USB (Device Parameters\PortName).
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string? WindowsDevice(string port)
    {
        using var usb = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        if (usb == null)
            return null;
        foreach (var hardware in usb.GetSubKeyNames())
        {
            using var h = usb.OpenSubKey(hardware);
            if (h == null)
                continue;
            foreach (var instance in h.GetSubKeyNames())
            {
                using var parameters = h.OpenSubKey(instance + @"\Device Parameters");
                if (parameters?.GetValue("PortName") is string name && string.Equals(name, port, StringComparison.OrdinalIgnoreCase))
                    return WindowsDeviceKey(hardware, instance);
            }
        }

        return null;
    }

    /// <summary>
    /// The device of a Windows USB port entry: an interface of a composite device (<c>VID_1209&amp;PID_5741&amp;MI_02</c>,
    /// instance <c>7&amp;2a1b3c4d&amp;0&amp;0002</c>) shares the instance prefix (<c>7&amp;2a1b3c4d&amp;0</c>) with its
    /// siblings; a plain device is its own instance.
    /// </summary>
    public static string WindowsDeviceKey(string hardware, string instance)
    {
        var composite = WindowsInterfaceRegex().IsMatch(hardware);
        var device = WindowsInterfaceRegex().Replace(hardware, "").ToUpperInvariant();
        var id = instance.ToUpperInvariant();
        if (composite && id.LastIndexOf('&') is var cut and > 0)
            id = id[..cut];
        return $"usb:{device}\\{id}";
    }

    /// <summary>The real path, following every symbolic link on the way (as the C library's realpath).</summary>
    public static string RealPath(string path)
    {
        if (!path.StartsWith('/'))
            return path;
        var pending = new List<string>(path.Split('/', StringSplitOptions.RemoveEmptyEntries));
        var current = "/";
        var links = 0;
        while (pending.Count > 0)
        {
            var part = pending[0];
            pending.RemoveAt(0);
            if (part == ".")
                continue;
            if (part == "..")
            {
                current = Path.GetDirectoryName(current) ?? "/";
                continue;
            }

            var next = Path.Combine(current, part);
            string? target = null;
            try
            {
                target = new FileInfo(next).LinkTarget;
            }
            catch (Exception)
            {
                // unreadable: keep the name
            }

            if (target != null && ++links < 40)
            {
                if (target.StartsWith('/'))
                    current = "/";
                pending.InsertRange(0, target.Split('/', StringSplitOptions.RemoveEmptyEntries));
                continue;
            }

            current = next;
        }

        return current;
    }

    [GeneratedRegex(@"^(?:cu|tty)\.usbmodem(.+)\d$")]
    private static partial Regex MacModemRegex();

    [GeneratedRegex(@"&MI_[0-9A-Fa-f]+", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsInterfaceRegex();
}
