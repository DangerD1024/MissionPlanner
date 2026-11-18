using System.Text;
using MissionPlanner.App.Core.ViewModels.SetupScreens;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Comms;

namespace MissionPlanner.App.Tests.SetupScreens.Comms;

[TestClass]
public sealed class SlcanTests
{
    [TestMethod]
    public void ParsesAndFormatsExtendedFrames()
    {
        // NodeStatus from node 10: id, DLC 8, 7 payload bytes and the tail byte
        const string line = "T" + "1001550A" + "8" + "E0030000000000C0";
        var f = Slcan.Parse(line)!;
        Assert.AreEqual(0x1001550Au, f.Id);
        Assert.IsTrue(f.Extended);
        Assert.IsFalse(f.Fd);
        CollectionAssert.AreEqual(new byte[] { 0xE0, 0x03, 0, 0, 0, 0, 0, 0xC0 }, f.Data);
        Assert.AreEqual(line + "\r", Slcan.Format(f));
        Assert.AreEqual("t1232AA55\r", Slcan.Format(new CanFrame(0x123, [0xAA, 0x55], Extended: false)));
    }

    [TestMethod]
    public void RoundTripsAllFrameKinds()
    {
        foreach (var frame in new[]
                 {
                     new CanFrame(0x1E0AF9FE, [1, 2, 3]),
                     new CanFrame(0x123, [0xAA, 0x55], Extended: false),
                     new CanFrame(0x18000101, Enumerable.Range(0, 12).Select(i => (byte)i).ToArray(), Fd: true),
                     new CanFrame(0x1000007F, []),
                 })
        {
            var back = Slcan.Parse(Slcan.Format(frame))!;
            Assert.AreEqual(frame.Id, back.Id);
            Assert.AreEqual(frame.Extended, back.Extended);
            Assert.AreEqual(frame.Fd, back.Fd);
            CollectionAssert.AreEqual(frame.Data, back.Data);
        }
    }

    [TestMethod]
    public void FdLengthsMapToDlcs()
    {
        Assert.AreEqual(8, Slcan.LengthToDlc(8));
        Assert.AreEqual(9, Slcan.LengthToDlc(12));
        Assert.AreEqual(9, Slcan.LengthToDlc(10), "rounded up to the next FD size");
        Assert.AreEqual(15, Slcan.LengthToDlc(64));
        Assert.AreEqual(48, Slcan.DlcToLength(14));
        var padded = Slcan.Parse(Slcan.Format(new CanFrame(0x100, new byte[10], Fd: true)))!;
        Assert.HasCount(12, padded.Data);
    }

    [TestMethod]
    public void RejectsControlAndMalformedLines()
    {
        Assert.IsNull(Slcan.Parse("O"));
        Assert.IsNull(Slcan.Parse("S8"));
        Assert.IsNull(Slcan.Parse("T12"));
        Assert.IsNull(Slcan.Parse("T1234567G1AA"));
        Assert.IsNull(Slcan.Parse("T123456782AA"), "two bytes announced, one given");
    }

    [TestMethod]
    public void BridgeAnswersAdapterCommandsAndPassesFramesBothWays()
    {
        using var bridge = new SlcanBridgeStream { ReadTimeout = 200 };
        var sent = new List<CanFrame>();
        bridge.FrameSent += sent.Add;

        // the DroneCAN library's StartSLCAN handshake: C, S8, N, V, O, F — each answered at once
        Write(bridge, "\r\r\rC\rS8\rN\rV\rO\rF\r");
        var reply = ReadAll(bridge);
        Assert.AreEqual("\r\rNMAVL\rV1013\r\rF00\r", reply);

        // a frame the library sends
        Write(bridge, "T1E0AF9FE300112");
        Assert.IsEmpty(sent, "nothing until the CR");
        Write(bridge, "C1\r");
        Assert.HasCount(1, sent);
        Assert.AreEqual(0x1E0AF9FEu, sent[0].Id);
        CollectionAssert.AreEqual(new byte[] { 0x00, 0x11, 0x2C }, sent[0].Data);

        // a frame from the bus
        bridge.Receive(new CanFrame(0x1001550A, [1, 2, 3, 4, 5, 6, 7, 0xC0]));
        Assert.AreEqual("T1001550A801020304050607C0\r", ReadAll(bridge));
        Assert.AreEqual(0, bridge.Read(new byte[8], 0, 8), "times out empty");
    }

    private static void Write(Stream s, string text)
    {
        var b = Encoding.ASCII.GetBytes(text);
        s.Write(b, 0, b.Length);
    }

    private static string ReadAll(Stream s)
    {
        var sb = new StringBuilder();
        var buffer = new byte[512];
        int n;
        while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
        {
            sb.Append(Encoding.ASCII.GetString(buffer, 0, n));
            if (s is SlcanBridgeStream b)
                b.ReadTimeout = 50;
        }

        return sb.ToString();
    }
}

