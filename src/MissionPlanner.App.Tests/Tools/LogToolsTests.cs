using System.Xml.Linq;
using MissionPlanner.App.Core.ViewModels.Logs;
using MissionPlanner.App.Core.ViewModels.Tools;
using MissionPlanner.App.Tests.ConfigScreens;
using MissionPlanner.App.Tests.Logs;

namespace MissionPlanner.App.Tests.Tools;

/// <summary>TOOLS > Log tools: KML/GPX export, GPS correction extraction, binary to text.</summary>
[TestClass]
public class LogToolsTests
{
    private static string Temp(string ext, byte[] content)
    {
        var path = Path.Combine(TestEnvironment.DataRoot, "lt-" + Guid.NewGuid().ToString("N") + ext);
        File.WriteAllBytes(path, content);
        return path;
    }

    [TestMethod]
    public void TrackIsCleanedAndWrittenAsValidKmlAndGpx()
    {
        var track = TrackExport.Clean(
        [
            new LogTrackPoint(0, 0, 0, 0), // no fix yet
            new LogTrackPoint(1, -35.36, 149.16, 584),
            new LogTrackPoint(1.1, -35.36, 149.16, 584), // repeated
            new LogTrackPoint(2, -35.361, 149.161, 600.5),
        ]);
        Assert.HasCount(2, track);

        var kml = XDocument.Parse(TrackExport.Kml(track, "flight <1>"));
        XNamespace k = "http://www.opengis.net/kml/2.2";
        Assert.AreEqual("flight <1>", kml.Root!.Element(k + "Document")!.Element(k + "name")!.Value);
        var coords = kml.Descendants(k + "LineString").Single().Element(k + "coordinates")!.Value.Trim().Split('\n');
        Assert.AreEqual("149.1600000,-35.3600000,584.00", coords[0].Trim(), "KML is lon,lat,alt");

        var gpx = XDocument.Parse(TrackExport.Gpx(track, "f", new DateTime(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc)));
        XNamespace g = "http://www.topografix.com/GPX/1/1";
        var points = gpx.Descendants(g + "trkpt").ToList();
        Assert.HasCount(2, points);
        Assert.AreEqual("-35.3610000", points[1].Attribute("lat")!.Value);
        Assert.AreEqual("600.50", points[1].Element(g + "ele")!.Value);
        Assert.AreEqual("2026-11-21T10:00:02.000Z", points[1].Element(g + "time")!.Value);
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task ExportFromATelemetryLogWritesBothFiles()
    {
        var tlog = Temp(".tlog", LogTestFiles.Tlog(new DateTime(2026, 11, 21, 9, 0, 0, DateTimeKind.Utc)));
        var (services, dialogs, _) = ParamTestSupport.CreateServices();
        dialogs.OpenPath = tlog;
        var vm = new LogToolsViewModel(services);

        await vm.KmlCommand.ExecuteAsync(null);

        Assert.IsFalse(vm.StatusIsError, vm.Status);
        Assert.IsTrue(File.Exists(tlog + ".kml"), vm.Status);
        Assert.IsTrue(File.Exists(tlog + ".gpx"));
        StringAssert.Contains(File.ReadAllText(tlog + ".kml"), "149.1652000,-35.3632000");
        StringAssert.Contains(vm.Log, "10 points");
    }

    [TestMethod]
    public void GpsCorrectionsAreExtractedInOrder()
    {
        var w = new LogTestFiles.TlogWriter(DateTime.UtcNow);
        w.Write(0, MAVLink.MAVLINK_MSG_ID.HEARTBEAT, new MAVLink.mavlink_heartbeat_t(0, 2, 3, 0, 0, 3), 1, 1);
        var a = new byte[110];
        a[0] = 0xD3;
        a[1] = 1;
        var b = new byte[180];
        b[0] = 0xD3;
        b[1] = 2;
        w.Write(0.1, MAVLink.MAVLINK_MSG_ID.GPS_RTCM_DATA, new MAVLink.mavlink_gps_rtcm_data_t(0, 3, a), 255, 190);
        w.Write(0.2, MAVLink.MAVLINK_MSG_ID.ATTITUDE, new MAVLink.mavlink_attitude_t(1, 0, 0, 0, 0, 0, 0), 1, 1);
        w.Write(0.3, MAVLink.MAVLINK_MSG_ID.GPS_INJECT_DATA, new MAVLink.mavlink_gps_inject_data_t(1, 1, 5, b), 255, 190);
        var tlog = Temp(".tlog", w.ToArray());
        var output = tlog + ".dat";

        Assert.AreEqual(8L, GpsInjectExtractor.Extract(tlog, output));
        var got = File.ReadAllBytes(output);
        CollectionAssert.AreEqual(new byte[] { 0xD3, 1, 0, 0xD3, 2, 0, 0, 0 }, got, string.Join(" ", got.Select(x => x.ToString("X2"))));
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task BinaryDataFlashLogBecomesText()
    {
        var bin = Temp(".bin", LogTestFiles.BinaryLog());
        var (services, dialogs, _) = ParamTestSupport.CreateServices();
        dialogs.OpenPath = bin;
        var vm = new LogToolsViewModel(services);

        await vm.BinToLogCommand.ExecuteAsync(null);

        Assert.IsFalse(vm.StatusIsError, vm.Status);
        var text = File.ReadAllText(Path.ChangeExtension(bin, ".log"));
        StringAssert.Contains(text, "ATT");
    }
}
