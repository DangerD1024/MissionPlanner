using System.Buffers.Binary;
using GMap.NET;
using MissionPlanner.App.Core.Mission;
using MissionPlanner.App.Core.ViewModels.FlightPlan;
using MissionPlanner.App.Tests.FlightData;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Tests.FlightPlan;

[TestClass]
public class PlanFilesTests
{
    private static string Temp(string ext) => Path.Combine(TestEnvironment.DataRoot, "pf-" + Guid.NewGuid().ToString("N") + ext);

    [TestMethod]
    public void FenceAndRallyFilesRoundTrip()
    {
        var poly = new List<PointLatLng> { new(-35, 149), new(-35, 149.01), new(-35.01, 149.01) };
        var text = PlanFiles.WriteFence(new PointLatLng(-35.005, 149.005), poly);
        StringAssert.StartsWith(text, "#saved by");
        var (ret, back) = PlanFiles.ReadFence(text.Split('\n'));
        Assert.AreEqual(new PointLatLng(-35.005, 149.005), ret);
        CollectionAssert.AreEqual(poly, back, "the closing vertex is dropped again");

        var rally = PlanFiles.WriteRally([(-35.1, 149.1, 50), (-35.2, 149.2, 60)]);
        StringAssert.Contains(rally, "RALLY\t-35.1000000\t149.1000000\t50\t40\t0\t0");
        CollectionAssert.AreEqual(new[] { (-35.1, 149.1, 50.0), (-35.2, 149.2, 60.0) }, PlanFiles.ReadRally(rally.Split('\n')).ToArray());
    }