[TestClass]
public sealed class MavlinkCanForwarderTests
{
    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task KeepsForwardingAliveAndStopsWithBusZero()
    {
        var vehicle = new FakeCanTransport();
        var fwd = new MavlinkCanForwarder(vehicle, bus: 2, targetSystem: 1, targetComponent: 1) { KeepAliveMs = 30 };
        fwd.Start();
        Assert.IsTrue(vehicle.Subscribed);
        await Wait.UntilAsync(() => vehicle.ForwardRequests.Count >= 3, what: "repeated CAN_FORWARD");
        Assert.IsTrue(vehicle.ForwardRequests.All(b => b == 2));

        vehicle.Inject(MAVLink.MAVLINK_MSG_ID.COMMAND_ACK, new MAVLink.mavlink_command_ack_t
        {
            command = (ushort)MAVLink.MAV_CMD.CAN_FORWARD,
            result = (byte)MAVLink.MAV_RESULT.ACCEPTED,
        });
        Assert.AreEqual(MAVLink.MAV_RESULT.ACCEPTED, fwd.ForwardResult);

        fwd.Dispose();
        Assert.AreEqual(0, vehicle.ForwardRequests.Last(), "bus 0 stops forwarding");
        Assert.IsFalse(vehicle.Subscribed);
    }

    [TestMethod]
    public void FramesGoBothWaysOnTheChosenBus()
    {
        var vehicle = new FakeCanTransport();
        using var fwd = new MavlinkCanForwarder(vehicle, bus: 1, targetSystem: 7, targetComponent: 1) { KeepAliveMs = 100000 };
        fwd.Start();

        // vehicle → library: extended flag (bit 31) dropped, frames of the other bus ignored
        vehicle.Inject(MAVLink.MAVLINK_MSG_ID.CAN_FRAME,
            new MAVLink.mavlink_can_frame_t(0x9001550A, 255, 190, 0, 8, [1, 0, 0, 0, 0, 0, 0, 0xC0]));
        vehicle.Inject(MAVLink.MAVLINK_MSG_ID.CAN_FRAME,
            new MAVLink.mavlink_can_frame_t(0x9001550B, 255, 190, 1, 8, [1, 0, 0, 0, 0, 0, 0, 0xC0]));
        var buffer = new byte[256];
        fwd.Stream.ReadTimeout = 200;
        var text = Encoding.ASCII.GetString(buffer, 0, fwd.Stream.Read(buffer, 0, buffer.Length));
        Assert.AreEqual("T1001550A" + "8" + "01000000000000C0" + "\r", text);
        Assert.AreEqual(1, fwd.FramesFromVehicle);

        // library → vehicle: CAN_FRAME on bus index 0 with the extended flag set
        var line = Encoding.ASCII.GetBytes("T1E0AF9FE3AABBCC\r");
        fwd.Stream.Write(line, 0, line.Length);
        var frame = (MAVLink.mavlink_can_frame_t)vehicle.Sent.Single();
        Assert.AreEqual(0x9E0AF9FEu, frame.id);
        Assert.AreEqual(0, frame.bus);
        Assert.AreEqual(3, frame.len);
        Assert.AreEqual(7, frame.target_system);
        CollectionAssert.AreEqual(new byte[] { 0xAA, 0xBB, 0xCC }, frame.data.Take(3).ToArray());

        // CAN FD frames go out as CANFD_FRAME with a DLC
        var fd = Encoding.ASCII.GetBytes("D1E0AF9FE9" + new string('0', 24) + "\r");
        fwd.Stream.Write(fd, 0, fd.Length);
        var fdFrame = (MAVLink.mavlink_canfd_frame_t)vehicle.Sent.Last();
        Assert.AreEqual(9, fdFrame.len);
    }
}

[TestClass]
public sealed class DroneCanViewModelTests
{
    private static (DroneCanViewModel Vm, CommsUi Ui, FakeDroneCanBus Bus) Create(bool library = true)
    {
        var ui = new CommsUi();
        var bus = new FakeDroneCanBus();
        var services = ui.Services();
        var vm = new DroneCanViewModel(services, library, () => bus,
            b => new MavlinkCanForwarder(new FakeCanTransport(), b, 1, 1));
        return (vm, ui, bus);
    }

    [TestMethod]
    public void WithoutTheLibraryThePageSaysSo()
    {
        var (vm, ui, bus) = Create(library: false);
        Assert.IsTrue(vm.LibraryMissing);
        StringAssert.Contains(vm.Status, "DroneCAN library");
        vm.ToggleConnectCommand.Execute(null);
        Assert.IsFalse(bus.Started);
        Assert.HasCount(1, ui.Messages);
    }

    [TestMethod]
    public async Task MavlinkModeNeedsAVehicle()
    {
        var (vm, ui, bus) = Create();
        await vm.ToggleConnectCommand.ExecuteAsync(null);
        Assert.IsFalse(vm.IsConnected);
        Assert.IsFalse(bus.Started);
        StringAssert.Contains(ui.Messages.Single(), "Connect to the vehicle first");
    }

