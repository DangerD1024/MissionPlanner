using System.Diagnostics;
using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.Session;

namespace MissionPlanner.App.Tests;

[TestClass]
public class AutoConnectTests
{
    [TestMethod]
    public void ListeningPortIsSeenWithoutConnecting()
    {
        // the Windows SITL exits when its first client disconnects, so the check must not connect at all
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            Assert.IsTrue(AutoConnectService.IsListening(port));
            Assert.IsFalse(listener.Pending(), "nobody connected to look");
        }
        finally
        {
            listener.Stop();
        }

        Assert.IsFalse(AutoConnectService.IsListening(port));
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task WindowsSitlSurvivesTheAutoConnectCheckAndConnects()
    {
        // the real simulator, when this computer has it (Mission Planner downloads it on first use)
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mission Planner", "sitl", "ArduCopter.exe");
        if (!OperatingSystem.IsWindows() || !File.Exists(exe) || AutoConnectService.IsListening(5760))
            Assert.Inconclusive("needs the Windows SITL and a free port 5760");
        // a Mission Planner running on this computer with Auto-connect on takes the simulator's one TCP client
        if (Process.GetProcessesByName("MissionPlanner.App").Length > 0)
            Assert.Inconclusive("Mission Planner is running here; its auto-connect would take the simulator's connection");

        var dir = Path.Combine(TestEnvironment.DataRoot, "sitl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var defaults = Path.Combine(Path.GetDirectoryName(exe)!, "copter.parm");
        using var sitl = Process.Start(new ProcessStartInfo(exe,
            "-M+ -O-35.363261,149.165230,584,353 -s1 --serial0 tcp:0" + (File.Exists(defaults) ? $" --defaults \"{defaults}\"" : ""))
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        sitl.OutputDataReceived += (_, _) => { };
        sitl.ErrorDataReceived += (_, _) => { };
        sitl.BeginOutputReadLine();
        sitl.BeginErrorReadLine();
        try
        {
            var auto = new AutoConnectService(() => true);
            var sw = Stopwatch.StartNew();
            while (!auto.ProbeTcp(TimeSpan.FromSeconds(1), CancellationToken.None) && sw.Elapsed < TimeSpan.FromSeconds(20))
                await Task.Delay(200);
            for (var i = 0; i < 5; i++)
                Assert.IsTrue(auto.ProbeTcp(TimeSpan.FromSeconds(1), CancellationToken.None));
            await Task.Delay(1000);
            Assert.IsFalse(sitl.HasExited, "the auto-connect check must not end the simulator");

            await using var session = new VehicleSession();
            await session.ConnectAsync(LinkSpec.Tcp("127.0.0.1", 5760), getParams: false);
            Assert.AreEqual(LinkState.Connected, session.State);
            await session.DisconnectAsync();
        }
        finally
        {
            if (!sitl.HasExited)
                sitl.Kill();
        }
    }

    [TestMethod]
    public void CandidatesProbeEachDeviceOnceMavlinkInterfaceFirst()
    {
        var links = new Dictionary<string, string>
        {
            ["/dev/serial/by-id/usb-ArduPilot_MatekH743_3B00-if00"] = "/dev/ttyACM0",
            ["/dev/serial/by-id/usb-ArduPilot_MatekH743_3B00-if02"] = "/dev/ttyACM1",
            ["/dev/serial/by-id/usb-FTDI_FT231X_USB_UART_D30-if00-port0"] = "/dev/ttyUSB0",
        };
        string Resolve(string p) => links.GetValueOrDefault(p, p);

        var ports = new[] { "/dev/ttyACM0", "/dev/ttyACM1", "/dev/ttyUSB0", "/dev/ttyS0" }.Concat(links.Keys).ToList();
        var c = AutoConnectService.Candidates(ports, Resolve);

        Assert.HasCount(3, c, "one entry per physical device, legacy ttyS skipped");
        Assert.AreEqual("/dev/serial/by-id/usb-ArduPilot_MatekH743_3B00-if00", c[0].Port);
        Assert.AreEqual("/dev/serial/by-id/usb-ArduPilot_MatekH743_3B00-if02", c[^1].Port, "second CDC interface is tried last");
        var radio = c.Single(x => x.Port.Contains("FTDI"));
        CollectionAssert.AreEqual(new[] { 57600, 115200 }, radio.Bauds, "radios run at 57600");
        CollectionAssert.AreEqual(new[] { 115200 }, c[0].Bauds);
    }

    [TestMethod]
    public void OnlyVehicleHeartbeatsCount()
    {
        var parse = new MAVLink.MavlinkParse();
        byte[] Hb(MAVLink.MAV_TYPE type, byte compid) => parse.GenerateMAVLinkPacket20(MAVLink.MAVLINK_MSG_ID.HEARTBEAT,
            new MAVLink.mavlink_heartbeat_t { type = (byte)type, autopilot = (byte)MAVLink.MAV_AUTOPILOT.ARDUPILOTMEGA }, false, 1, compid);

        var noise = new byte[] { 1, 2, 3, 0xfd, 9 };
        Assert.IsTrue(SerialProbe.ContainsVehicleHeartbeat(new MAVLink.MavlinkParse(), [.. noise, .. Hb(MAVLink.MAV_TYPE.QUADROTOR, 1)]));
        Assert.IsFalse(SerialProbe.ContainsVehicleHeartbeat(new MAVLink.MavlinkParse(), Hb(MAVLink.MAV_TYPE.GCS, 190)), "another ground station");
        Assert.IsFalse(SerialProbe.ContainsVehicleHeartbeat(new MAVLink.MavlinkParse(), "$GPGGA,123519,4807.038,N*47\r\n"u8.ToArray()), "NMEA from a GPS");
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task ScanReportsTheFirstPortWithAVehicle()
    {
        var probed = new List<(string, int)>();
        using var svc = new AutoConnectService(() => true)
        {
            ProbeUdp = () => false,
            ProbeTcp = (_, _) => false,
            ListPorts = () => ["/dev/ttyACM5", "/dev/ttyACM6"],
            ProbeSerial = (port, baud, _, _) =>
            {
                lock (probed)
                    probed.Add((port, baud));
                return port == "/dev/ttyACM6";
            },
        };
        var found = new TaskCompletionSource<LinkSpec>();
        svc.Found += (spec, _) => found.TrySetResult(spec);
        svc.Start();

        var spec = await found.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.AreEqual(LinkSpec.Serial("/dev/ttyACM6", 115200), spec);
        lock (probed)
            CollectionAssert.Contains(probed, ("/dev/ttyACM5", 115200));
    }

    [TestMethod]
    public void AHoldMatchesTheSameDeviceUnderAnyName()
    {
        using var svc = new AutoConnectService(() => true)
        {
            ResolvePort = p => p == "/dev/serial/by-id/usb-ArduPilot_MatekH743_3B00-if00" ? "/dev/ttyACM0" : p,
        };
        svc.HoldOff(LinkSpec.Serial("/dev/serial/by-id/usb-ArduPilot_MatekH743_3B00-if00", 115200));
        Assert.IsTrue(svc.IsHeld(LinkSpec.Serial("/dev/ttyACM0", 57600)));
        Assert.IsFalse(svc.IsHeld(LinkSpec.Serial("/dev/ttyACM1")));
        svc.Release(LinkSpec.Serial("/dev/ttyACM0"));
        Assert.IsFalse(svc.IsHeld(LinkSpec.Serial("/dev/serial/by-id/usb-ArduPilot_MatekH743_3B00-if00")));

        svc.HoldOff(LinkSpec.Tcp("localhost", 5760));
        Assert.IsTrue(svc.IsHeld(LinkSpec.Tcp("127.0.0.1", 5760)));
        Assert.IsFalse(svc.IsHeld(LinkSpec.Tcp("127.0.0.1", 5770)));
        svc.ReleaseAll();
        Assert.IsFalse(svc.IsHeld(LinkSpec.Tcp("127.0.0.1", 5760)));
    }

    [TestMethod]
    [Timeout(40000, CooperativeCancellation = true)]
    public async Task AUsbVehicleDisconnectedByTheUserIsReconnectedOnlyAfterReplugging()
    {
        var ports = new List<string> { "/dev/ttyACM5" };
        var scans = 0;
        var probed = 0;
        using var svc = new AutoConnectService(() => true)
        {
            ProbeUdp = () => false,
            ProbeTcp = (_, _) => false,
            ResolvePort = p => p,
            ListPorts = () =>
            {
                Interlocked.Increment(ref scans);
                lock (ports)
                    return ports.ToList();
            },
            ProbeSerial = (_, _, _, _) =>
            {
                Interlocked.Increment(ref probed);
                return true;
            },
        };
        var found = new TaskCompletionSource<LinkSpec>();
        svc.Found += (spec, _) => found.TrySetResult(spec);
        svc.HoldOff(LinkSpec.Serial("/dev/ttyACM5", 115200));
        svc.Start();

        await WaitScans(() => scans, 3);
        Assert.IsFalse(found.Task.IsCompleted, "the vehicle the user disconnected is left alone");
        Assert.AreEqual(0, probed, "a held port is not even opened");

        lock (ports)
            ports.Clear(); // unplugged
        await WaitScans(() => scans, 2);
        Assert.IsFalse(svc.IsHeld(LinkSpec.Serial("/dev/ttyACM5")));
        lock (ports)
            ports.Add("/dev/ttyACM5"); // plugged in again

        var spec = await found.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(LinkSpec.Serial("/dev/ttyACM5", 115200), spec);
    }

    [TestMethod]
    [Timeout(40000, CooperativeCancellation = true)]
    public async Task ANetworkVehicleDisconnectedByTheUserIsReconnectedOnlyAfterItWentQuiet()
    {
        var sending = true;
        var scans = 0;
        using var svc = new AutoConnectService(() => true)
        {
            ProbeUdp = () => Volatile.Read(ref sending),
            ProbeTcp = (_, _) => false,
            ListPorts = () =>
            {
                Interlocked.Increment(ref scans);
                return [];
            },
            GoneAfter = TimeSpan.FromSeconds(2),
        };
        var found = new TaskCompletionSource<LinkSpec>();
        svc.Found += (spec, _) => found.TrySetResult(spec);
        var status = "";
        svc.StatusChanged += s => status = s;
        svc.HoldOff(LinkSpec.UdpListen(14550));
        svc.Start();

        await WaitScans(() => scans, 3);
        Assert.IsFalse(found.Task.IsCompleted, "still sending: the user's disconnect stands");
        StringAssert.Contains(status, "UDP 14550");

        Volatile.Write(ref sending, false); // vehicle powered off
        await WaitScans(() => scans, 3);
        Assert.IsFalse(svc.IsHeld(LinkSpec.UdpListen(14550)));
        Volatile.Write(ref sending, true); // and on again

        var spec = await found.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(LinkSpec.UdpListen(14550), spec);
    }

    /// <summary>
    /// A MatekH743 is two USB serial ports ("-if00", "-if02"), both answering MAVLink: after Disconnect on
    /// one, auto-connect used to connect straight back through the other.
    /// </summary>
    [TestMethod]
    [Timeout(40000, CooperativeCancellation = true)]
    public async Task DisconnectHoldsEveryUsbPortOfThatFlightController()
    {
        var probed = new System.Collections.Concurrent.ConcurrentBag<string>();
        var scans = 0;
        using var svc = new AutoConnectService(() => true)
        {
            ProbeUdp = () => false,
            ProbeTcp = (_, _) => false,
            ResolvePort = p => p,
            PortDevice = p => p is "/dev/ttyACM1" or "/dev/ttyACM2" ? "usb:/sys/devices/usb1/1-2" : p == "/dev/ttyACM7" ? "usb:/sys/devices/usb1/1-3" : null,
            ListPorts = () =>
            {
                Interlocked.Increment(ref scans);
                return ["/dev/ttyACM1", "/dev/ttyACM2"];
            },
            ProbeSerial = (port, _, _, _) =>
            {
                probed.Add(port);
                return true;
            },
        };
        var found = new TaskCompletionSource<LinkSpec>();
        svc.Found += (spec, _) => found.TrySetResult(spec);
        var status = "";
        svc.StatusChanged += s => status = s;

        svc.HoldOff(LinkSpec.Serial("/dev/ttyACM1", 115200));
        Assert.IsTrue(svc.IsHeld(LinkSpec.Serial("/dev/ttyACM2")), "the same flight controller's other port");
        Assert.IsFalse(svc.IsHeld(LinkSpec.Serial("/dev/ttyACM7")), "another flight controller");
        svc.Start();

        await WaitScans(() => scans, 3);
        Assert.IsFalse(found.Task.IsCompleted, "no connecting back through the second port");
        Assert.AreEqual(0, probed.Count, "neither port is opened");
        StringAssert.Contains(status, "ttyACM1 and its other USB ports until it is unplugged");

        // connecting by hand to either port is the user's choice: the whole board is released
        svc.Release(LinkSpec.Serial("/dev/ttyACM2", 115200));
        Assert.IsFalse(svc.IsHeld(LinkSpec.Serial("/dev/ttyACM1")));
        Assert.IsFalse(svc.IsHeld(LinkSpec.Serial("/dev/ttyACM2")));
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task AHeldPortDoesNotStopTheScanFindingAnotherVehicle()
    {
        using var svc = new AutoConnectService(() => true)
        {
            ProbeUdp = () => true,
            ProbeTcp = (_, _) => false,
            ResolvePort = p => p,
            ListPorts = () => ["/dev/ttyACM5", "/dev/ttyACM6"],
            ProbeSerial = (_, _, _, _) => true,
        };
        var found = new TaskCompletionSource<LinkSpec>();
        svc.Found += (spec, _) => found.TrySetResult(spec);
        svc.HoldOff(LinkSpec.UdpListen(14550));
        svc.HoldOff(LinkSpec.Serial("/dev/ttyACM5"));
        svc.Start();

        Assert.AreEqual(LinkSpec.Serial("/dev/ttyACM6", 115200), await found.Task.WaitAsync(TimeSpan.FromSeconds(15)));
    }

    private static async Task WaitScans(Func<int> scans, int count)
    {
        var target = scans() + count;
        var sw = Stopwatch.StartNew();
        while (scans() < target && sw.Elapsed < TimeSpan.FromSeconds(15))
            await Task.Delay(50);
        Assert.IsGreaterThanOrEqualTo(target, scans(), "the scan stalled");
    }

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ProbeHearsAVehicleOnARealSerialPort()
    {
        if (OperatingSystem.IsWindows())
            Assert.Inconclusive("needs socat");
        var dir = Path.Combine(TestEnvironment.DataRoot, "auto-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        Process? socat;
        try
        {
            socat = Process.Start(new ProcessStartInfo("socat") { ArgumentList = { $"pty,raw,echo=0,link={dir}/v", $"pty,raw,echo=0,link={dir}/g" } });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Inconclusive("socat is not installed");
            return;
        }

        try
        {
            var sw = Stopwatch.StartNew();
            while (!File.Exists($"{dir}/g") && sw.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(20);

            using var vehiclePort = new System.IO.Ports.SerialPort($"{dir}/v", 115200) { ReadTimeout = 300, WriteTimeout = 300 };
            vehiclePort.Open();
            await using var vehicle = new FakeVehicle(vehiclePort.BaseStream, 0);

            Assert.IsTrue(await Task.Run(() => SerialProbe.HasVehicleHeartbeat($"{dir}/g", 115200, TimeSpan.FromSeconds(3), CancellationToken.None)));
            Assert.IsTrue(vehicle.Received.IsEmpty, "probing must not send anything");
        }
        finally
        {
            socat!.Kill();
            socat.Dispose();
        }
    }
}
