namespace MissionPlanner.App.Core.Links.Usb;

/// <summary>One endpoint of a USB interface, as the platform reports it.</summary>
public sealed record UsbEndpointInfo(int Address, bool IsIn, UsbTransferType Type, int MaxPacketSize);

public enum UsbTransferType
{
    Control,
    Isochronous,
    Bulk,
    Interrupt,
}

/// <summary>One interface of a USB device: its number, class triple and endpoints.</summary>
public sealed record UsbInterfaceInfo(int Number, int Class, int Subclass, int Protocol, IReadOnlyList<UsbEndpointInfo> Endpoints)
{
    public UsbEndpointInfo? BulkIn => Endpoints.FirstOrDefault(e => e.IsIn && e.Type == UsbTransferType.Bulk);

    public UsbEndpointInfo? BulkOut => Endpoints.FirstOrDefault(e => !e.IsIn && e.Type == UsbTransferType.Bulk);
}

/// <summary>
/// An attached USB device. <see cref="Name"/> is the platform's handle for it (on Android the
/// /dev/bus/usb path), which changes when the device is plugged in again; links are therefore
/// saved by vendor and product id, see <see cref="UsbSerialAddress"/>.
/// </summary>
public sealed record UsbDeviceInfo(
    string Name,
    int VendorId,
    int ProductId,
    string? Manufacturer,
    string? Product,
    IReadOnlyList<UsbInterfaceInfo> Interfaces);

/// <summary>
/// The phone's USB host (Android's UsbManager): which devices are attached, the user's
/// permission to use them, and opening one. The desktop apps have none and use serial ports.
/// </summary>
public interface IUsbHost
{
    IReadOnlyList<UsbDeviceInfo> Devices();

    bool HasPermission(UsbDeviceInfo device);

    /// <summary>Asks the user (the system dialog); true when allowed.</summary>
    Task<bool> RequestPermissionAsync(UsbDeviceInfo device);

    /// <summary>Opens the device, or null when it is gone or not allowed.</summary>
    IUsbConnection? Open(UsbDeviceInfo device);

    /// <summary>A device was plugged in or removed.</summary>
    event EventHandler? DevicesChanged;
}

/// <summary>An open USB device: control and bulk transfers, as Android's UsbDeviceConnection.</summary>
public interface IUsbConnection : IDisposable
{
    bool ClaimInterface(int number);

    /// <summary>Returns the bytes transferred, or a negative number on failure.</summary>
    int ControlTransfer(int requestType, int request, int value, int index, byte[]? buffer, int length, int timeoutMs);

    /// <summary>Returns the bytes transferred, or a negative number on failure or timeout.</summary>
    int BulkTransfer(UsbEndpointInfo endpoint, byte[] buffer, int offset, int length, int timeoutMs);
}
