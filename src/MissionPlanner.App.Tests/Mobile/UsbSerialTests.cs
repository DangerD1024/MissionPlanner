using System.Collections.Concurrent;
using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.Links.Usb;
using MissionPlanner.App.Core.ViewModels.Mobile;
using MissionPlanner.App.Tests.FlightData;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Tests.Mobile;

[TestClass]
public class UsbSerialTests
{
    private static readonly UsbEndpointInfo FsIn = new(0x81, true, UsbTransferType.Bulk, 64);
    private static readonly UsbEndpointInfo FsOut = new(0x02, false, UsbTransferType.Bulk, 64);

    [TestCleanup]
    public void Cleanup()
    {
        Settings.Instance.Remove(MobileLinkViewModel.RecentKey);
        LinkFactory.UsbHost = null;
    }

    // --- devices as Android describes them

    /// <summary>A Cube/Pixhawk: two CDC ports (MAVLink and SLCAN), each a comm + data interface.</summary>
    internal static UsbDeviceInfo Cube() => new("/dev/bus/usb/001/002", 0x2DAE, 0x1016, "CubePilot", "CubeOrange+",
    [
        new(0, 2, 2, 1, [new(0x83, true, UsbTransferType.Interrupt, 16)]),
        new(1, 10, 0, 0, [FsOut, FsIn]),
        new(2, 2, 2, 1, [new(0x85, true, UsbTransferType.Interrupt, 16)]),
        new(3, 10, 0, 0, [new(0x04, false, UsbTransferType.Bulk, 64), new(0x84, true, UsbTransferType.Bulk, 64)]),
    ]);

    internal static UsbDeviceInfo FtdiRadio() => new("/dev/bus/usb/001/003", 0x0403, 0x6015, "FTDI", "FT231X USB UART",
        [new(0, 0xFF, 0xFF, 0xFF, [FsIn, FsOut])]);

    internal static UsbDeviceInfo Cp2102() => new("/dev/bus/usb/001/004", 0x10C4, 0xEA60, "Silicon Labs", "CP2102 USB to UART Bridge Controller",
        [new(0, 0xFF, 0, 0, [FsIn, FsOut])]);

    internal static UsbDeviceInfo Ch340() => new("/dev/bus/usb/001/005", 0x1A86, 0x7523, null, "USB Serial",
        [new(0, 0xFF, 1, 2, [new(0x82, true, UsbTransferType.Bulk, 32), new(0x02, false, UsbTransferType.Bulk, 32), new(0x81, true, UsbTransferType.Interrupt, 8)])]);

    internal static UsbDeviceInfo Keyboard() => new("/dev/bus/usb/001/006", 0x046D, 0xC31C, "Logitech", "Keyboard",
        [new(0, 3, 1, 1, [new(0x81, true, UsbTransferType.Interrupt, 8)])]);

    [TestMethod]
    public void ProbeFindsThePortsOfTheFourChipFamilies()
    {
        var ports = UsbSerialProbe.Ports([Cube(), FtdiRadio(), Cp2102(), Ch340(), Keyboard()]);
        CollectionAssert.AreEqual(
            new[] { UsbSerialChip.CdcAcm, UsbSerialChip.CdcAcm, UsbSerialChip.Ftdi, UsbSerialChip.Cp210x, UsbSerialChip.Ch34x },
            ports.Select(p => p.Chip).ToArray());

        var cube = ports[0];
        Assert.AreEqual(0, cube.Control.Number);
        Assert.AreEqual(1, cube.Data.Number);
        Assert.AreEqual(0x81, cube.In.Address);
        Assert.AreEqual(0x02, cube.Out.Address);
        Assert.AreEqual("CubeOrange+ · port 1 of 2", cube.Title);
        Assert.AreEqual(2, ports[1].Control.Number);
        Assert.AreEqual(3, ports[1].Data.Number);
        Assert.AreEqual("usb:2dae:1016:1", ports[1].Address.ToString());

        Assert.AreEqual("FT231X USB UART (FTDI)", ports[2].Title);
        Assert.AreEqual(57600, ports[2].DefaultBaud);
        Assert.AreEqual(115200, cube.DefaultBaud);
        Assert.AreEqual(0x82, ports[4].In.Address); // the CH340's bulk pair, not its interrupt endpoint
    }

