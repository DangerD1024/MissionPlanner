using System.Diagnostics;
using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.Session;

namespace MissionPlanner.App.Tests;

[TestClass]
public class AutoConnectTests
{
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
