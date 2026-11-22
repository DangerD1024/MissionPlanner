using CoreBluetooth;
using CoreFoundation;
using Foundation;
using MissionPlanner.App.Core.Links.Bluetooth;

namespace MissionPlanner.Mobile.iOS;

/// <summary>
/// Bluetooth LE serial through CoreBluetooth (iOS gives apps no classic serial profile). The
/// central runs on its own queue, so callbacks never wait for the UI thread.
/// </summary>
public sealed class IosBluetoothHost : IBluetoothHost
{
    private readonly DispatchQueue _queue = new("org.missionplanner.bluetooth");
    private readonly CentralEvents _events = new();
    private readonly Dictionary<string, CBPeripheral> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private CBCentralManager? _central;

    public bool SupportsClassic => false;

    public bool IsEnabled
    {
        get
        {
            var central = Central();
            _events.StateKnown.Wait(TimeSpan.FromSeconds(2));
            return central.State == CBManagerState.PoweredOn;
        }
    }

    /// <summary>Creating the central asks the user the first time; the answer arrives as a state change.</summary>
    public Task<bool> RequestPermissionAsync() => Task.Run(() =>
    {
        if (CBManager.Authorization is CBManagerAuthorization.Denied or CBManagerAuthorization.Restricted)
            return false;
        Central();
        // up to a minute for the user to answer the system prompt
        for (var i = 0; i < 600 && CBManager.Authorization == CBManagerAuthorization.NotDetermined; i++)
            Thread.Sleep(100);
        return CBManager.Authorization == CBManagerAuthorization.AllowedAlways;
    });

    public IReadOnlyList<BluetoothDeviceInfo> PairedDevices() => [];

    public async Task ScanAsync(Action<BluetoothDeviceInfo> found, CancellationToken cancel)
    {
        var central = Central();
        _events.StateKnown.Wait(TimeSpan.FromSeconds(3));
        if (central.State != CBManagerState.PoweredOn)
            throw new IOException("Bluetooth is switched off.");
        var seen = new HashSet<string>();

        void OnDiscovered(CBPeripheral peripheral, NSDictionary advertisement)
        {
            var id = peripheral.Identifier.AsString();
            var name = (advertisement[CBAdvertisement.DataLocalNameKey] as NSString)?.ToString() ?? peripheral.Name;
            var services = (advertisement[CBAdvertisement.DataServiceUUIDsKey] as NSArray)?.ToArray<CBUUID>().Select(ToGuid) ?? [];
            if (string.IsNullOrWhiteSpace(name) && BleSerialProfile.Find(services) == null)
                return;
            lock (_gate)
                _known[id] = peripheral;
            if (seen.Add(id + "|" + name))
                found(new BluetoothDeviceInfo(new BluetoothAddress(BluetoothKind.Ble, id), name, false));
        }

        _events.Discovered += OnDiscovered;
        central.ScanForPeripherals((CBUUID[]?)null, new PeripheralScanningOptions { AllowDuplicatesKey = false });
        try
        {
            await Task.Delay(Timeout.Infinite, cancel);
        }
        finally
        {
            central.StopScan();
            _events.Discovered -= OnDiscovered;
        }
    }

    public IBluetoothChannel Connect(BluetoothAddress address, TimeSpan timeout)
    {
        if (address.Kind != BluetoothKind.Ble)
            throw new IOException("The iPhone connects to Bluetooth LE devices only.");
        var central = Central();
        _events.StateKnown.Wait(TimeSpan.FromSeconds(3));
        if (central.State != CBManagerState.PoweredOn)
            throw new IOException("Bluetooth is switched off.");
        CBPeripheral? peripheral;
        lock (_gate)
            _known.TryGetValue(address.Id, out peripheral);
        peripheral ??= central.RetrievePeripheralsWithIdentifiers(new NSUuid(address.Id)).FirstOrDefault();
        if (peripheral == null)
            throw new IOException("The Bluetooth device was not found; scan for it again.");
        return BleChannel.Connect(central, _events, peripheral, timeout);
    }

    private CBCentralManager Central()
    {
        lock (_gate)
            return _central ??= new CBCentralManager(_events, _queue);
    }

    /// <summary>A CoreBluetooth UUID (short "FFE0" or full) as a Guid.</summary>
    internal static Guid ToGuid(CBUUID uuid)
    {
        var text = uuid.Uuid;
        if (text.Length == 4)
            text = "0000" + text + "-0000-1000-8000-00805F9B34FB";
        else if (text.Length == 8)
            text += "-0000-1000-8000-00805F9B34FB";
        return Guid.TryParse(text, out var g) ? g : Guid.Empty;
    }

    /// <summary>The central's callbacks, handed on to whoever listens.</summary>
    private sealed class CentralEvents : CBCentralManagerDelegate
    {
        public ManualResetEventSlim StateKnown { get; } = new();

        public event Action<CBPeripheral, NSDictionary>? Discovered;

        public event Action<CBPeripheral>? Connected;

        public event Action<CBPeripheral, string>? Failed;

        public event Action<CBPeripheral>? Disconnected;

        public override void UpdatedState(CBCentralManager central)
        {
            if (central.State != CBManagerState.Unknown && central.State != CBManagerState.Resetting)
                StateKnown.Set();
        }

        public override void DiscoveredPeripheral(CBCentralManager central, CBPeripheral peripheral, NSDictionary advertisementData, NSNumber RSSI) =>
            Discovered?.Invoke(peripheral, advertisementData);

        public override void ConnectedPeripheral(CBCentralManager central, CBPeripheral peripheral) => Connected?.Invoke(peripheral);