    [TestMethod]
    public void AddressesRoundTripInALinkSpec()
    {
        var address = new UsbSerialAddress(0x0403, 0x6015, 0);
        Assert.AreEqual("usb:0403:6015:0", address.ToString());
        Assert.AreEqual(address, UsbSerialAddress.TryParse("usb:0403:6015:0"));
        Assert.AreEqual(new UsbSerialAddress(0x10C4, 0xEA60, 0), UsbSerialAddress.TryParse("USB:10C4:EA60"));
        Assert.IsNull(UsbSerialAddress.TryParse("COM3"));
        Assert.IsNull(UsbSerialAddress.TryParse("usb:zz:1"));

        var spec = LinkSpec.Serial(address.ToString(), 57600);
        Assert.AreEqual("serial:usb:0403:6015:0:57600", spec.ToString());
        Assert.AreEqual(spec, LinkSpec.Parse(spec.ToString()));

        // a replugged device has a new /dev path but the same ids: still found
        var ports = UsbSerialProbe.Ports([FtdiRadio() with { Name = "/dev/bus/usb/001/017" }]);
        Assert.AreEqual("/dev/bus/usb/001/017", UsbSerialProbe.Find(ports, address)!.Device.Name);
    }

    [TestMethod]
    public void LinkFactoryOpensUsbNamesThroughTheHostOnly()
    {
        Assert.IsInstanceOfType<LinkFactory.SerialLink>(LinkFactory.Create(LinkSpec.Serial("usb:0403:6015:0", 57600)));
        LinkFactory.UsbHost = new FakeHost();
        Assert.IsInstanceOfType<UsbSerialLink>(LinkFactory.Create(LinkSpec.Serial("usb:0403:6015:0", 57600)));
        Assert.IsInstanceOfType<LinkFactory.SerialLink>(LinkFactory.Create(LinkSpec.Serial("COM3", 57600)));
    }

    // --- the requests each chip gets

    [TestMethod]
    public void CdcAcmSetsLineCodingAndLines()
    {
        var c = new FakeConnection();
        var port = UsbSerialProbe.Ports(Cube())[1];
        UsbSerialDriver.For(c, port).Open(115200, dtr: true, rts: true);
        CollectionAssert.AreEqual(new[] { 2, 3 }, c.Claimed);
        Assert.AreEqual("21 20 0000 0002 [00 c2 01 00 00 00 08]", c.Controls[0]);
        Assert.AreEqual("21 22 0003 0002", c.Controls[1]);
    }

    [TestMethod]
    public void FtdiBaudDivisorsMatchTheChip()
    {
        Assert.AreEqual((0x001A, 0), FtdiDriver.BaudValue(115200));
        Assert.AreEqual((0xC034, 0), FtdiDriver.BaudValue(57600));
        Assert.AreEqual((0x4138, 0), FtdiDriver.BaudValue(9600));
        Assert.AreEqual((0x0003, 0), FtdiDriver.BaudValue(1_000_000));
        Assert.AreEqual((0x8003, 0), FtdiDriver.BaudValue(921600));
        Assert.AreEqual((0, 0), FtdiDriver.BaudValue(3_000_000));
        Assert.AreEqual((0x001A, 2), FtdiDriver.BaudValue(115200, multiPortIndex: 2));

        var c = new FakeConnection();
        UsbSerialDriver.For(c, UsbSerialProbe.Ports(FtdiRadio())[0]).Open(57600, true, true);
        CollectionAssert.AreEqual(new[]
        {
            "40 00 0000 0000", // reset
            "40 02 0000 0000", // no flow control
            "40 04 0008 0000", // 8N1
            "40 09 0001 0000", // 1 ms latency
            "40 03 c034 0000", // 57600
            "40 01 0303 0000", // DTR and RTS on
        }, c.Controls);
    }

