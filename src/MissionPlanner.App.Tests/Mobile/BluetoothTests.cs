using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.Links.Bluetooth;
using MissionPlanner.App.Core.ViewModels.Mobile;
using MissionPlanner.App.Tests.FlightData;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Tests.Mobile;

[TestClass]
public class BluetoothTests
{
    private static readonly BluetoothDeviceInfo Hc06 = new(BluetoothAddress.From(BluetoothKind.Classic, "98:d3:31:fb:12:34"), "HC-06", true);
    private static readonly BluetoothDeviceInfo Esp32 = new(BluetoothAddress.From(BluetoothKind.Ble, "24:0A:C4:00:11:22"), "ESP32 MAVLink", false);
    private static readonly BluetoothDeviceInfo IosLe = new(new BluetoothAddress(BluetoothKind.Ble, "5F2C1A4E-0B0D-4C59-9E8C-7D1B2B9E0A11"), "HM-10", false);

    [TestCleanup]
    public void Cleanup()
    {
        Settings.Instance.Remove(MobileLinkViewModel.RecentKey);
        Settings.Instance.Remove(MobileLinkViewModel.BluetoothNamesKey);
        LinkFactory.BluetoothHost = null;
    }

    [TestMethod]
    public void AddressesSurviveTheSavedLink()
    {
        Assert.AreEqual("bt:98-D3-31-FB-12-34", Hc06.Address.ToString());
        Assert.AreEqual("98:D3:31:FB:12:34", Hc06.Address.PlatformId);
        var spec = LinkSpec.Serial(Hc06.Address.ToString(), 57600);
        Assert.AreEqual("serial:bt:98-D3-31-FB-12-34:57600", spec.ToString());
        var back = LinkSpec.Parse(spec.ToString());
        Assert.AreEqual(spec, back);
        Assert.AreEqual(Hc06.Address, BluetoothAddress.TryParse(back.Address));

        // an iOS peripheral id is a UUID and stays as it is
        Assert.AreEqual("ble:5F2C1A4E-0B0D-4C59-9E8C-7D1B2B9E0A11", IosLe.Address.ToString());
        Assert.AreEqual(IosLe.Address.Id, IosLe.Address.PlatformId);
        Assert.AreEqual(IosLe.Address, BluetoothAddress.TryParse(LinkSpec.Parse(LinkSpec.Serial(IosLe.Address.ToString(), 57600).ToString()).Address));

        Assert.IsNull(BluetoothAddress.TryParse("COM4"));
        Assert.IsNull(BluetoothAddress.TryParse("bt:"));
        Assert.AreEqual("HC-06 · Bluetooth", Hc06.Title);
        Assert.AreEqual("ESP32 MAVLink · Bluetooth LE", Esp32.Title);
    }

    [TestMethod]
    public void KnownSerialServicesAreFound()
    {
        Assert.AreEqual("Nordic UART", BleSerialProfile.Find([Guid.Parse("0000180A-0000-1000-8000-00805F9B34FB"), Guid.Parse("6E400001-B5A3-F393-E0A9-E50E24DCCA9E")])!.Name);
        var hm10 = BleSerialProfile.Find([Guid.Parse("0000FFE0-0000-1000-8000-00805F9B34FB")])!;
        Assert.AreEqual(hm10.Write, hm10.Notify);
        Assert.IsNull(BleSerialProfile.Find([Guid.Parse("0000180F-0000-1000-8000-00805F9B34FB")]));
    }

    [TestMethod]
    public void LinkFactoryOpensBluetoothNamesThroughTheHost()
    {
        Assert.IsInstanceOfType<LinkFactory.SerialLink>(LinkFactory.Create(LinkSpec.Serial("bt:98-D3-31-FB-12-34", 57600)));
        LinkFactory.BluetoothHost = new FakeBluetooth();
        Assert.IsInstanceOfType<BluetoothLink>(LinkFactory.Create(LinkSpec.Serial("bt:98-D3-31-FB-12-34", 57600)));
        Assert.IsInstanceOfType<BluetoothLink>(LinkFactory.Create(LinkSpec.Serial("ble:24-0A-C4-00-11-22", 57600)));
    }

    [TestMethod]
    [Timeout(10000)]
    public void LinkCarriesDataAndClosesWhenTheDeviceGoes()
    {
        var host = new FakeBluetooth();
        var link = new BluetoothLink(host, "ble:24-0A-C4-00-11-22", 57600) { ReadTimeout = 1000 };
        link.Open();
        Assert.AreEqual(Esp32.Address, host.Connected);
        var channel = host.Channel!;
        channel.Deliver([0xFD, 0x09, 0x00]);
        var buffer = new byte[3];
        Assert.AreEqual(3, link.Read(buffer, 0, 3));
        CollectionAssert.AreEqual(new byte[] { 0xFD, 0x09, 0x00 }, buffer);
        link.Write([7, 8], 0, 2);
        CollectionAssert.AreEqual(new byte[] { 7, 8 }, channel.Written.ToArray());

        channel.Drop();
        Assert.IsFalse(link.IsOpen);
        link.Close();
        Assert.IsTrue(channel.Disposed);

        host.Enabled = false;
        var ex = Assert.ThrowsExactly<IOException>(() => new BluetoothLink(host, "bt:98-D3-31-FB-12-34", 57600).Open());
        StringAssert.Contains(ex.Message, "switched off");
    }

