using System.Net;
using System.Net.Sockets;
using System.Text;
using MissionPlanner.App.Core.ViewModels.Tools;
using MissionPlanner.App.Tests.FlightData;
using MissionPlanner.ArduPilot;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Tests.Tools;

/// <summary>TOOLS > Follow Me and Moving Base: the NMEA reading, the update rate and what each sends.</summary>
[TestClass]
public class NmeaGpsTests
{
    // the textbook GGA: 48°07.038' N, 11°31.000' E, 545.4 m, 8 satellites, hdop 0.9
    private const string Gga = "$GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,*47";

    private static string WithChecksum(string body) =>
        "$" + body + "*" + NmeaGgaParser.Checksum("$" + body + "*").ToString("X2");

    [TestMethod]
    public void GgaIsReadIntoDegrees()
    {
        Assert.AreEqual(NmeaGgaParser.Outcome.Fix, NmeaGgaParser.TryParse(Gga, out var fix));
        Assert.IsNotNull(fix);
        Assert.AreEqual(48.1173, fix.Lat, 1e-9);
        Assert.AreEqual(11.516666667, fix.Lng, 1e-8);
        Assert.AreEqual(545.4, fix.Alt, 1e-9);
        Assert.AreEqual(1, fix.Quality);
        Assert.AreEqual("08", fix.Satellites);
        Assert.AreEqual("0.9", fix.Hdop);
    }

    [TestMethod]
    public void SouthAndWestAreNegativeAndGnggaIsAccepted()
    {
        var line = WithChecksum("GNGGA,010203.00,3521.80000,S,14909.60000,W,4,14,0.6,584.1,M,0,M,,");
        Assert.AreEqual(NmeaGgaParser.Outcome.Fix, NmeaGgaParser.TryParse(line, out var fix));
        Assert.AreEqual(-35.363333333, fix!.Lat, 1e-8);
        Assert.AreEqual(-149.16, fix.Lng, 1e-8);
        Assert.AreEqual(4, fix.Quality);
    }

    [TestMethod]
    public void BadChecksumNoFixAndOtherSentencesGiveNoPosition()
    {
        Assert.AreEqual(NmeaGgaParser.Outcome.BadChecksum, NmeaGgaParser.TryParse(Gga[..^2] + "48", out _));
        Assert.AreEqual(NmeaGgaParser.Outcome.BadChecksum, NmeaGgaParser.TryParse(Gga[..Gga.IndexOf('*')], out _), "no checksum at all");
        Assert.AreEqual(NmeaGgaParser.Outcome.NoFix,
            NmeaGgaParser.TryParse(WithChecksum("GPGGA,123519,4807.038,N,01131.000,E,0,00,99.9,545.4,M,46.9,M,,"), out _));
        Assert.AreEqual(NmeaGgaParser.Outcome.NotGga, NmeaGgaParser.TryParse("$GPRMC,123519,A,4807.038,N,01131.000,E,022.4,084.4,230394,003.1,W*6A", out _));
        Assert.AreEqual(NmeaGgaParser.Outcome.NotGga, NmeaGgaParser.TryParse("", out _));
        Assert.AreEqual(NmeaGgaParser.Outcome.Malformed, NmeaGgaParser.TryParse(WithChecksum("GPGGA,1,2"), out _));
        Assert.AreEqual(NmeaGgaParser.Outcome.Fix, NmeaGgaParser.TryParse("  " + Gga + "\r\n", out _), "whitespace and line ends are trimmed");
    }

    [TestMethod]
    public void RateGateLetsOneThroughPerPeriod()
    {
        var gate = new RateGate(0.5);
        var t = new DateTime(2026, 1, 1, 12, 0, 0);
        Assert.IsTrue(gate.Due(t));
        Assert.IsFalse(gate.Due(t.AddSeconds(1.9)));
        Assert.IsTrue(gate.Due(t.AddSeconds(2)));
        gate.Hz = 2; // a new rate applies after the send already scheduled
        Assert.IsFalse(gate.Due(t.AddSeconds(3.9)));
        Assert.IsTrue(gate.Due(t.AddSeconds(4)));
        Assert.IsFalse(gate.Due(t.AddSeconds(4.4)));
        Assert.IsTrue(gate.Due(t.AddSeconds(4.5)));
    }