    [TestMethod]
    public void KmlPointsLinesAndPolygons()
    {
        const string kml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <kml xmlns="http://www.opengis.net/kml/2.2"><Document>
              <Placemark><Point><coordinates>149.1,-35.1,30</coordinates></Point></Placemark>
              <Placemark><LineString><coordinates>149.2,-35.2 149.3,-35.3,40</coordinates></LineString></Placemark>
              <Placemark><Polygon><outerBoundaryIs><LinearRing><coordinates>
                 149,-35 149.01,-35 149.01,-35.01 149,-35
              </coordinates></LinearRing></outerBoundaryIs></Polygon></Placemark>
            </Document></kml>
            """;
        var shapes = PlanFiles.ReadKml(kml);
        Assert.HasCount(3, shapes);
        Assert.AreEqual(("Point", -35.1, 149.1, (double?)30), (shapes[0].Kind, shapes[0].Points[0].Lat, shapes[0].Points[0].Lng, shapes[0].Points[0].Alt));
        Assert.AreEqual("LineString", shapes[1].Kind);
        Assert.IsNull(shapes[1].Points[0].Alt);
        Assert.IsTrue(shapes[2].IsPolygon);
        Assert.HasCount(3, shapes[2].Points, "closed ring without its repeated first point");
    }

    /// <summary>A minimal shapefile: one point record and one single-part polygon record.</summary>
    private static byte[] Shapefile()
    {
        var ms = new MemoryStream();
        var header = new byte[100];
        BinaryPrimitives.WriteInt32BigEndian(header, 9994);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), 1000);
        ms.Write(header);

        void Record(byte[] content, int n)
        {
            var h = new byte[8];
            BinaryPrimitives.WriteInt32BigEndian(h, n);
            BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(4), content.Length / 2);
            ms.Write(h);
            ms.Write(content);
        }

        var point = new byte[20];
        BinaryPrimitives.WriteInt32LittleEndian(point, 1);
        BitConverter.TryWriteBytes(point.AsSpan(4), 149.5);
        BitConverter.TryWriteBytes(point.AsSpan(12), -35.5);
        Record(point, 1);

        (double X, double Y)[] ring = [(149, -35), (149.01, -35), (149.01, -35.01), (149, -35)];
        var poly = new byte[44 + 4 + ring.Length * 16];
        BinaryPrimitives.WriteInt32LittleEndian(poly, 5);
        BinaryPrimitives.WriteInt32LittleEndian(poly.AsSpan(36), 1);
        BinaryPrimitives.WriteInt32LittleEndian(poly.AsSpan(40), ring.Length);
        BinaryPrimitives.WriteInt32LittleEndian(poly.AsSpan(44), 0);
        for (var i = 0; i < ring.Length; i++)
        {
            BitConverter.TryWriteBytes(poly.AsSpan(48 + i * 16), ring[i].X);
            BitConverter.TryWriteBytes(poly.AsSpan(56 + i * 16), ring[i].Y);
        }

        Record(poly, 2);
        return ms.ToArray();
    }

    [TestMethod]
    public void ShapefilePointsAndPolygons()
    {
        var shapes = PlanFiles.ReadShapefile(Shapefile());
        Assert.HasCount(2, shapes);
        Assert.AreEqual((-35.5, 149.5), (shapes[0].Points[0].Lat, shapes[0].Points[0].Lng));
        Assert.IsTrue(shapes[1].IsPolygon);
        Assert.HasCount(3, shapes[1].Points);
        Assert.ThrowsExactly<InvalidDataException>(() => PlanFiles.ReadShapefile(new byte[120]));
        Assert.IsNotNull(PlanFiles.ShapefileProjectionProblem("PROJCS[\"GDA94 / MGA zone 55\"]"));
        Assert.IsNull(PlanFiles.ShapefileProjectionProblem("GEOGCS[\"GCS_WGS_1984\"]"));
    }

    [TestMethod]
    public void OffsetGrowsASquareEvenly()
    {
        // ~111 m square
        var square = new List<PointLatLng> { new(0, 0), new(0, 0.001), new(-0.001, 0.001), new(-0.001, 0) };
        var grown = PlanFiles.Offset(square, 10);
        Assert.HasCount(4, grown);
        var a = new PointLatLngAlt(grown[0].Lat, grown[0].Lng);
        var b = new PointLatLngAlt(grown[1].Lat, grown[1].Lng);
        Assert.AreEqual(111.3 + 20, a.GetDistance(b), 1, "each side 10 m out");
        var shrunk = PlanFiles.Offset(square, -10);
        Assert.AreEqual(111.3 - 20, new PointLatLngAlt(shrunk[0].Lat, shrunk[0].Lng).GetDistance(new PointLatLngAlt(shrunk[1].Lat, shrunk[1].Lng)), 1);
    }

    [TestMethod]
    public async Task PlanJsonSavesAndLoads_AndAppends()
    {
        var services = FakeUi.Services(out _);
        var vm = new FlightPlanViewModel(services);
        vm.Store.Mission.Clear();
        vm.Store.SetHome(-35.36, 149.16, 584);
        vm.Store.Mission.Add(vm.Store.NewWaypoint(-35.37, 149.17));
        vm.Store.Mission.Add(new MissionItem { Command = MAVLink.MAV_CMD.DO_CHANGE_SPEED, P2 = 7 });
        var path = Temp(".plan");
        var home = new Locationwp { id = (ushort)MAVLink.MAV_CMD.WAYPOINT, lat = -35.36, lng = 149.16, alt = 584 };
        PlanFiles.WritePlan(path, home, vm.Store.Mission.Select(i => i.ToLocationwp()));

        vm.Store.Mission.Clear();
        Assert.IsTrue(await vm.LoadAnyFileFromAsync(path, append: false));
        Assert.HasCount(2, vm.Store.Mission);
        Assert.AreEqual(-35.37, vm.Store.Mission[0].Lat, 1e-7);
        Assert.AreEqual(7f, vm.Store.Mission[1].P2);

        Assert.IsTrue(await vm.LoadAnyFileFromAsync(path, append: true));
        Assert.HasCount(4, vm.Store.Mission, "appended");

        var shp = Temp(".shp");
        File.WriteAllBytes(shp, Shapefile());
        Assert.IsTrue(await vm.LoadAnyFileFromAsync(shp, append: false));
        Assert.HasCount(1, vm.Store.Mission, "the point became a waypoint");
        Assert.HasCount(3, vm.Polygon, "the polygon became the drawn polygon");
        vm.Store.Mission.Clear();
        vm.Polygon.Clear();
    }
}