    [TestMethod]
    public async Task LinkTabListsPairedDevicesAndScans()
    {
        var host = new FakeBluetooth();
        var vm = new MobileLinkViewModel(FakeUi.Services(out _), bluetooth: host);
        var bt = vm.Kinds.Single(k => k.IsBluetooth);
        Assert.AreEqual(LinkKind.Serial, bt.Kind);
        Assert.AreSame(bt, vm.Kinds[0]); // first when there is no USB

        vm.SelectedKind = bt;
        await vm.RefreshBluetoothAsync();
        Assert.IsTrue(vm.IsBluetooth);
        Assert.IsFalse(vm.IsUsb);
        Assert.IsFalse(vm.NeedsPort);
        Assert.IsTrue(host.PermissionAsked);
        Assert.AreEqual("HC-06 · Bluetooth", vm.SelectedBluetoothDevice!.Title);

        await vm.ToggleScanCommand.ExecuteAsync(null);
        CollectionAssert.AreEqual(new[] { "HC-06 · Bluetooth", "ESP32 MAVLink · Bluetooth LE" }, vm.BluetoothDevices.Select(d => d.Title).ToArray());
        Assert.IsFalse(vm.IsScanning);

        vm.SelectedBluetoothDevice = vm.BluetoothDevices[1];
        var spec = vm.BuildSpec()!;
        Assert.AreEqual("serial:ble:24-0A-C4-00-11-22:57600", spec.ToString());
        Assert.AreEqual("ESP32 MAVLink · Bluetooth LE", vm.Describe(spec));
    }

    [TestMethod]
    public async Task ConnectNeedsPermissionAndBluetoothOn()
    {
        var host = new FakeBluetooth { Grant = false };
        var vm = new MobileLinkViewModel(FakeUi.Services(out _), bluetooth: host);
        Assert.IsFalse(await vm.ConnectAsync(LinkSpec.Serial(Hc06.Address.ToString(), 57600)));
        StringAssert.Contains(vm.Error, "permission");

        host.Grant = true;
        host.Enabled = false;
        Assert.IsFalse(await vm.ConnectAsync(LinkSpec.Serial(Hc06.Address.ToString(), 57600)));
        Assert.AreEqual("Bluetooth is switched off.", vm.Error);
    }

    [TestMethod]
    public void IphoneHasNoClassicBluetooth()
    {
        Settings.Instance[MobileLinkViewModel.RecentKey] = "serial:bt:98-D3-31-FB-12-34:57600|serial:ble:24-0A-C4-00-11-22:57600";
        Settings.Instance[MobileLinkViewModel.BluetoothNamesKey] = "ble:24-0A-C4-00-11-22=ESP32 MAVLink";
        var vm = new MobileLinkViewModel(FakeUi.Services(out _), bluetooth: new FakeBluetooth { Classic = false });
        // the classic link cannot be used there and is not offered again
        Assert.AreEqual(1, vm.Recent.Count);
        Assert.IsTrue(vm.IsBluetooth);
        Assert.AreEqual("ESP32 MAVLink · Bluetooth LE", vm.SelectedBluetoothDevice!.Title);
        StringAssert.Contains(vm.Kinds.Single(k => k.IsBluetooth).Hint, "LE");
        Assert.IsFalse(vm.SupportsClassicBluetooth);
        Assert.IsFalse(vm.NoBluetoothText.Contains("classic", StringComparison.Ordinal));
    }

    internal sealed class FakeBluetooth : IBluetoothHost
    {
        public bool Classic { get; set; } = true;

        public bool Enabled { get; set; } = true;

        public bool Grant { get; set; } = true;

        public bool PermissionAsked { get; private set; }

        public BluetoothAddress? Connected { get; private set; }

        public FakeChannel? Channel { get; private set; }

        public bool SupportsClassic => Classic;

        public bool IsEnabled => Enabled;

        public Task<bool> RequestPermissionAsync()
        {
            PermissionAsked = true;
            return Task.FromResult(Grant);
        }

        public IReadOnlyList<BluetoothDeviceInfo> PairedDevices() => [Hc06];

        public Task ScanAsync(Action<BluetoothDeviceInfo> found, CancellationToken cancel)
        {
            found(Esp32);
            found(Esp32); // seen twice: listed once
            return Task.CompletedTask;
        }

        public IBluetoothChannel Connect(BluetoothAddress address, TimeSpan timeout)
        {
            Connected = address;
            return Channel = new FakeChannel();
        }
    }

    internal sealed class FakeChannel : IBluetoothChannel
    {
        private Action<byte[], int>? _received;
        private Action? _lost;

        public List<byte> Written { get; } = new();

        public bool Disposed { get; private set; }

        public void Start(Action<byte[], int> received, Action lost)
        {
            _received = received;
            _lost = lost;
        }

        public void Deliver(byte[] data) => _received!(data, data.Length);

        public void Drop() => _lost!();

        public void Write(byte[] buffer, int offset, int count) => Written.AddRange(buffer.Skip(offset).Take(count));

        public void Dispose() => Disposed = true;
    }
}