    [TestMethod]
    public void ReaderSendsTheLatestFixAtTheRate()
    {
        var updates = new List<GgaFix>();
        var reader = new NmeaGpsReader(new NoSource(), 1, null) { OnUpdate = updates.Add };
        var t = new DateTime(2026, 1, 1, 12, 0, 0);

        Assert.IsFalse(reader.Process("$GPGSV,3,1,11*00", t), "nothing before the first fix");
        Assert.IsTrue(reader.Process(Gga, t));
        Assert.IsFalse(reader.Process(Gga, t.AddSeconds(0.5)), "too soon");
        var moved = WithChecksum("GPGGA,123520,4808.000,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,");
        Assert.IsFalse(reader.Process(moved, t.AddSeconds(0.8)));
        Assert.IsTrue(reader.Process("$GPGSA,A,3*00", t.AddSeconds(1.1)), "a later line sends the last fix");
        Assert.AreEqual(2, updates.Count);
        Assert.AreEqual(48 + 8 / 60.0, updates[1].Lat, 1e-9);
        Assert.AreEqual(0, reader.BadLines, "other sentences are skipped, not bad");
        reader.Process(Gga[..^2] + "00", t.AddSeconds(5));
        Assert.AreEqual(1, reader.BadLines);
        Assert.AreEqual(48 + 8 / 60.0, reader.LastFix!.Lat, 1e-9, "a bad line keeps the last good fix");
    }

