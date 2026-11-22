using System.Globalization;

namespace MissionPlanner.App.Core.Links.Usb;

/// <summary>The chip family behind a USB serial port, which decides how it is set up.</summary>
public enum UsbSerialChip
{
    /// <summary>USB CDC-ACM: flight controllers on their own USB port (Pixhawk, Cube, Matek...).</summary>
    CdcAcm,

    /// <summary>FTDI FT232R / FT-X / FT2232: 3DR and RFD radios, many USB-UART cables.</summary>
    Ftdi,

    /// <summary>Silicon Labs CP210x: Holybro and other SiK radios.</summary>
    Cp210x,

    /// <summary>WCH CH340 / CH341: cheap USB-UART adapters.</summary>
    Ch34x,
}

/// <summary>
/// How a serial port on a USB device is saved in a link: "usb:VVVV:PPPP:N" (vendor and product
/// id in hex, port number on the device). In a <see cref="LinkSpec"/> it is the serial port name,
/// so "serial:usb:0403:6001:0:57600" is the first port of an FTDI radio at 57600 baud.
/// </summary>
public sealed record UsbSerialAddress(int VendorId, int ProductId, int Port)
{
    public const string Prefix = "usb:";

    public static bool IsUsb(string? portName) =>
        portName != null && portName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public static UsbSerialAddress? TryParse(string? portName)
    {
        if (!IsUsb(portName))
            return null;
        var parts = portName![Prefix.Length..].Split(':');
        if (parts.Length is < 2 or > 3
            || !int.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var vid)
            || !int.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var pid))
            return null;
        var port = 0;
        if (parts.Length == 3 && !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out port))
            return null;
        return new UsbSerialAddress(vid, pid, port);
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Prefix}{VendorId:x4}:{ProductId:x4}:{Port}");
}

/// <summary>A serial port found on an attached USB device, ready to open.</summary>
public sealed record UsbSerialPortInfo(
    UsbDeviceInfo Device,
    UsbSerialChip Chip,
    int Port,
    int PortCount,
    UsbInterfaceInfo Control,
    UsbInterfaceInfo Data)
{
    public UsbSerialAddress Address => new(Device.VendorId, Device.ProductId, Port);

    /// <summary>"Pixhawk6X (port 1 of 2)", "FT231X USB UART (FTDI)".</summary>
    public string Title
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Device.Product)
                ? string.Create(CultureInfo.InvariantCulture, $"USB device {Device.VendorId:x4}:{Device.ProductId:x4}")
                : Device.Product!.Trim();
            var chip = Chip switch
            {
                UsbSerialChip.Ftdi => " (FTDI)",
                UsbSerialChip.Cp210x => " (CP210x)",
                UsbSerialChip.Ch34x => " (CH340)",
                _ => "",
            };
            return PortCount > 1 ? $"{name}{chip} · port {Port + 1} of {PortCount}" : name + chip;
        }
    }

    /// <summary>
    /// Telemetry radios run at 57600; a flight controller's own USB port ignores the baud rate.
    /// </summary>
    public int DefaultBaud => Chip == UsbSerialChip.CdcAcm ? 115200 : 57600;

    /// <summary>The bulk endpoints for data.</summary>
    public UsbEndpointInfo In => Data.BulkIn!;

    public UsbEndpointInfo Out => Data.BulkOut!;
}

/// <summary>Finds the serial ports on USB devices: the four chip families ArduPilot users meet.</summary>
public static class UsbSerialProbe
{
    private const int ClassComm = 0x02;
    private const int ClassCdcData = 0x0A;
    private const int SubclassAcm = 0x02;

    public const int VendorFtdi = 0x0403;
    public const int VendorSiLabs = 0x10C4;
    public const int VendorWch = 0x1A86;

    private static readonly HashSet<int> Cp210xProducts = [0xEA60, 0xEA61, 0xEA63, 0xEA70, 0xEA71, 0xEA80];
    private static readonly HashSet<int> Ch34xProducts = [0x7523, 0x7522, 0x5523, 0x55D4];

    /// <summary>Every port on every attached device, in the platform's order.</summary>
    public static IReadOnlyList<UsbSerialPortInfo> Ports(IEnumerable<UsbDeviceInfo> devices) =>
        devices.SelectMany(Ports).ToList();

    public static IReadOnlyList<UsbSerialPortInfo> Ports(UsbDeviceInfo device)
    {
        var chip = ChipFor(device);
        if (chip == null)
            return [];

        // the vendor chips: every interface with a bulk pair is one port (FT2232 has two)
        if (chip != UsbSerialChip.CdcAcm)
        {
            var data = device.Interfaces.Where(i => i.BulkIn != null && i.BulkOut != null).ToList();
            if (chip == UsbSerialChip.Ch34x)
                data = data.Take(1).ToList();
            return data.Select((d, n) => new UsbSerialPortInfo(device, chip.Value, n, data.Count, d, d)).ToList();
        }

        // CDC-ACM: each data interface belongs to the communication interface before it (the
        // usual pairing, and what an interface association says on composite boards such as
        // the Cube's two ports); a lone data interface works without one
        var ports = new List<(UsbInterfaceInfo Control, UsbInterfaceInfo Data)>();
        UsbInterfaceInfo? control = null;
        foreach (var i in device.Interfaces)
        {
            if (i.Class == ClassComm && i.Subclass == SubclassAcm)
                control = i;
            else if (i.Class == ClassCdcData && i.BulkIn != null && i.BulkOut != null)
            {
                ports.Add((control ?? i, i));
                control = null;
            }
        }

        return ports.Select((p, n) => new UsbSerialPortInfo(device, UsbSerialChip.CdcAcm, n, ports.Count, p.Control, p.Data)).ToList();
    }

    public static UsbSerialChip? ChipFor(UsbDeviceInfo device)
    {
        if (device.VendorId == VendorFtdi)
            return UsbSerialChip.Ftdi;
        if (device.VendorId == VendorSiLabs && Cp210xProducts.Contains(device.ProductId))
            return UsbSerialChip.Cp210x;
        if (device.VendorId == VendorWch && Ch34xProducts.Contains(device.ProductId))
            return UsbSerialChip.Ch34x;
        if (device.Interfaces.Any(i => i.Class == ClassCdcData))
            return UsbSerialChip.CdcAcm;
        return null;
    }

    /// <summary>The port a saved address names, among those attached now.</summary>
    public static UsbSerialPortInfo? Find(IEnumerable<UsbSerialPortInfo> ports, UsbSerialAddress address) =>
        ports.FirstOrDefault(p => p.Address == address)
        // the same kind of device again with a port count that changed (a board in its bootloader)
        ?? ports.FirstOrDefault(p => p.Device.VendorId == address.VendorId && p.Device.ProductId == address.ProductId);
}