        public override void FailedToConnectPeripheral(CBCentralManager central, CBPeripheral peripheral, NSError? error) =>
            Failed?.Invoke(peripheral, error?.LocalizedDescription ?? "the connection failed");

        public override void DisconnectedPeripheral(CBCentralManager central, CBPeripheral peripheral, NSError? error) =>
            Disconnected?.Invoke(peripheral);
    }

    /// <summary>A serial service on a peripheral: notifications in, writes out in pieces it accepts.</summary>
    private sealed class BleChannel : CBPeripheralDelegate, IBluetoothChannel
    {
        private readonly CBCentralManager _central;
        private readonly CentralEvents _events;
        private readonly CBPeripheral _peripheral;
        private readonly ManualResetEventSlim _ready = new();
        private readonly AutoResetEvent _written = new(false);
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private CBCharacteristic? _write;
        private CBCharacteristic? _notify;
        private string? _failure;
        private volatile bool _closed;
        private Action<byte[], int>? _received;
        private Action? _lost;

        private BleChannel(CBCentralManager central, CentralEvents events, CBPeripheral peripheral)
        {
            _central = central;
            _events = events;
            _peripheral = peripheral;
        }

        public static BleChannel Connect(CBCentralManager central, CentralEvents events, CBPeripheral peripheral, TimeSpan timeout)
        {
            var channel = new BleChannel(central, events, peripheral);
            events.Connected += channel.OnConnected;
            events.Failed += channel.OnFailed;
            events.Disconnected += channel.OnDisconnected;
            peripheral.Delegate = channel;
            central.ConnectPeripheral(peripheral, new PeripheralConnectionOptions());
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

        private bool Mine(CBPeripheral p) => p.Identifier.Equals(_peripheral.Identifier);

        private void OnConnected(CBPeripheral p)
        {
            if (Mine(p))
                p.DiscoverServices();
        }

        private void OnFailed(CBPeripheral p, string why)
        {
            if (!Mine(p))
                return;
            _failure = why;
            _ready.Set();
        }

        private void OnDisconnected(CBPeripheral p)
        {
            if (!Mine(p))
                return;
            if (!_ready.IsSet)
            {
                _failure = "the device disconnected";
                _ready.Set();
            }
            else if (!_closed)
            {
                _lost?.Invoke();
            }

            _written.Set();
        }

        public override void DiscoveredService(CBPeripheral peripheral, NSError? error)
        {
            var services = peripheral.Services ?? [];
            var profile = BleSerialProfile.Find(services.Select(s => ToGuid(s.UUID)));
            if (error != null || profile == null)
            {
                _failure = error?.LocalizedDescription ?? "the device has no serial service (Nordic UART, HM-10 or Microchip)";
                _ready.Set();
                return;
            }

            var service = services.First(s => ToGuid(s.UUID) == profile.Service);
            peripheral.DiscoverCharacteristics(service);
        }

        public override void DiscoveredCharacteristics(CBPeripheral peripheral, CBService service, NSError? error)
        {
            var profile = BleSerialProfile.Find([ToGuid(service.UUID)]);
            var characteristics = service.Characteristics ?? [];
            _write = characteristics.FirstOrDefault(c => ToGuid(c.UUID) == profile?.Write);
            _notify = characteristics.FirstOrDefault(c => ToGuid(c.UUID) == profile?.Notify);
            if (error != null || _write == null || _notify == null)
            {
                _failure = error?.LocalizedDescription ?? "the serial service is incomplete";
                _ready.Set();
                return;
            }

            peripheral.SetNotifyValue(true, _notify);
        }

        public override void UpdatedNotificationState(CBPeripheral peripheral, CBCharacteristic characteristic, NSError? error)
        {
            if (error != null)
                _failure = error.LocalizedDescription;
            _ready.Set();
        }

        public override void UpdatedCharacterteristicValue(CBPeripheral peripheral, CBCharacteristic characteristic, NSError? error)
        {
            if (error != null || characteristic.Value is not { Length: > 0 } value)
                return;
            var data = value.ToArray();
            _received?.Invoke(data, data.Length);
        }

        public override void WroteCharacteristicValue(CBPeripheral peripheral, CBCharacteristic characteristic, NSError? error) =>
            _written.Set();

        public override void IsReadyToSendWriteWithoutResponse(CBPeripheral peripheral) => _written.Set();

        public void Write(byte[] buffer, int offset, int count)
        {
            var ch = _write;
            if (_closed || ch == null)
                throw new IOException("The Bluetooth LE link is closed.");
            var noResponse = (ch.Properties & CBCharacteristicProperties.WriteWithoutResponse) != 0;
            var type = noResponse ? CBCharacteristicWriteType.WithoutResponse : CBCharacteristicWriteType.WithResponse;
            var chunk = Math.Max(20, (int)_peripheral.GetMaximumWriteValueLength(type));
            _writeLock.Wait();
            try
            {
                while (count > 0)
                {
                    var n = Math.Min(chunk, count);
                    if (noResponse)
                    {
                        // the peripheral's queue is full: wait for room
                        for (var i = 0; i < 50 && !_peripheral.CanSendWriteWithoutResponse && !_closed; i++)
                            _written.WaitOne(20);
                    }

                    _written.Reset();
                    _peripheral.WriteValue(NSData.FromArray(buffer.AsSpan(offset, n).ToArray()), ch, type);
                    if (!noResponse)
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
                _events.Connected -= OnConnected;
                _events.Failed -= OnFailed;
                _events.Disconnected -= OnDisconnected;
                try
                {
                    _central.CancelPeripheralConnection(_peripheral);
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
