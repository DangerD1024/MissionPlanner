using System.Net;
using System.Net.Sockets;
using System.Text;
using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.Outputs;
using MissionPlanner.App.Core.ViewModels.Tools;
using MissionPlanner.App.Tests.FlightData;

namespace MissionPlanner.App.Tests.Tools;

/// <summary>TOOLS > Outputs: the formats, the TCP server, and outputs running against a scripted vehicle.</summary>
[TestClass]
public class OutputsTests
{
    private static readonly VehicleFix Fix = new(48.1173, 11.516667, 545.4, 10, 84.4, 1.5, -2.25, 90, 0.5, 3, 8, 0.9);

    [TestMethod]
    public void NmeaChecksumAndCoordinatesMatchTheStandardExample()
    {
        // the textbook GGA: $GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,*47
        Assert.AreEqual("47", NmeaOutput.Checksum("GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,"));
        Assert.AreEqual("4807.03800", NmeaOutput.Coordinate(48.1173, 2));
        Assert.AreEqual("01131.00002", NmeaOutput.Coordinate(11.516667, 3));
        Assert.AreEqual("3400.00000", NmeaOutput.Coordinate(-33.99999999, 2), "59.99999… minutes round up to the next degree");
        Assert.AreEqual("0000.00000", NmeaOutput.Coordinate(0, 2));
    }

    [TestMethod]
    public void NmeaSentencesAreValidAndCarryTheFix()
    {
        var utc = new DateTime(2026, 11, 21, 12, 35, 19, 500, DateTimeKind.Utc);
        var lines = NmeaOutput.Sentences(Fix, (48.1, 11.5, 520), utc, 0);
        foreach (var line in lines)
        {
            StringAssert.EndsWith(line, "\r\n");
            var body = line[1..line.IndexOf('*')];
            Assert.AreEqual(NmeaOutput.Checksum(body), line[(line.IndexOf('*') + 1)..].TrimEnd(), line);
        }

        var gga = lines.Single(l => l.StartsWith("$GPGGA"));
        StringAssert.StartsWith(gga, "$GPGGA,123519.50,4807.03800,N,01131.00002,E,1,08,0.9,545.4,M,");
        var rmc = lines.Single(l => l.StartsWith("$GPRMC")).Split(',');
        Assert.AreEqual("A", rmc[2]);
        Assert.AreEqual("19.44", rmc[7], "knots");
        Assert.AreEqual("84.4", rmc[8]);
        Assert.AreEqual("211126", rmc[9], "UTC date");
        StringAssert.StartsWith(lines.Single(l => l.StartsWith("$GPVTG")), "$GPVTG,84.4,T,,M,19.44,N,36.00,K,A");
        Assert.IsTrue(lines.Any(l => l.StartsWith("$GPHOM")), "home every 20th round, starting with the first");
        Assert.IsFalse(NmeaOutput.Sentences(Fix, (48.1, 11.5, 520), utc, 1).Any(l => l.StartsWith("$GPHOM")));
        StringAssert.StartsWith(lines.Last(), "$GPRPY,1.50000,-2.25000,90.00000,");

        var noFix = NmeaOutput.Sentences(Fix with { FixType = 1 }, null, utc, 0);
        Assert.AreEqual("0", noFix.Single(l => l.StartsWith("$GPGGA")).Split(',')[6], "no fix quality without a 3D fix");
        Assert.AreEqual("V", noFix.Single(l => l.StartsWith("$GPRMC")).Split(',')[2]);
    }

    [TestMethod]
    public void MicroDroneLinesCarryTheirChecksum()
    {
        var lines = MicroDroneOutput.Lines(Fix, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 30);
        Assert.HasCount(7, lines);
        foreach (var line in lines)
        {
            var text = line.TrimEnd();
            var cut = text.LastIndexOf(',') + 1;
            Assert.AreEqual(MicroDroneOutput.Checksum(text[..cut]).ToString(), text[cut..], line);
        }

        StringAssert.StartsWith(lines[1], "#4,3,", "the round count / 10");
        var (x, y, z) = MicroDroneOutput.Ecef(0, 0, 0);
        Assert.AreEqual(6378137, x, 1);
        Assert.AreEqual(0, y, 1e-6);
        Assert.AreEqual(0, z, 1e-6);
    }

