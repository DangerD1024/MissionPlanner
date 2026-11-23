using Android;
using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using MissionPlanner.App.Core.Links.Bluetooth;
using Java.Util;

namespace MissionPlanner.Mobile.Android;

/// <summary>
/// Android Bluetooth for the shared links: classic serial (RFCOMM to the SPP service) for paired
/// HC-05/HC-06 style modules, and GATT for Bluetooth LE serial services.
/// </summary>
#pragma warning disable CA1422 // the deprecated GATT calls are the ones that work back to Android 8
public sealed class AndroidBluetoothHost : IBluetoothHost
{
    private readonly Context _context;

    public AndroidBluetoothHost(Context context) => _context = context;

    private BluetoothAdapter? Adapter => (_context.GetSystemService(Context.BluetoothService) as BluetoothManager)?.Adapter;

    public bool SupportsClassic => true;

    public bool IsEnabled
    {
        get
        {
            try
            {
                return Adapter?.IsEnabled == true;
            }
            catch (Java.Lang.SecurityException)
            {
                return false;
            }
        }
    }

    private static string[] Needed => Build.VERSION.SdkInt >= BuildVersionCodes.S
        ? [Manifest.Permission.BluetoothConnect, Manifest.Permission.BluetoothScan]
        : [Manifest.Permission.AccessFineLocation];

    public Task<bool> RequestPermissionAsync()
    {
        var missing = Needed.Where(p => _context.CheckSelfPermission(p) != Permission.Granted).ToArray();
        if (missing.Length == 0)
            return Task.FromResult(true);
        return MainActivity.RequestPermissionsAsync(missing);
    }

