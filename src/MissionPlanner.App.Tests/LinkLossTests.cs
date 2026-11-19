using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.Session;

namespace MissionPlanner.App.Tests;

/// <summary>
/// A link that goes away must not leave the app showing "connected": an unplugged USB device
/// disconnects; TCP and UDP links show "reconnecting" and carry on when the vehicle is back.
/// </summary>
[TestClass]
public class LinkLossTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void MissingSerialDeviceIsReported()
    {
        var spec = LinkSpec.Serial("/dev/serial/by-id/usb-ArduPilot_X-if00", 115200);
        Assert.IsNull(LinkHealth.Check(spec, null, _ => true));
        StringAssert.Contains(LinkHealth.Check(spec, null, _ => false), "disconnected");
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task TcpPeerCloseIsSeenWithoutReading()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var server = await listener.AcceptTcpClientAsync();
        listener.Stop();

        Assert.IsFalse(LinkHealth.TcpPeerClosed(client), "open connection");
        server.Close();
        await WaitFor(() => LinkHealth.TcpPeerClosed(client), TimeSpan.FromSeconds(5), "peer close");
        Assert.IsTrue(client.Client.Connected, "the socket itself still claims to be connected");
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task UnpluggedSerialDeviceDisconnects()
    {
        if (OperatingSystem.IsWindows())
            Assert.Inconclusive("pseudo-terminal pair needs socat (Linux/macOS)");
        var pty = ConnectTests.PtyPair.TryStart();
        if (pty == null)
            Assert.Inconclusive("socat is not installed");

        var vehiclePort = new System.IO.Ports.SerialPort(pty.VehiclePath, 115200) { ReadTimeout = 500, WriteTimeout = 500 };
        vehiclePort.Open();
        var vehicle = new FakeVehicle(vehiclePort.BaseStream, 5);

        await using var session = new VehicleSession();
        await session.ConnectAsync(LinkSpec.Serial(pty.GcsPath, 115200), getParams: false);
        var lost = new TaskCompletionSource<string>();
        session.LinkLost += (_, reason) => lost.TrySetResult(reason);
        await WaitFor(() => session.Cs.roll != 0, TimeSpan.FromSeconds(10), "telemetry");

        // what unplugging does to the ground station's port: the device under it goes away
        pty.Dispose();

        var sw = Stopwatch.StartNew();
        var reason = await lost.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(LinkState.Disconnected, session.State);
        TestContext.WriteLine($"{reason} after {sw.ElapsedMilliseconds} ms");

        // a later Disconnect must not hang on the dead port either
        var disconnect = session.DisconnectAsync();
        Assert.AreSame(disconnect, await Task.WhenAny(disconnect, Task.Delay(5000)), "disconnect hung");

        // the vehicle end is abandoned on purpose: its port is dead and closing it can block
        _ = Task.Run(async () =>
        {
            await vehicle.DisposeAsync();
            vehiclePort.Dispose();
        });
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task TcpDropShowsReconnectingAndResumes()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        await using var session = new VehicleSession();
        var lostEvents = 0;
        session.LinkLost += (_, _) => Interlocked.Increment(ref lostEvents);

        var connecting = session.ConnectAsync(LinkSpec.Tcp("127.0.0.1", port), getParams: false);
        var first = await listener.AcceptTcpClientAsync();
        var vehicle = new FakeVehicle(first.GetStream(), 5);
        await connecting;
        await WaitFor(() => session.Cs.roll > 4, TimeSpan.FromSeconds(10), "telemetry");

        // the vehicle side goes away (SITL stopped, Wi-Fi dropped)
        await vehicle.DisposeAsync();
        first.Close();
        await WaitFor(() => session.IsReconnecting, TimeSpan.FromSeconds(6), "reconnecting");
        Assert.AreEqual(LinkState.Connected, session.State, "a TCP drop is not a disconnect");

        // ...and comes back on the same port: the session reopens the socket by itself
        var second = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await using var back = new FakeVehicle(second.GetStream(), 9);
        await WaitFor(() => !session.IsReconnecting && session.Cs.roll > 8, TimeSpan.FromSeconds(10), "resumed telemetry");
        Assert.AreEqual(0, lostEvents);

        listener.Stop();
        second.Dispose();
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task SilentUdpLinkShowsReconnectingAndRecovers()
    {
        int port;
        using (var probe = new UdpClient(0))
            port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;

        using var sender = new UdpClient();
        var parse = new MAVLink.MavlinkParse();
        var sending = true;
        using var stop = new CancellationTokenSource();
        var seq = 0;
        var loop = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (Volatile.Read(ref sending))
                {
                    var hb = parse.GenerateMAVLinkPacket20(MAVLink.MAVLINK_MSG_ID.HEARTBEAT, new MAVLink.mavlink_heartbeat_t
                    {
                        type = (byte)MAVLink.MAV_TYPE.QUADROTOR,
                        autopilot = (byte)MAVLink.MAV_AUTOPILOT.ARDUPILOTMEGA,
                        mavlink_version = 3,
                    }, false, 1, 1, seq++);
                    await sender.SendAsync(hb, hb.Length, new IPEndPoint(IPAddress.Loopback, port));
                }

                await Task.Delay(100);
            }
        });

        await using var session = new VehicleSession();
        var lostEvents = 0;
        session.LinkLost += (_, _) => Interlocked.Increment(ref lostEvents);
        await session.ConnectAsync(LinkSpec.UdpListen(port), getParams: false);
        Assert.IsFalse(session.IsReconnecting);

        Volatile.Write(ref sending, false);
        await WaitFor(() => session.IsReconnecting, TimeSpan.FromSeconds(6), "reconnecting");
        Assert.AreEqual(LinkState.Connected, session.State, "UDP keeps listening");

        Volatile.Write(ref sending, true);
        await WaitFor(() => !session.IsReconnecting, TimeSpan.FromSeconds(4), "recovery");
        Assert.AreEqual(0, lostEvents);

        await stop.CancelAsync();
        await loop;
    }

    private static async Task WaitFor(Func<bool> condition, TimeSpan timeout, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout)
                Assert.Fail("timed out waiting for " + what);
            await Task.Delay(50);
        }
    }
}
