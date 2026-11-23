namespace MissionPlanner.App.Core.Links.Bluetooth;

/// <summary>Classic Bluetooth serial (SPP / RFCOMM) or a Bluetooth LE serial service.</summary>
public enum BluetoothKind
{
    /// <summary>HC-05 / HC-06 style modules and other SPP devices (Android only; iOS has no SPP for apps).</summary>
    Classic,

    /// <summary>A UART over Bluetooth LE: ESP32 MAVLink bridges, HM-10, Nordic UART (Android and iPhone).</summary>
    Ble,
}

/// <summary>
/// How a Bluetooth device is saved in a link: "bt:AA-BB-CC-DD-EE-FF" (classic) or
/// "ble:&lt;id&gt;" (an address on Android, a UUID on iOS). Colons become dashes so the address
/// survives <see cref="LinkSpec"/>'s "serial:name:baud" form.
/// </summary>
public sealed record BluetoothAddress(BluetoothKind Kind, string Id)
{
    public const string ClassicPrefix = "bt:";
    public const string BlePrefix = "ble:";

    public static bool IsBluetooth(string? portName) =>
        portName != null && (portName.StartsWith(ClassicPrefix, StringComparison.OrdinalIgnoreCase)
                             || portName.StartsWith(BlePrefix, StringComparison.OrdinalIgnoreCase));

    public static BluetoothAddress? TryParse(string? portName)
    {
        if (portName == null)
            return null;
        if (portName.StartsWith(ClassicPrefix, StringComparison.OrdinalIgnoreCase) && portName.Length > ClassicPrefix.Length)
            return new BluetoothAddress(BluetoothKind.Classic, portName[ClassicPrefix.Length..]);
        if (portName.StartsWith(BlePrefix, StringComparison.OrdinalIgnoreCase) && portName.Length > BlePrefix.Length)
            return new BluetoothAddress(BluetoothKind.Ble, portName[BlePrefix.Length..]);
        return null;
    }

    /// <summary>The platform's id: "AA:BB:CC:DD:EE:FF" for a MAC address, a UUID as it is.</summary>
    public string PlatformId => Id.Length == 17 && Id.Count(c => c == '-') == 5 ? Id.Replace('-', ':') : Id;

    public static BluetoothAddress From(BluetoothKind kind, string platformId) =>
        new(kind, platformId.Length == 17 && platformId.Count(c => c == ':') == 5 ? platformId.Replace(':', '-').ToUpperInvariant() : platformId);

    public override string ToString() => (Kind == BluetoothKind.Classic ? ClassicPrefix : BlePrefix) + Id;
}

/// <summary>A Bluetooth device the phone knows (paired) or found in a scan.</summary>
public sealed record BluetoothDeviceInfo(BluetoothAddress Address, string? Name, bool Paired)
{
    /// <summary>"HC-06 · Bluetooth", "ESP32 MAVLink · Bluetooth LE".</summary>
    public string Title => $"{(string.IsNullOrWhiteSpace(Name) ? Address.PlatformId : Name!.Trim())} · {(Address.Kind == BluetoothKind.Ble ? "Bluetooth LE" : "Bluetooth")}";
}

/// <summary>A serial service over Bluetooth LE: where to write and where notifications arrive.</summary>
public sealed record BleSerialProfile(string Name, Guid Service, Guid Write, Guid Notify)
{
    /// <summary>The services serial bridges use, most common first.</summary>
    public static IReadOnlyList<BleSerialProfile> Known { get; } =
    [
        // Nordic UART: ESP32 BLE-MAVLink bridges, many modules
        new("Nordic UART", Guid.Parse("6E400001-B5A3-F393-E0A9-E50E24DCCA9E"), Guid.Parse("6E400002-B5A3-F393-E0A9-E50E24DCCA9E"), Guid.Parse("6E400003-B5A3-F393-E0A9-E50E24DCCA9E")),
        // HM-10 / CC254x / JDY: one characteristic both ways
        new("HM-10", Guid.Parse("0000FFE0-0000-1000-8000-00805F9B34FB"), Guid.Parse("0000FFE1-0000-1000-8000-00805F9B34FB"), Guid.Parse("0000FFE1-0000-1000-8000-00805F9B34FB")),
        // Microchip / ISSC transparent UART (RN4870, BM70)
        new("Microchip UART", Guid.Parse("49535343-FE7D-4AE5-8FA9-9FAFD205E455"), Guid.Parse("49535343-8841-43F4-A8D4-ECBE34729BB3"), Guid.Parse("49535343-1E4D-4BD9-BA61-23C647249616")),
    ];

