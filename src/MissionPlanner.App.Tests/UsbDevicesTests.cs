using MissionPlanner.App.Core.Links;

namespace MissionPlanner.App.Tests;

/// <summary>Which USB device a serial port belongs to (both ports of a two-port flight controller agree).</summary>
[TestClass]
public class UsbDevicesTests
{
    [TestMethod]
    public void LinuxFollowsSysfsToTheUsbDevice()
    {
        if (OperatingSystem.IsWindows())
            Assert.Inconclusive("symbolic links in a temporary sysfs");
        var root = Path.Combine(TestEnvironment.DataRoot, "sysfs-" + Guid.NewGuid().ToString("N"));
        try
        {
            // /sys/devices/.../usb1/1-2 is the device (idVendor); 1-2:1.0 and 1-2:1.2 its two serial interfaces
            var device = Path.Combine(root, "devices", "pci0000:00", "usb1", "1-2");
            Directory.CreateDirectory(device);
            File.WriteAllText(Path.Combine(device, "idVendor"), "1209\n");
            foreach (var (iface, tty) in new[] { ("1-2:1.0", "ttyACM1"), ("1-2:1.2", "ttyACM2") })
            {
                var ttyDir = Path.Combine(device, iface, "tty", tty);
                Directory.CreateDirectory(ttyDir);
                // relative links as the kernel makes them
                Directory.CreateSymbolicLink(Path.Combine(ttyDir, "device"), "../../../" + iface);
                Directory.CreateDirectory(Path.Combine(root, "class", "tty"));
                Directory.CreateSymbolicLink(Path.Combine(root, "class", "tty", tty), $"../../devices/pci0000:00/usb1/1-2/{iface}/tty/{tty}");
            }

            // a serial port that is not USB
            var platform = Path.Combine(root, "devices", "platform", "serial8250", "tty", "ttyS0");
            Directory.CreateDirectory(platform);
            Directory.CreateSymbolicLink(Path.Combine(platform, "device"), "../../../serial8250");
            Directory.CreateSymbolicLink(Path.Combine(root, "class", "tty", "ttyS0"), "../../devices/platform/serial8250/tty/ttyS0");

            var a = UsbDevices.LinuxDevice("/dev/ttyACM1", root);
            var b = UsbDevices.LinuxDevice("/dev/ttyACM2", root);
            Assert.AreEqual("usb:" + UsbDevices.RealPath(device), a);
            Assert.AreEqual(a, b, "two ports, one flight controller");
            Assert.IsNull(UsbDevices.LinuxDevice("/dev/ttyS0", root));
            Assert.IsNull(UsbDevices.LinuxDevice("/dev/ttyACM9", root), "no such port");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void MacPortsOfOneDeviceDifferInTheLastDigit()
    {
        Assert.AreEqual(UsbDevices.MacDevice("/dev/cu.usbmodem14101"), UsbDevices.MacDevice("/dev/cu.usbmodem14103"));
        Assert.AreEqual(UsbDevices.MacDevice("/dev/cu.usbmodem14101"), UsbDevices.MacDevice("/dev/tty.usbmodem14101"));
        Assert.AreNotEqual(UsbDevices.MacDevice("/dev/cu.usbmodem14101"), UsbDevices.MacDevice("/dev/cu.usbmodem14201"), "another hub port");
        Assert.IsNull(UsbDevices.MacDevice("/dev/cu.Bluetooth-Incoming-Port"));
    }

    [TestMethod]
    public void WindowsCompositeInterfacesShareTheirParent()
    {
        var a = UsbDevices.WindowsDeviceKey("VID_1209&PID_5741&MI_00", "7&2a1b3c4d&0&0000");
        var b = UsbDevices.WindowsDeviceKey("VID_1209&PID_5741&MI_02", "7&2a1b3c4d&0&0002");
        Assert.AreEqual(a, b);
        Assert.AreNotEqual(a, UsbDevices.WindowsDeviceKey("VID_1209&PID_5741&MI_00", "7&99999999&0&0000"), "a second board of the same kind");
        Assert.AreEqual(@"usb:VID_0403&PID_6001\A12345", UsbDevices.WindowsDeviceKey("VID_0403&PID_6001", "A12345"), "a plain adapter is its own device");
    }

    [TestMethod]
    public void RealPathFollowsRelativeLinksInsideLinkedDirectories()
    {
        if (OperatingSystem.IsWindows())
            Assert.Inconclusive("POSIX paths");
        var root = UsbDevices.RealPath(Path.Combine(TestEnvironment.DataRoot, "realpath-" + Guid.NewGuid().ToString("N")));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "real", "a", "b"));
            Directory.CreateSymbolicLink(Path.Combine(root, "link"), "real/a");
            Directory.CreateSymbolicLink(Path.Combine(root, "real", "a", "b", "up"), "../../a");
            Assert.AreEqual(Path.Combine(root, "real", "a"), UsbDevices.RealPath(Path.Combine(root, "link", "b", "up")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