    [TestMethod]
    public void CursorOnTargetEventsNameEveryVehicleAndExpireWhenStopped()
    {
        var cot = new CotOutput { Callsign = "UAV", IntervalSeconds = 1 };
        var now = DateTime.UtcNow;
        var vehicle = new MissionPlanner.Controls.CotVehicle
        {
            SysId = 3, CompId = 1, Autopilot = MAVLink.MAV_AUTOPILOT.ARDUPILOTMEGA, Lat = -35.36, Lng = 149.16,
            AltMslM = 600, LastPacketUtc = now,
        };
        var xml = cot.Round([vehicle], now).Single();
        StringAssert.Contains(xml, "uid=\"MP-sysid3\"");
        StringAssert.Contains(xml, "lat=\"-35.3600000\"");
        StringAssert.Contains(xml, "callsign=\"UAV-3\"");
        StringAssert.EndsWith(xml, "\n");
        var ended = cot.Expire(now).Single();
        StringAssert.Contains(ended, "uid=\"MP-sysid3\"");
    }

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task TcpServerSendsToEveryClientAndReadsWhatTheySend()
    {
        using var server = new TcpServerComms(0);
        server.Open();
        using var a = new TcpClient();
        using var b = new TcpClient();
        await a.ConnectAsync(IPAddress.Loopback, server.LocalPort);
        await b.ConnectAsync(IPAddress.Loopback, server.LocalPort);
        await Until(() => server.ClientCount == 2);

        server.Write("hello\n");
        Assert.AreEqual("hello\n", await ReadLine(a.GetStream()));
        Assert.AreEqual("hello\n", await ReadLine(b.GetStream()));

        await a.GetStream().WriteAsync("from a"u8.ToArray());
        await Until(() => server.BytesToRead == 6);
        Assert.AreEqual("from a", server.ReadExisting());
        Assert.AreEqual(0, server.BytesToRead);

        a.Close();
        server.Write("bye\n");
        await Until(() => server.ClientCount == 1);
        server.Close();
        Assert.IsFalse(server.IsOpen);
    }