    /// <summary>The first known profile among a device's services.</summary>
    public static BleSerialProfile? Find(IEnumerable<Guid> services)
    {
        var set = services.ToHashSet();
        return Known.FirstOrDefault(p => set.Contains(p.Service));
    }

    /// <summary>The Client Characteristic Configuration descriptor that turns notifications on.</summary>
    public static Guid ClientConfig { get; } = Guid.Parse("00002902-0000-1000-8000-00805F9B34FB");

    /// <summary>Classic serial port profile (SPP).</summary>
    public static Guid SerialPortService { get; } = Guid.Parse("00001101-0000-1000-8000-00805F9B34FB");
}

/// <summary>
/// The phone's Bluetooth: permission, paired devices, an LE scan for serial services, and
/// connecting. Android supplies classic and LE, iOS LE only.
/// </summary>
public interface IBluetoothHost
{
    bool SupportsClassic { get; }

    /// <summary>Bluetooth is switched on.</summary>
    bool IsEnabled { get; }

    /// <summary>Asks for the Bluetooth permissions (Android 12+ "Nearby devices", iOS Bluetooth).</summary>
    Task<bool> RequestPermissionAsync();

    /// <summary>Classic devices paired in the phone's settings.</summary>
    IReadOnlyList<BluetoothDeviceInfo> PairedDevices();

    /// <summary>Looks for LE devices offering a serial service until cancelled.</summary>
    Task ScanAsync(Action<BluetoothDeviceInfo> found, CancellationToken cancel);

    /// <summary>Connects (blocking, on a worker thread); throws <see cref="IOException"/> when it cannot.</summary>
    IBluetoothChannel Connect(BluetoothAddress address, TimeSpan timeout);
}

/// <summary>A connected Bluetooth serial channel.</summary>
public interface IBluetoothChannel : IDisposable
{
    /// <summary>Starts delivering data; <paramref name="lost"/> is called once if the device goes away.</summary>
    void Start(Action<byte[], int> received, Action lost);

    void Write(byte[] buffer, int offset, int count);
}

/// <summary>A Bluetooth serial device as a link for the MAVLink library.</summary>
public sealed class BluetoothLink : ChannelLink
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    private readonly IBluetoothHost _host;
    private readonly BluetoothAddress _address;
    private IBluetoothChannel? _channel;

    public BluetoothLink(IBluetoothHost host, string portName, int baud)
        : base(portName, baud)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _address = BluetoothAddress.TryParse(portName) ?? throw new ArgumentException($"'{portName}' is not a Bluetooth address", nameof(portName));
    }

    protected override void OpenChannel()
    {
        if (!_host.IsEnabled)
            throw new IOException("Bluetooth is switched off.");
        _channel = _host.Connect(_address, ConnectTimeout);
    }

    protected override void Started() =>
        _channel?.Start((data, n) => Received(data, 0, n), Lost);

    protected override void CloseChannel(bool wasOpen)
    {
        var channel = _channel;
        _channel = null;
        try
        {
            channel?.Dispose();
        }
        catch (Exception)
        {
            // gone already
        }
    }

    protected override void WriteChannel(byte[] buffer, int offset, int count)
    {
        var channel = _channel ?? throw new InvalidOperationException("The Bluetooth link is not open.");
        channel.Write(buffer, offset, count);
    }
}
