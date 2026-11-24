using System.Net;
using System.Net.Sockets;
using System.Text;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Comms;
using MissionPlanner.App.Core.ViewModels.Tools;
using MissionPlanner.App.Tests.FlightData;
using MissionPlanner.App.Tests.SetupScreens.Comms;

namespace MissionPlanner.App.Tests.Tools;

/// <summary>TOOLS > MAVSerial pass: a TCP client bridged to the vehicle's GPS port over SERIAL_CONTROL.</summary>
[TestClass]
public class MavSerialPassTests
{
    private static FakeSerialControlVehicle GpsVehicle() => new()
    {
        ReplyDevice = (byte)MAVLink.SERIAL_CONTROL_DEV.GPS1,
        Echo = false,
    };

    private static byte[] ReadAtLeast(NetworkStream stream, int count, int timeoutMs = 5000)
    {
        var got = new List<byte>();
        var buffer = new byte[1024];
        stream.ReadTimeout = timeoutMs;
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (got.Count < count && DateTime.UtcNow < deadline)
        {
            var n = stream.Read(buffer, 0, buffer.Length);
            if (n == 0)
                break;
            got.AddRange(buffer.AsSpan(0, n).ToArray());
        }

        return got.ToArray();
    }

    private static bool WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
            Thread.Sleep(20);
        return condition();
    }

    [TestMethod]
    public void BytesFlowBothWaysAndThePortIsHeldExclusively()
    {
        var vehicle = GpsVehicle();
        var link = new SerialControlLink(vehicle, MAVLink.SERIAL_CONTROL_DEV.GPS1) { FastPollMs = 10, IdlePollMs = 20 };
        using var bridge = new TcpSerialBridge(link, 0, IPAddress.Loopback);
        bridge.Start();
        Assert.IsFalse(link.IsOpen, "the vehicle port is taken only when a client connects");

        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, bridge.LocalPort);
        var stream = client.GetStream();

        // a UBX poll from the GPS tool reaches the GPS
        var poll = new byte[] { 0xB5, 0x62, 0x0A, 0x04, 0x00, 0x00, 0x0E, 0x34 };
        stream.Write(poll);
        Assert.IsTrue(WaitFor(() => vehicle.Input.Count >= poll.Length), "the poll reached the vehicle");
        CollectionAssert.AreEqual(poll, vehicle.Input.Take(poll.Length).ToArray());

        // what the GPS prints comes back, longer than one 70-byte SERIAL_CONTROL packet
        var nmea = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("$GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,*47\r\n", 3)));
        vehicle.Output.Append(nmea);
        var back = ReadAtLeast(stream, nmea.Length);
        CollectionAssert.AreEqual(nmea, back);

        Assert.IsTrue(bridge.ClientConnected);
        Assert.AreEqual(poll.Length, bridge.BytesToDevice);
        Assert.AreEqual(nmea.Length, bridge.BytesFromDevice);

        var sent = vehicle.Sent.ToArray();
        Assert.IsTrue(sent.All(p => p.device == (byte)MAVLink.SERIAL_CONTROL_DEV.GPS1));
        Assert.IsTrue(sent.All(p => (p.flags & (byte)MAVLink.SERIAL_CONTROL_FLAG.EXCLUSIVE) != 0), "exclusive while bridged");

        // a second tool is turned away while the first is connected
        using (var second = new TcpClient())
        {
            second.Connect(IPAddress.Loopback, bridge.LocalPort);
            var s2 = second.GetStream();
            s2.ReadTimeout = 3000;
            var buf = new byte[16];
            int n;
            try
            {
                n = s2.Read(buf, 0, buf.Length);
            }
            catch (IOException)
            {
                n = 0;
            }

            Assert.AreEqual(0, n, "the second connection is closed");
        }

        bridge.Stop();
        Assert.IsFalse(link.IsOpen);
        var last = vehicle.Sent.Last();
        Assert.AreEqual(0, last.flags, "stopping hands the port back to the autopilot (no EXCLUSIVE)");
    }

    [TestMethod]
    public void AClientCanReconnectAfterTheFirstLeaves()
    {
        var vehicle = GpsVehicle();
        var link = new SerialControlLink(vehicle, MAVLink.SERIAL_CONTROL_DEV.GPS1) { FastPollMs = 10, IdlePollMs = 20 };
        using var bridge = new TcpSerialBridge(link, 0, IPAddress.Loopback);
        bridge.Start();

        using (var first = new TcpClient())
        {
            first.Connect(IPAddress.Loopback, bridge.LocalPort);
            Assert.IsTrue(WaitFor(() => bridge.ClientConnected));
        }

        Assert.IsTrue(WaitFor(() => !bridge.ClientConnected), "the bridge notices the client left");

        using var again = new TcpClient();
        again.Connect(IPAddress.Loopback, bridge.LocalPort);
        again.GetStream().Write("hi"u8.ToArray());
        Assert.IsTrue(WaitFor(() => vehicle.Input.Count >= 2), "the second session reaches the vehicle");
        Assert.AreEqual(2, bridge.Clients);
    }

    [TestMethod]
    public void PageStartsAndStopsTheBridge()
    {
        var vm = new MavSerialPassViewModel(FakeUi.Services(out _));
        Assert.AreEqual(MAVLink.SERIAL_CONTROL_DEV.GPS1, vm.Device!.Device, "GPS1 by default, as Mission Planner");

        vm.StartStopCommand.Execute(null);
        Assert.IsFalse(vm.IsRunning, "not connected: nothing to bridge");
        Assert.IsTrue(vm.StatusIsError);

        vm.TcpPort = 0;
        var vehicle = GpsVehicle();
        Assert.IsTrue(vm.Start(new SerialControlLink(vehicle, MAVLink.SERIAL_CONTROL_DEV.GPS1)));
        Assert.IsTrue(vm.IsRunning);
        Assert.AreEqual("Stop", vm.StartStopText);
        vm.Tick();
        StringAssert.Contains(vm.ClientText, "Waiting");

        vm.StartStopCommand.Execute(null);
        Assert.IsFalse(vm.IsRunning);
        Assert.IsNull(vm.Bridge);
    }

    [TestMethod]
    public void TakenPortIsReported()
    {
        var blocker = new TcpListener(IPAddress.Any, 0);
        blocker.Start();
        try
        {
            var vm = new MavSerialPassViewModel(FakeUi.Services(out _)) { TcpPort = ((IPEndPoint)blocker.LocalEndpoint).Port };
            Assert.IsFalse(vm.Start(new SerialControlLink(GpsVehicle(), MAVLink.SERIAL_CONTROL_DEV.GPS1)));
            Assert.IsFalse(vm.IsRunning);
            Assert.IsTrue(vm.StatusIsError);
        }
        finally
        {
            blocker.Stop();
        }
    }
}