    public IReadOnlyList<BluetoothDeviceInfo> PairedDevices()
    {
        var adapter = Adapter;
        if (adapter == null)
            return [];
        return (adapter.BondedDevices ?? (ICollection<BluetoothDevice>)Array.Empty<BluetoothDevice>())
            .Where(d => d.Type != BluetoothDeviceType.Le && d.Address != null)
            .Select(d => new BluetoothDeviceInfo(BluetoothAddress.From(BluetoothKind.Classic, d.Address!), d.Name, true))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task ScanAsync(Action<BluetoothDeviceInfo> found, CancellationToken cancel)
    {
        var scanner = Adapter?.BluetoothLeScanner ?? throw new IOException("Bluetooth LE is not available.");
        var callback = new ScanSink(found);
        var settings = new ScanSettings.Builder().SetScanMode(global::Android.Bluetooth.LE.ScanMode.LowLatency)!.Build();
        scanner.StartScan(null, settings, callback);
        try
        {
            await Task.Delay(Timeout.Infinite, cancel);
        }
        finally
        {
            try
            {
                scanner.StopScan(callback);
            }
            catch (Exception)
            {
                // Bluetooth switched off meanwhile
            }
        }
    }

    public IBluetoothChannel Connect(BluetoothAddress address, TimeSpan timeout)
    {
        var adapter = Adapter ?? throw new IOException("This phone has no Bluetooth.");
        var device = adapter.GetRemoteDevice(address.PlatformId) ?? throw new IOException("Unknown Bluetooth device.");
        if (address.Kind == BluetoothKind.Classic)
        {
            try
            {
                adapter.CancelDiscovery();
            }
            catch (Java.Lang.SecurityException)
            {
            }

            return ClassicChannel.Connect(device);
        }

        return BleChannel.Connect(_context, device, timeout);
    }

    /// <summary>LE devices with a name or a known serial service.</summary>
    private sealed class ScanSink(Action<BluetoothDeviceInfo> found) : ScanCallback
    {
        private readonly HashSet<string> _seen = new();

        public override void OnScanResult(ScanCallbackType callbackType, ScanResult? result)
        {
            var device = result?.Device;
            if (device?.Address == null)
                return;
            var name = result!.ScanRecord?.DeviceName ?? SafeName(device);
            var services = result.ScanRecord?.ServiceUuids?.Select(u => Guid.Parse(u.ToString())) ?? [];
            if (string.IsNullOrWhiteSpace(name) && BleSerialProfile.Find(services) == null)
                return;
            if (!_seen.Add(device.Address + "|" + name))
                return;
            found(new BluetoothDeviceInfo(BluetoothAddress.From(BluetoothKind.Ble, device.Address), name, false));
        }

        private static string? SafeName(BluetoothDevice d)
        {
            try
            {
                return d.Name;
            }
            catch (Java.Lang.SecurityException)
            {
                return null;
            }
        }
    }

    /// <summary>RFCOMM to the serial port service (secure first, then insecure for modules that need it).</summary>
    private sealed class ClassicChannel : IBluetoothChannel
    {
        private static readonly UUID Spp = UUID.FromString(BleSerialProfile.SerialPortService.ToString())!;
        private readonly BluetoothSocket _socket;
        private volatile bool _closed;

        private ClassicChannel(BluetoothSocket socket) => _socket = socket;

        public static ClassicChannel Connect(BluetoothDevice device)
        {
            Exception? first = null;
            foreach (var secure in new[] { true, false })
            {
                BluetoothSocket? socket = null;
                try
                {
                    socket = secure ? device.CreateRfcommSocketToServiceRecord(Spp) : device.CreateInsecureRfcommSocketToServiceRecord(Spp);
                    socket!.Connect();
                    return new ClassicChannel(socket);
                }
                catch (Exception ex) when (ex is Java.IO.IOException or IOException or Java.Lang.SecurityException)
                {
                    first ??= ex;
                    try
                    {
                        socket?.Close();
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            throw new IOException("Could not connect to the Bluetooth device (is it switched on and in range?): " + first?.Message, first);
        }

        public void Start(Action<byte[], int> received, Action lost)
        {
            var input = _socket.InputStream!;
            new Thread(() =>
            {
                var buffer = new byte[4096];
                try
                {
                    while (!_closed)
                    {
                        var n = input.Read(buffer, 0, buffer.Length);
                        if (n <= 0)
                            break;
                        received(buffer, n);
                    }
                }
                catch (Exception)
                {
                    // the socket closed
                }

                if (!_closed)
                    lost();
            }) { IsBackground = true, Name = "Bluetooth read" }.Start();
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            var output = _socket.OutputStream ?? throw new IOException("The Bluetooth link is closed.");
            output.Write(buffer, offset, count);
            output.Flush();
        }

        public void Dispose()
        {
            _closed = true;
            try
            {
                _socket.Close();
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>
    /// A GATT serial service: notifications in, writes out in MTU-sized pieces, one at a time
    /// (Android allows a single outstanding GATT operation).
    /// </summary>
    private sealed class BleChannel : BluetoothGattCallback, IBluetoothChannel
    {
        private readonly ManualResetEventSlim _ready = new();
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly AutoResetEvent _written = new(false);
        private BluetoothGatt? _gatt;
        private BluetoothGattCharacteristic? _write;
        private BluetoothGattCharacteristic? _notify;
        private string? _failure;
        private int _mtu = 23;
        private volatile bool _closed;
        private Action<byte[], int>? _received;
        private Action? _lost;

        public static BleChannel Connect(Context context, BluetoothDevice device, TimeSpan timeout)
        {
            var channel = new BleChannel();
            channel._gatt = device.ConnectGatt(context, false, channel, BluetoothTransports.Le)
                            ?? throw new IOException("Could not start the Bluetooth LE connection.");
            if (!channel._ready.Wait(timeout) || channel._failure != null)
            {
                var why = channel._failure ?? "no answer from the device";
                channel.Dispose();
                throw new IOException("Bluetooth LE: " + why + ".");
            }

            return channel;
        }

        public void Start(Action<byte[], int> received, Action lost)
        {
            _received = received;
            _lost = lost;
        }

        public override void OnConnectionStateChange(BluetoothGatt? gatt, GattStatus status, ProfileState newState)
        {
            if (newState == ProfileState.Connected && status == GattStatus.Success)
            {
                // bigger packets for MAVLink; the services are looked up once the MTU is settled
                if (gatt?.RequestMtu(247) != true)
                    gatt?.DiscoverServices();
                return;
            }

            if (newState == ProfileState.Disconnected || status != GattStatus.Success)
            {
                if (!_ready.IsSet)
                {
                    _failure = $"the connection failed (status {(int)status})";
                    _ready.Set();
                }
                else if (!_closed)
                {
                    _lost?.Invoke();
                }

                _written.Set();
            }
        }

        public override void OnMtuChanged(BluetoothGatt? gatt, int mtu, GattStatus status)
        {
            if (status == GattStatus.Success)
                _mtu = mtu;
            gatt?.DiscoverServices();
        }

        public override void OnServicesDiscovered(BluetoothGatt? gatt, GattStatus status)
        {
            if (gatt == null || status != GattStatus.Success)
            {
                _failure = "the services could not be read";
                _ready.Set();
                return;
            }

            var services = gatt.Services?.Where(s => s.Uuid != null).ToList() ?? [];
            var profile = BleSerialProfile.Find(services.Select(s => Guid.Parse(s.Uuid!.ToString())));
            if (profile == null)
            {
                _failure = "the device has no serial service (Nordic UART, HM-10 or Microchip)";
                _ready.Set();
                return;
            }

            var service = services.First(s => Guid.Parse(s.Uuid!.ToString()) == profile.Service);
            _write = service.GetCharacteristic(UUID.FromString(profile.Write.ToString()));
            _notify = service.GetCharacteristic(UUID.FromString(profile.Notify.ToString()));
            if (_write == null || _notify == null)
            {
                _failure = "the serial service is incomplete";
                _ready.Set();
                return;
            }

            gatt.SetCharacteristicNotification(_notify, true);
            var cccd = _notify.GetDescriptor(UUID.FromString(BleSerialProfile.ClientConfig.ToString()));
            if (cccd == null)
            {
                _ready.Set();
                return;
            }

            var indicate = (_notify.Properties & GattProperty.Indicate) != 0 && (_notify.Properties & GattProperty.Notify) == 0;
            cccd.SetValue(indicate ? BluetoothGattDescriptor.EnableIndicationValue!.ToArray() : BluetoothGattDescriptor.EnableNotificationValue!.ToArray());
            if (!gatt.WriteDescriptor(cccd))
                _ready.Set();
        }

        public override void OnDescriptorWrite(BluetoothGatt? gatt, BluetoothGattDescriptor? descriptor, GattStatus status) =>
            _ready.Set();

        public override void OnCharacteristicChanged(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic)
        {
            var value = characteristic?.GetValue();
            if (value is { Length: > 0 })
                _received?.Invoke(value, value.Length);
        }

        public override void OnCharacteristicWrite(BluetoothGatt? gatt, BluetoothGattCharacteristic? characteristic, GattStatus status) =>
            _written.Set();

        public void Write(byte[] buffer, int offset, int count)
        {
            var gatt = _gatt;
            var ch = _write;
            if (_closed || gatt == null || ch == null)
                throw new IOException("The Bluetooth LE link is closed.");
            var chunk = Math.Max(20, _mtu - 3);
            var noResponse = (ch.Properties & GattProperty.WriteNoResponse) != 0;
            _writeLock.Wait();
            try
            {
                while (count > 0)
                {
                    var n = Math.Min(chunk, count);
                    var piece = new byte[n];
                    Buffer.BlockCopy(buffer, offset, piece, 0, n);
                    ch.WriteType = noResponse ? GattWriteType.NoResponse : GattWriteType.Default;
                    ch.SetValue(piece);
                    _written.Reset();
                    var sent = false;
                    // busy with the previous operation: try again shortly
                    for (var attempt = 0; attempt < 20 && !sent; attempt++)
                    {
                        sent = gatt.WriteCharacteristic(ch);
                        if (!sent)
                            Thread.Sleep(5);
                    }

                    if (!sent)
                        throw new TimeoutException("The Bluetooth LE device did not take the data.");
                    _written.WaitOne(1000);
                    if (_closed)
                        throw new IOException("The Bluetooth LE link is closed.");
                    offset += n;
                    count -= n;
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_closed)
            {
                _closed = true;
                try
                {
                    _gatt?.Disconnect();
                    _gatt?.Close();
                }
                catch (Exception)
                {
                }

                _written.Set();
            }

            base.Dispose(disposing);
        }
    }
}
#pragma warning restore CA1422