    [TestMethod]
    public void FtdiStatusBytesAreRemovedFromEveryPacket()
    {
        // two full 64-byte packets and a short one, each led by two status bytes
        var data = new byte[64 + 64 + 5];
        for (var i = 0; i < data.Length; i++)
            data[i] = (byte)i;
        foreach (var start in new[] { 0, 64, 128 })
        {
            data[start] = 0x31;
            data[start + 1] = 0x60;
        }

        var n = UsbSerialLink.StripHeaders(data, data.Length, 64, 2);
        Assert.AreEqual(62 + 62 + 3, n);
        Assert.AreEqual(2, data[0]);
        Assert.AreEqual(63, data[61]);
        Assert.AreEqual(66, data[62]);
        Assert.AreEqual(130, data[124]);
        // a status-only packet is nothing
        Assert.AreEqual(0, UsbSerialLink.StripHeaders([0x31, 0x60], 2, 64, 2));
    }

    [TestMethod]
    public void Cp210xEnablesAndSetsTheBaudRate()
    {
        var c = new FakeConnection();
        UsbSerialDriver.For(c, UsbSerialProbe.Ports(Cp2102())[0]).Open(57600, true, false);
        CollectionAssert.AreEqual(new[]
        {
            "41 00 0001 0000",
            "41 03 0800 0000",
            "41 1e 0000 0000 [00 e1 00 00]",
            "41 12 000f 0000",
            "41 07 0301 0000",
        }, c.Controls);
    }

    [TestMethod]
    public void Ch340DivisorsAndInit()
    {
        Assert.AreEqual((0x9883, 0x10), Ch34xDriver.BaudValues(57600));
        Assert.AreEqual((0xCC83, 0x08), Ch34xDriver.BaudValues(115200));
        Assert.AreEqual((0xF387, 0x00), Ch34xDriver.BaudValues(921600));
        Assert.AreEqual((0xB282, 0x0C), Ch34xDriver.BaudValues(9600));

        var c = new FakeConnection();
        UsbSerialDriver.For(c, UsbSerialProbe.Ports(Ch340())[0]).Open(57600, true, true);
        CollectionAssert.AreEqual(new[]
        {
            "c0 5f 0000 0000 [00 00]",
            "40 a1 0000 0000",
            "40 9a 1312 9883",
            "40 9a 0f2c 0010",
            "c0 95 2518 0000 [00 00]",
            "40 9a 2518 00c3",
            "c0 95 0706 0000 [00 00]",
            "40 a1 501f d90a",
            "40 9a 1312 9883",
            "40 9a 0f2c 0010",
            "40 a4 ff9f 0000", // DTR and RTS on (active low)
        }, c.Controls);
    }

    [TestMethod]
    public void AFailedRequestFailsTheOpen()
    {
        var c = new FakeConnection { FailControl = true };
        Assert.ThrowsExactly<IOException>(() => UsbSerialDriver.For(c, UsbSerialProbe.Ports(Cp2102())[0]).Open(57600, true, true));
        var busy = new FakeConnection { FailClaim = true };
        var ex = Assert.ThrowsExactly<IOException>(() => UsbSerialDriver.For(busy, UsbSerialProbe.Ports(Cube())[0]).Open(115200, true, true));
        StringAssert.Contains(ex.Message, "another app");
    }

    // --- the link the MAVLink library reads