    [TestMethod]
    public void NodesAreListedInIdOrderAndGoStale()
    {
        var (vm, _, bus) = Create();
        vm.AttachForTest(bus);

        bus.RaiseStatus(new CanNodeStatus(125, CanNodeHealth.Ok, CanNodeMode.Operational, 3700, 0));
        bus.RaiseStatus(new CanNodeStatus(10, CanNodeHealth.Warning, CanNodeMode.Initialization, 5, 0));
        bus.RaiseInfo(new CanNodeInfo(125, "org.ardupilot.f303_gps", "1.6 (deadbeef)", "1.0", "00112233", 42));
        bus.RaiseLog(new CanLogMessage(125, 2, "GPS", "no fix"));

        CollectionAssert.AreEqual(new byte[] { 10, 125 }, vm.Nodes.Select(n => n.Id).ToArray());
        var gps = vm.Nodes[1];
        Assert.AreEqual("org.ardupilot.f303_gps", gps.Name);
        Assert.AreEqual("01:01:40", gps.UptimeText);
        Assert.AreEqual("OPERATIONAL", gps.ModeText);
        Assert.IsTrue(vm.Nodes[0].IsWarning);
        Assert.AreEqual(125, vm.SelectedNode!.Id, "the first node seen is selected");
        Assert.AreEqual("WARNING", vm.LogMessages.Single().Level);

        gps.LastSeen -= 5000;
        for (var i = 0; i < 20; i++)
            vm.Tick();
        Assert.IsTrue(gps.IsStale);
        Assert.AreEqual("NOT SEEN", gps.ModeText);
        Assert.IsFalse(vm.Nodes[0].IsStale);

        Assert.AreEqual("2d 00:00:01", CanNodeRow.FormatUptime(2 * 86400 + 1));
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task ParametersAreReadEditedWrittenAndSaved()
    {
        var (vm, ui, bus) = Create();
        bus.Params[125] =
        [
            new CanParameter("GPS_TYPE", CanParamKind.Integer, 1, Default: 1, Min: 0, Max: 20),
            new CanParameter("BARO_ENABLE", CanParamKind.Boolean, 1),
            new CanParameter("MAG_SCALE", CanParamKind.Real, 1.5),
            new CanParameter("CAN_NODE_NAME", CanParamKind.String, 0, "gps"),
        ];
        vm.AttachForTest(bus);
        bus.RaiseStatus(new CanNodeStatus(125, CanNodeHealth.Ok, CanNodeMode.Operational, 1, 0));

        await vm.ReadParametersCommand.ExecuteAsync(null);
        Assert.AreEqual((byte)125, vm.ParamsNodeId);
        CollectionAssert.AreEqual(new[] { "BARO_ENABLE", "CAN_NODE_NAME", "GPS_TYPE", "MAG_SCALE" }, vm.Parameters.Select(p => p.Name).ToArray());
        var gpsType = vm.Parameters.Single(p => p.Name == "GPS_TYPE");
        Assert.AreEqual("1", gpsType.ValueText);
        Assert.AreEqual("0 … 20", gpsType.RangeText);

        gpsType.ValueText = "25";
        Assert.IsTrue(gpsType.IsInvalid, "above the node's maximum");
        await vm.WriteParametersCommand.ExecuteAsync(null);
        Assert.IsFalse(bus.Calls.Any(c => c.StartsWith("set")));
        StringAssert.Contains(ui.Messages.Last(), "GPS_TYPE");

        gpsType.ValueText = "9";
        vm.Parameters.Single(p => p.Name == "MAG_SCALE").ValueText = "0.75";
        vm.Parameters.Single(p => p.Name == "CAN_NODE_NAME").ValueText = "front gps";
        await vm.WriteParametersCommand.ExecuteAsync(null);
        CollectionAssert.IsSubsetOf(new[] { "set 125 GPS_TYPE=9", "set 125 MAG_SCALE=0.75", "set 125 CAN_NODE_NAME=front gps" }, bus.Calls.ToList());
        Assert.IsFalse(gpsType.IsChanged, "written values are the node's now");

        await vm.SaveParametersCommand.ExecuteAsync(null);
        Assert.Contains("save 125", bus.Calls.ToList());
        StringAssert.Contains(vm.Status, "stored");

        await vm.RestartNodeCommand.ExecuteAsync(null);
        Assert.Contains("restart 125", bus.Calls.ToList());
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task FirmwareUpdateServesTheChosenFile()
    {
        var (vm, ui, bus) = Create();
        vm.AttachForTest(bus);
        bus.RaiseStatus(new CanNodeStatus(125, CanNodeHealth.Ok, CanNodeMode.Operational, 1, 0));
        var file = Path.Combine(Path.GetTempPath(), $"fw-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(file, new byte[1024]);
        ui.OpenPath = file;
        try
        {
            await vm.UpdateFirmwareCommand.ExecuteAsync(null);
            Assert.Contains("update 125", bus.Calls.ToList());
            Assert.AreEqual(file, bus.FirmwareFile);
            StringAssert.Contains(vm.Status, "finished");
        }
        finally
        {
            File.Delete(file);
        }
    }
}