    [TestMethod]
    public void TcpHostSourceReadsLinesFromAClient()
    {
        using var source = new TcpHostLineSource(0);
        source.Open();
        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, source.LocalPort);
        var bytes = Encoding.ASCII.GetBytes("$GPGGA,partial");
        client.GetStream().Write(bytes);
        Assert.IsNull(source.ReadLine(300), "no line end yet");
        client.GetStream().Write(Encoding.ASCII.GetBytes("\r\n" + Gga + "\r\n"));
        Assert.AreEqual("$GPGGA,partial", source.ReadLine(2000));
        Assert.AreEqual(Gga, source.ReadLine(2000));
    }

    [TestMethod]
    public void FollowMeTargetIsAGuidedWaypointAboveHome()
    {
        NmeaGgaParser.TryParse(Gga, out var fix);
        var wp = FollowMeLogic.Target(fix!, 25);
        Assert.AreEqual((ushort)MAVLink.MAV_CMD.WAYPOINT, wp.id);
        Assert.AreEqual(fix!.Lat, wp.lat);
        Assert.AreEqual(fix.Lng, wp.lng);
        Assert.AreEqual(25f, wp.alt);
        Assert.AreEqual((byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT, wp.frame);
        Assert.AreEqual(10, FollowMeLogic.DefaultAltitude(Firmwares.ArduCopter2));
        Assert.AreEqual(100, FollowMeLogic.DefaultAltitude(Firmwares.ArduPlane));
    }

    [TestMethod]
    public void FollowMeShowsTheTargetAndSendsNothingWithoutAVehicle()
    {
        var vm = new FollowMeViewModel(FakeUi.Services(out _));
        NmeaGgaParser.TryParse(Gga, out var fix);
        vm.SendTarget(fix!, 30);
        StringAssert.StartsWith(vm.Location, "48.1173000 11.5166667");
        StringAssert.Contains(vm.Location, "Sats 08 hdop 0.9");
        Assert.AreEqual(0, vm.TargetsSent, "not connected: no guided target");
    }

    [TestMethod]
    public void FollowMeRefusesToStartWithoutAPort()
    {
        var vm = new FollowMeViewModel(FakeUi.Services(out _)) { Port = "" };
        vm.StartStopCommand.Execute(null);
        Assert.IsFalse(vm.IsRunning);
        Assert.IsTrue(vm.StatusIsError);

        vm.Port = "NO_SUCH_PORT_" + Guid.NewGuid().ToString("N");
        vm.Altitude = 20;
        vm.StartStopCommand.Execute(null);
        Assert.IsFalse(vm.IsRunning, "a port that does not exist does not start");
        Assert.IsTrue(vm.StatusIsError);
    }

    [TestMethod]
    public void MovingBaseAltitudeAndRallyPoint()
    {
        NmeaGgaParser.TryParse(Gga, out var fix);
        Assert.AreEqual(545.4, MovingBaseLogic.BaseAltitude(fix!, false, 500), 1e-9);
        Assert.AreEqual(45.4, MovingBaseLogic.BaseAltitude(fix!, true, 500), 1e-9);
        var rally = MovingBaseLogic.RallyPoint(fix!, 100);
        Assert.AreEqual(fix!.Lat, rally.Lat);
        Assert.AreEqual(645.4, rally.Alt, 1e-9);
    }

    [TestMethod]
    public void MovingBasePutsTheGroundStationOnTheMap()
    {
        var services = FakeUi.Services(out _);
        var vm = new MovingBaseViewModel(services);
        var changes = 0;
        services.GcsLocation.Changed += (_, _) => changes++;
        NmeaGgaParser.TryParse(Gga, out var fix);

        vm.OnBaseUpdate(fix!, relative: false, rally: false);

        var p = services.GcsLocation.Current;
        Assert.IsNotNull(p);
        Assert.AreEqual(48.1173, p.Lat, 1e-9);
        Assert.AreEqual(545.4, p.Alt, 1e-9);
        Assert.AreEqual(1, changes);
        Assert.AreEqual(48.1173, services.Session.Cs.Base.Lat, 1e-9, "MAV.cs.Base as Mission Planner sets it");
        StringAssert.StartsWith(vm.Location, "48.1173000 11.5166667");

        services.GcsLocation.Clear();
        Assert.IsNull(services.GcsLocation.Current);
        Assert.AreEqual(2, changes);
        services.GcsLocation.Clear();
        Assert.AreEqual(2, changes, "clearing twice raises once");
    }

    [TestMethod]
    public void MovingBaseReadsATcpGps()
    {
        var services = FakeUi.Services(out _);
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        try
        {
            var port = ((IPEndPoint)server.LocalEndpoint).Port;
            var vm = new MovingBaseViewModel(services);
            vm.OnShown();
            vm.Source = vm.Sources.First(s => s.Kind == NmeaSourceKind.TcpClient);
            vm.Address = "127.0.0.1:" + port;
            vm.RateHz = 2;
            vm.StartStopCommand.Execute(null);
            try
            {
                Assert.IsTrue(vm.IsRunning, vm.Status);
                using var gps = server.AcceptTcpClient();
                var b = Encoding.ASCII.GetBytes(Gga + "\r\n");
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (services.GcsLocation.Current == null && DateTime.UtcNow < deadline)
                {
                    gps.GetStream().Write(b);
                    Thread.Sleep(100);
                }

                Assert.IsNotNull(services.GcsLocation.Current, "the GPS position reached the map service");
                Assert.AreEqual(48.1173, services.GcsLocation.Current.Lat, 1e-9);
            }
            finally
            {
                vm.StartStopCommand.Execute(null);
            }

            Assert.IsFalse(vm.IsRunning);
            Assert.IsNull(services.GcsLocation.Current, "stopping removes the marker");
        }
        finally
        {
            server.Stop();
        }
    }

    private sealed class NoSource : INmeaLineSource
    {
        public void Open()
        {
        }

        public string? ReadLine(int timeoutMs) => null;

        public void Dispose()
        {
        }
    }
}