    [TestMethod]
    [Timeout(20000)]
    public void LinkReadsWritesAndNoticesTheUnplug()
    {
        var host = new FakeHost(FtdiRadio());
        var link = new UsbSerialLink(host, "usb:0403:6015:0", 57600) { ReadTimeout = 2000 };
        link.Open();
        Assert.IsTrue(link.IsOpen);
        var c = host.Last!;

        // the radio sends a MAVLink heartbeat start in a packet with its status bytes
        c.Incoming.Add([0x31, 0x60, 0xFD, 0x09, 0x00]);
        var buffer = new byte[8];
        Assert.AreEqual(1, link.Read(buffer, 0, 1)); // waits for the reader thread
        Assert.AreEqual(0xFD, buffer[0]);
        for (var i = 0; i < 50 && link.BytesToRead < 2; i++)
            Thread.Sleep(10);
        Assert.AreEqual(2, link.BytesToRead);
        Assert.AreEqual(0x09, link.ReadByte());

        link.Write([1, 2, 3], 0, 3);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, c.Written.ToArray());

        // nothing more: Read times out as a serial port does
        link.ReadTimeout = 100;
        Assert.AreEqual(0, link.ReadByte());
        Assert.ThrowsExactly<TimeoutException>(() => link.ReadByte());

        // unplugged: transfers fail at once and the link closes itself, so the session reconnects
        c.Unplugged = true;
        for (var i = 0; i < 300 && link.IsOpen; i++)
            Thread.Sleep(10);
        Assert.IsFalse(link.IsOpen);
        link.Close();
        Assert.IsTrue(c.Disposed);
    }

    [TestMethod]
    public void OpeningNeedsTheDeviceAndThePermission()
    {
        var host = new FakeHost();
        var ex = Assert.ThrowsExactly<IOException>(() => new UsbSerialLink(host, "usb:0403:6015:0", 57600).Open());
        StringAssert.Contains(ex.Message, "not plugged in");
        host.Devices_.Add(FtdiRadio());
        host.Allowed = false;
        ex = Assert.ThrowsExactly<IOException>(() => new UsbSerialLink(host, "usb:0403:6015:0", 57600).Open());
        StringAssert.Contains(ex.Message, "not allowed");
    }

    // --- the phone's Link tab

    [TestMethod]
    public void LinkTabOffersUsbOnlyWithAUsbHost()
    {
        Assert.IsFalse(new MobileLinkViewModel(FakeUi.Services(out _)).Kinds.Any(k => k.Kind == LinkKind.Serial));

        var host = new FakeHost();
        var vm = new MobileLinkViewModel(FakeUi.Services(out _), host);
        Assert.AreEqual(LinkKind.Serial, vm.Kinds[0].Kind);
        Assert.IsTrue(vm.IsUsb);
        Assert.IsFalse(vm.NeedsHost);
        Assert.IsFalse(vm.NeedsPort);
        Assert.IsTrue(vm.ShowNoUsb);
        Assert.IsNull(vm.BuildSpec());
        StringAssert.Contains(vm.Error, "Plug in");

        // the radio is plugged in: listed, chosen, at 57600
        host.Devices_.Add(FtdiRadio());
        vm.RefreshUsb();
        Assert.IsFalse(vm.ShowNoUsb);
        Assert.AreEqual("FT231X USB UART (FTDI)", vm.SelectedUsbPort!.Title);
        Assert.AreEqual("57600", vm.Baud);
        Assert.AreEqual(LinkSpec.Serial("usb:0403:6015:0", 57600), vm.BuildSpec());
        Assert.AreEqual("FT231X USB UART (FTDI) · 57600", vm.Describe(vm.BuildSpec()));

        // then the flight controller as well, while UDP is chosen: it takes over the choice
        vm.SelectedKind = vm.Kinds.Single(k => k.Kind == LinkKind.UdpListen);
        host.Devices_.Add(Cube());
        vm.RefreshUsb();
        Assert.IsTrue(vm.IsUsb);
        Assert.AreEqual("CubeOrange+ · port 1 of 2", vm.SelectedUsbPort!.Title);
        Assert.AreEqual("115200", vm.Baud);
        Assert.AreEqual(3, vm.UsbPorts.Count);

        vm.Baud = "fast";
        Assert.IsNull(vm.BuildSpec());
        StringAssert.Contains(vm.Error, "baud");
    }

    [TestMethod]
    public void RecentUsbLinksAreRestoredOnlyWhereUsbExists()
    {
        Settings.Instance[MobileLinkViewModel.RecentKey] = "serial:usb:0403:6015:0:57600|udp:14550";
        Assert.AreEqual(1, new MobileLinkViewModel(FakeUi.Services(out _)).Recent.Count);

        var vm = new MobileLinkViewModel(FakeUi.Services(out _), new FakeHost(FtdiRadio()));
        Assert.AreEqual(2, vm.Recent.Count);
        Assert.IsTrue(vm.IsUsb);
        Assert.AreEqual("usb:0403:6015:0", vm.SelectedUsbPort!.Address.ToString());
        Assert.AreEqual("57600", vm.Baud);
    }

    [TestMethod]
    public async Task ConnectAsksForThePermissionFirst()
    {
        var host = new FakeHost(FtdiRadio()) { Allowed = false, Grant = false };
        var vm = new MobileLinkViewModel(FakeUi.Services(out _), host);
        Assert.IsFalse(await vm.ConnectAsync(vm.BuildSpec()!));
        Assert.AreEqual(1, host.PermissionRequests);
        StringAssert.Contains(vm.Error, "not allowed");
        Assert.AreEqual("", vm.ProgressText);

        host.Devices_.Clear();
        Assert.IsFalse(await vm.ConnectAsync(LinkSpec.Serial("usb:0403:6015:0", 57600)));
        StringAssert.Contains(vm.Error, "not plugged in");
    }

    // --- fakes

    internal sealed class FakeHost(params UsbDeviceInfo[] devices) : IUsbHost
    {
        public List<UsbDeviceInfo> Devices_ { get; } = devices.ToList();

        public bool Allowed { get; set; } = true;

        public bool Grant { get; set; } = true;

        public int PermissionRequests { get; private set; }

        public FakeConnection? Last { get; private set; }

        public event EventHandler? DevicesChanged;

        public IReadOnlyList<UsbDeviceInfo> Devices() => Devices_.ToList();

        public bool HasPermission(UsbDeviceInfo device) => Allowed;

        public Task<bool> RequestPermissionAsync(UsbDeviceInfo device)
        {
            PermissionRequests++;
            Allowed = Grant;
            return Task.FromResult(Grant);
        }

        public IUsbConnection? Open(UsbDeviceInfo device) => Last = new FakeConnection();

        public void Changed() => DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    internal sealed class FakeConnection : IUsbConnection
    {
        public List<string> Controls { get; } = new();

        public List<int> Claimed { get; } = new();

        public BlockingCollection<byte[]> Incoming { get; } = new();

        public ConcurrentQueue<byte> Written { get; } = new();

        public bool FailControl { get; set; }

        public bool FailClaim { get; set; }

        public volatile bool Unplugged;

        public bool Disposed { get; private set; }

        public bool ClaimInterface(int number)
        {
            Claimed.Add(number);
            return !FailClaim;
        }

        public int ControlTransfer(int requestType, int request, int value, int index, byte[]? buffer, int length, int timeoutMs)
        {
            var text = $"{requestType:x2} {request:x2} {value:x4} {index:x4}";
            if (buffer != null && length > 0)
                text += " [" + string.Join(" ", buffer.Take(length).Select(b => b.ToString("x2"))) + "]";
            Controls.Add(text);
            return FailControl ? -1 : length;
        }

        public int BulkTransfer(UsbEndpointInfo endpoint, byte[] buffer, int offset, int length, int timeoutMs)
        {
            if (Unplugged)
                return -1;
            if (!endpoint.IsIn)
            {
                for (var i = 0; i < length; i++)
                    Written.Enqueue(buffer[offset + i]);
                return length;
            }

            if (!Incoming.TryTake(out var packet, timeoutMs))
                return Unplugged ? -1 : -1;
            Array.Copy(packet, 0, buffer, offset, packet.Length);
            return packet.Length;
        }

        public void Dispose() => Disposed = true;
    }
}