    [TestMethod]
    [Timeout(90000, CooperativeCancellation = true)]
    public async Task MirrorAndNmeaOutputsAgainstAVehicle()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync();
        var services = FakeUi.Services(out _);
        var connecting = services.Session.ConnectAsync(LinkSpec.Tcp("127.0.0.1", port), getParams: false);
        using var vehicleSide = await accept;
        listener.Stop();
        await using var vehicle = new ExtrasFakeVehicle(vehicleSide.GetStream());
        await connecting;
        try
        {
            // MAVLink mirror as a TCP server: the vehicle's packets come out, and commands go back in
            var mirrorPort = FreePort();
            var mirror = services.Outputs.Add(OutputKind.MavlinkMirror);
            mirror.Config.Transport = OutputTransportKind.TcpServer;
            mirror.Config.Port = mirrorPort;
            mirror.Config.AllowWrite = true;
            mirror.Start();
            Assert.IsTrue(mirror.IsRunning, mirror.Status);

            using var gcs = new TcpClient();
            await gcs.ConnectAsync(IPAddress.Loopback, mirrorPort);
            var parse = new MAVLink.MavlinkParse();
            var heartbeat = await Task.Run(() =>
            {
                var stream = gcs.GetStream();
                for (var i = 0; i < 50; i++)
                {
                    var packet = parse.ReadPacket(stream);
                    if (packet?.msgid == (uint)MAVLink.MAVLINK_MSG_ID.HEARTBEAT)
                        return packet;
                }

                return null;
            });
            Assert.IsNotNull(heartbeat, "the vehicle's heartbeat reaches the mirror");

            var cmd = parse.GenerateMAVLinkPacket20(MAVLink.MAVLINK_MSG_ID.COMMAND_LONG, new MAVLink.mavlink_command_long_t
            {
                target_system = 1, target_component = 1, command = (ushort)MAVLink.MAV_CMD.DO_SET_SERVO, param1 = 7, param2 = 1234,
            }, false, 250, 190);
            await gcs.GetStream().WriteAsync(cmd);
            await Until(() => vehicle.Commands.Any(c => c.command == (ushort)MAVLink.MAV_CMD.DO_SET_SERVO && c.param2 == 1234));
            Assert.IsTrue(vehicle.Commands.Any(c => c.param2 == 1234), "the mirror's command reached the vehicle");

            await mirror.StopAsync();
            Assert.IsEmpty(services.Session.Mav.Mirrors.Where(m => m.MirrorStream != null && m.MirrorStream.IsOpen));

            // NMEA over UDP once the vehicle has a position
            using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var nmea = services.Outputs.Add(OutputKind.Nmea);
            nmea.Config.Transport = OutputTransportKind.UdpSend;
            nmea.Config.Address = "127.0.0.1";
            nmea.Config.Port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
            nmea.Config.RateHz = 10;
            services.Session.Cs.lat = -35.3632;
            services.Session.Cs.lng = 149.1652;
            nmea.Start();
            Assert.IsTrue(nmea.IsRunning, nmea.Status);
            var got = await receiver.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(10));
            StringAssert.StartsWith(Encoding.ASCII.GetString(got.Buffer), "$GPGGA,");
            StringAssert.Contains(Encoding.ASCII.GetString(got.Buffer), "3521.79200,S,14909.91200,E");
            await nmea.StopAsync();
            Assert.IsGreaterThan(0L, nmea.BytesSent);

            // saved, and loaded again by a fresh manager
            var saved = MissionPlanner.Utilities.Settings.Instance["next_outputs"];
            StringAssert.Contains(saved, "\"Kind\":1");
            await services.Outputs.RemoveAsync(mirror);
            await services.Outputs.RemoveAsync(nmea);
        }
        finally
        {
            await services.Session.DisconnectAsync();
        }
    }

    [TestMethod]
    public void OutputsPageEditsTheSelectedOutput()
    {
        var services = FakeUi.Services(out _);
        foreach (var item in services.Outputs.Items.ToList())
            services.Outputs.Items.Remove(item);
        var vm = new OutputsViewModel(services);
        vm.OnShown();
        vm.NewKind = OutputsViewModel.Kinds.Single(k => k.Value == OutputKind.CursorOnTarget);
        vm.AddCommand.Execute(null);
        Assert.IsNotNull(vm.Selected);
        Assert.IsTrue(vm.IsCot);
        Assert.AreEqual("239.2.3.1", vm.Address, "TAK's SA multicast group by default");
        Assert.AreEqual(6969m, vm.Port);

        vm.Transport = OutputsViewModel.Transports.Single(t => t.Value == OutputTransportKind.Serial);
        Assert.IsTrue(vm.IsSerial);
        Assert.AreEqual(57600, vm.Baud, "serial gets a baud rate");
        vm.Transport = OutputsViewModel.Transports.Single(t => t.Value == OutputTransportKind.TcpServer);
        Assert.AreEqual(6969m, vm.Port, "back to the kind's port");
        vm.CotCallsign = "HAWK";
        Assert.AreEqual("HAWK", vm.Selected!.Config.CotCallsign);
        Assert.AreEqual("TCP server on port 6969", vm.Selected.Summary);
        services.Outputs.Items.Remove(vm.Selected);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static async Task<string> ReadLine(NetworkStream s)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (!sb.ToString().EndsWith('\n') && await s.ReadAsync(one).AsTask().WaitAsync(TimeSpan.FromSeconds(5)) == 1)
            sb.Append((char)one[0]);
        return sb.ToString();
    }

    private static async Task Until(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < until)
            await Task.Delay(20);
    }
}
