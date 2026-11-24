using System.Globalization;
using DotSpatial.Projections;
using GMap.NET;
using GMap.NET.Projections;
using MissionPlanner.App.Core.Maps;
using MissionPlanner.App.Core.ViewModels.Tools;
using MissionPlanner.App.Tests.FlightData;
using MissionPlanner.App.Tests.Logs;
using MissionPlanner.Maps;
using MissionPlanner.Utilities;
using SkiaSharp;

namespace MissionPlanner.App.Tests.Tools;

[TestClass]
public class MapToolsTests
{
    private const string Utm55S =
        "PROJCS[\"WGS_1984_UTM_Zone_55S\",GEOGCS[\"GCS_WGS_1984\",DATUM[\"D_WGS_1984\",SPHEROID[\"WGS_1984\",6378137.0,298.257223563]]," +
        "PRIMEM[\"Greenwich\",0.0],UNIT[\"Degree\",0.0174532925199433]],PROJECTION[\"Transverse_Mercator\"],PARAMETER[\"False_Easting\",500000.0]," +
        "PARAMETER[\"False_Northing\",10000000.0],PARAMETER[\"Central_Meridian\",147.0],PARAMETER[\"Scale_Factor\",0.9996]," +
        "PARAMETER[\"Latitude_Of_Origin\",0.0],UNIT[\"Meter\",1.0]]";

    [ClassInitialize]
    public static void Init(TestContext _) =>
        MapTiles.Initialize(Path.Combine(TestEnvironment.DataRoot, "gmapcache") + Path.DirectorySeparatorChar);

    private static string NewFolder(string name) =>
        Directory.CreateDirectory(Path.Combine(TestEnvironment.DataRoot, name + "-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    // ---- Shp to Poly

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ShapefileFeatures_AreWrittenAsPolyFiles()
    {
        var dir = NewFolder("shp");
        var shp = Path.Combine(dir, "fields.shp");
        File.WriteAllBytes(shp, MapToolFixtures.Shapefile(
            [(149.0, -35.0), (149.1, -35.0), (149.1, -35.1), (149.0, -35.0)],
            [(150.5, -34.5), (150.6, -34.5), (150.5, -34.5)]));

        var vm = new ShpToPolyViewModel(FakeUi.Services(out _));
        await vm.ConvertFileAsync(shp);

        Assert.IsFalse(vm.StatusIsError, vm.Status);
        var poly1 = File.ReadAllText(Path.Combine(dir, "poly-1.poly"));
        Assert.AreEqual("#Shap to Poly - Mission Planner\r\n-35\t149\r\n-35\t149.1\r\n-35.1\t149.1\r\n-35\t149\r\n", poly1);
        var poly2 = File.ReadAllLines(Path.Combine(dir, "poly-2.poly"));
        CollectionAssert.AreEqual(new[] { "#Shap to Poly - Mission Planner", "-34.5\t150.5", "-34.5\t150.6", "-34.5\t150.5" }, poly2);
        StringAssert.Contains(vm.Status, "2 polygon files");

        // the poly format PLAN reads
        var points = MissionPlanner.App.Core.ViewModels.FlightPlan.MissionEditing.ReadPolygonFile(Path.Combine(dir, "poly-1.poly"));
        Assert.HasCount(3, points);
    }

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public void ProjectedShapefile_IsReprojectedWithItsPrj()
    {
        var dir = NewFolder("shp-utm");
        // a field near Canberra, given in lat/lng and stored in UTM 55S
        (double Lat, double Lng)[] ll = [(-35.3632, 149.1652), (-35.3640, 149.1660), (-35.3650, 149.1650)];
        var xy = ll.SelectMany(p => new[] { p.Lng, p.Lat }).ToArray();
        Reproject.ReprojectPoints(xy, new double[ll.Length], KnownCoordinateSystems.Geographic.World.WGS1984,
            ProjectionInfo.FromEsriString(Utm55S), 0, ll.Length);
        Assert.IsTrue(xy[0] > 600_000 && xy[0] < 800_000, "easting " + xy[0]);

        var shp = Path.Combine(dir, "utm.shp");
        File.WriteAllBytes(shp, MapToolFixtures.Shapefile(Enumerable.Range(0, ll.Length).Select(i => (xy[i * 2], xy[i * 2 + 1])).ToArray()));
        File.WriteAllText(Path.Combine(dir, "utm.prj"), Utm55S);

        var written = ShapefilePolygons.Convert(shp);

        Assert.HasCount(1, written);
        var lines = File.ReadAllLines(written[0]).Skip(1).Select(l => l.Split('\t').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray()).ToList();
        Assert.HasCount(3, lines);
        for (var i = 0; i < ll.Length; i++)
        {
            Assert.AreEqual(ll[i].Lat, lines[i][0], 1e-6);
            Assert.AreEqual(ll[i].Lng, lines[i][1], 1e-6);
        }
    }

    // ---- Inject GE / Clear Custom Maps

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task TileFolder_IsInjectedAsCustomMap_AndCleared()
    {
        var src = NewFolder("ge");
        var tile = Png.Encode(Enumerable.Repeat(0xFF0000FFu, 256 * 256).ToArray(), 256, 256);
        Directory.CreateDirectory(Path.Combine(src, "Z15", "100"));
        File.WriteAllBytes(Path.Combine(src, "Z15", "100", "200.png"), tile);
        File.WriteAllText(Path.Combine(src, "Z15", "100", "201.jpg"), "not an image");
        File.WriteAllBytes(Path.Combine(src, "loose.png"), tile);

        Assert.AreEqual((15, 200L, 100L), CustomImagery.ParseTilePath(Path.Combine("x", "Z15", "100", "200.png")));

        var vm = new CustomImageryViewModel(FakeUi.Services(out var ui));
        await vm.InjectFolderAsync(src);
        StringAssert.StartsWith(vm.Status, "Added 1 tiles");
        StringAssert.Contains(vm.Status, "1 skipped");

        var cached = Path.Combine(MyImageCache.Instance.CacheLocation, Custom.Instance.Name, "15", "100", "200.jpg");
        Assert.IsTrue(File.Exists(cached), cached);
        CollectionAssert.AreEqual(tile, File.ReadAllBytes(cached));
        CollectionAssert.AreEqual(tile, await MapTiles.GetTileAsync(Custom.Instance, 200, 100, 15), "the Custom map shows it");
        Assert.Contains(Custom.Instance, MapTiles.CommonProviders.ToList(), "offered in the map type lists");

        await vm.ClearCommand.ExecuteAsync(null);
        Assert.HasCount(1, ui.Confirms);
        Assert.IsFalse(File.Exists(cached));
        Assert.AreEqual("Removed 1 images", vm.Status);
    }

    // ---- Custom GDAL (GeoTIFF imagery)

    private static (byte, byte, byte) Halves(int x, int y, int height) => y < height / 2 ? ((byte)255, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)255);

    private static (byte, byte, byte) Quadrants(int x, int y, int size) =>
        (x < size / 2, y < size / 2) switch
        {
            (true, true) => (255, 0, 0),
            (false, true) => (0, 255, 0),
            (true, false) => (0, 0, 255),
            _ => (255, 255, 255),
        };

    private static uint Rgba(byte r, byte g, byte b) => r | ((uint)g << 8) | ((uint)b << 16) | 0xFF000000u;

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public void GeoTiffPixels_AreReadTopDown_FromStripsAndTiles_AndReduced()
    {
        var dir = NewFolder("tif-px");
        var striped = Path.Combine(dir, "s.tif");
        File.WriteAllBytes(striped, MapToolFixtures.GeoTiff(40, 20, (x, y) => Halves(x, y, 20), 149, -35, 0.001, 0.001));
        var tiled = Path.Combine(dir, "t.tif");
        File.WriteAllBytes(tiled, MapToolFixtures.GeoTiff(32, 32, (x, y) => Quadrants(x, y, 32), 149, -35, 0.001, 0.001, tile: 16));

        var s = PixelBuffer.Load(striped, 8192);
        Assert.AreEqual((40, 20, 1), (s.Width, s.Height, s.Factor));
        Assert.AreEqual(Rgba(255, 0, 0), s.Data[0], "top row red");
        Assert.AreEqual(Rgba(0, 0, 255), s.Data[19 * 40 + 39], "bottom row blue");
        Assert.AreEqual(Rgba(255, 0, 0), s.Data[9 * 40 + 5]);
        Assert.AreEqual(Rgba(0, 0, 255), s.Data[10 * 40 + 5]);

        var t = PixelBuffer.Load(tiled, 8192);
        Assert.AreEqual(Rgba(255, 0, 0), t.Data[0]);
        Assert.AreEqual(Rgba(0, 255, 0), t.Data[31]);
        Assert.AreEqual(Rgba(0, 0, 255), t.Data[31 * 32]);
        Assert.AreEqual(Rgba(255, 255, 255), t.Data[31 * 32 + 31]);
        Assert.AreEqual(Rgba(0, 0, 255), t.Data[16 * 32 + 15]);

        var half = PixelBuffer.Load(tiled, 16);
        Assert.AreEqual((16, 16, 2), (half.Width, half.Height, half.Factor));
        Assert.AreEqual(Rgba(0, 255, 0), half.Data[15]);
        Assert.AreEqual(Rgba(0, 0, 255), half.Data[15 * 16]);
    }

    /// <summary>The colour the overlay draws at a position (zoom 16), or null when the tile is empty there.</summary>
    private static SKColor? OverlayAt(double lat, double lng, int zoom = 16)
    {
        var px = MercatorProjection.Instance.FromLatLngToPixel(lat, lng, zoom);
        var png = GeoTiffImagery.RenderTile(px.X / 256, px.Y / 256, zoom);
        if (png == null)
            return null;
        using var bmp = SKBitmap.Decode(png);
        var c = bmp.GetPixel((int)(px.X % 256), (int)(px.Y % 256));
        return c.Alpha == 0 ? null : c;
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task GeoTiffFolder_IsLoaded_AndDrawnWhereItLies()
    {
        GeoTiffImagery.Clear();
        GeoTiffImagery.Opacity = 1;
        var dir = NewFolder("tif");
        // geographic: 0.001° pixels from 149.0E 35.0S, red north half, blue south half
        File.WriteAllBytes(Path.Combine(dir, "geo.tif"), MapToolFixtures.GeoTiff(64, 32, (x, y) => Halves(x, y, 32), 149.0, -35.0, 0.001, 0.001));
        // UTM 55S: 32 px of 10 m, quadrants
        var utmOrigin = new[] { 692_000.0, 6_085_000.0 };
        File.WriteAllBytes(Path.Combine(dir, "utm.tif"), MapToolFixtures.GeoTiff(32, 32, (x, y) => Quadrants(x, y, 32), utmOrigin[0], utmOrigin[1], 10, 10, projectedEpsg: 32755, tile: 16));
        File.WriteAllText(Path.Combine(dir, "notes.tif"), new string('x', 2000));

        try
        {
            var vm = new GeoTiffImageryViewModel(FakeUi.Services(out _));
            await vm.LoadFolderAsync(dir);

            Assert.HasCount(2, vm.Images, vm.Output);
            StringAssert.Contains(vm.Output, "notes.tif");
            Assert.AreEqual(dir, Settings.Instance[GeoTiffImagery.FolderKey], "remembered for next time");

            var geo = vm.Images.Single(i => i.FileName.EndsWith("geo.tif"));
            Assert.AreEqual(-35.0, geo.Bounds.Top, 1e-9);
            Assert.AreEqual(149.0, geo.Bounds.Left, 1e-9);
            Assert.AreEqual(149.064, geo.Bounds.Right, 1e-9);
            Assert.AreEqual(-35.032, geo.Bounds.Bottom, 1e-9);

            var utm = vm.Images.Single(i => i.FileName.EndsWith("utm.tif"));
            StringAssert.Contains(utm.Crs, "32755");
            Assert.IsTrue(utm.Bounds.Top is < -35.3 and > -35.5 && utm.Bounds.Left is > 149.1 and < 149.2, utm.Bounds.ToString());
            Assert.AreEqual(geo, vm.Images[0], "coarser image drawn first");

            Assert.AreEqual(SKColors.Red, OverlayAt(-35.008, 149.03));
            Assert.AreEqual(SKColors.Blue, OverlayAt(-35.024, 149.03));
            Assert.IsNull(OverlayAt(-34.99, 149.03), "north of the image");
            Assert.IsNull(GeoTiffImagery.RenderTile(0, 0, 3), "nothing there");

            // quadrants of the UTM image, through the reprojection
            var xy = new[] { utmOrigin[0] + 80, utmOrigin[1] - 80, utmOrigin[0] + 240, utmOrigin[1] - 240 };
            Reproject.ReprojectPoints(xy, null, ProjectionInfo.FromEpsgCode(32755), KnownCoordinateSystems.Geographic.World.WGS1984, 0, 2);
            Assert.AreEqual(SKColors.Red, OverlayAt(xy[1], xy[0], 19));
            Assert.AreEqual(SKColors.White, OverlayAt(xy[3], xy[2], 19));

            // the map type: satellite under the overlay; a transparent layer where nothing is loaded
            var provider = GeoTiffImageryProvider.Instance;
            Assert.AreEqual(2, provider.Overlays.Length);
            using var empty = provider.GetTileImage(new GPoint(0, 0), 3);
            Assert.IsNotNull(empty);
            Assert.Contains(provider, MapTiles.CommonProviders.ToList());

            vm.Opacity = 50;
            Assert.AreEqual(0.5, GeoTiffImagery.Opacity, 1e-9);
            Assert.AreEqual(128, OverlayAt(-35.008, 149.03)!.Value.Alpha, 1);
        }
        finally
        {
            GeoTiffImagery.Opacity = 1;
            GeoTiffImagery.Clear();
            Settings.Instance[GeoTiffImagery.FolderKey] = "";
        }
    }

    // ---- DEM

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task Dem_ListsTheLoadedElevationFiles()
    {
        var dem = new GeoTiff.geotiffdata
        {
            FileName = Path.Combine("srtm", "test-dem.tif"),
            Area = new RectLatLng(-35, 149, 1, 1),
            width = 3601,
            height = 3601,
            bits = 16,
        };
        lock (GeoTiff.index)
            GeoTiff.index.Add(dem);
        try
        {
            var vm = new DemInfoViewModel(FakeUi.Services(out _));
            await vm.RefreshCommand.ExecuteAsync(null);

            var entry = vm.Entries.Single(e => e.FileName == dem.FileName);
            Assert.AreEqual("test-dem.tif", entry.Name);
            StringAssert.StartsWith(entry.Line, dem.FileName + " = -35.0000,149.0000 to -36.0000,150.0000 = 3601*3601 16");
            Assert.AreEqual(dem.Area, entry.Area);
            StringAssert.Contains(vm.Status, "elevation files");
        }
        finally
        {
            lock (GeoTiff.index)
                GeoTiff.index.Remove(dem);
        }
    }

    // ---- map logs

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task LogsInAFolder_GetAMapPicture()
    {
        var dir = NewFolder("maplogs");
        var sub = Directory.CreateDirectory(Path.Combine(dir, "sub")).FullName;
        var flight = Path.Combine(dir, "flight.tlog");
        File.WriteAllBytes(flight, LogTestFiles.Tlog(new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Utc), attitudeSamples: 30));
        File.SetLastWriteTime(flight, new DateTime(2024, 5, 1, 13, 0, 0));
        var noGps = Path.Combine(sub, "bench.tlog");
        File.WriteAllBytes(noGps, LogTestFiles.Tlog(DateTime.UtcNow, attitudeSamples: 0));
        var bin = Path.Combine(sub, "flight.bin");
        File.WriteAllBytes(bin, LogTestFiles.BinaryLog(attSamples: 80));

        // green imagery instead of a tile server
        var green = Png.Encode(Enumerable.Repeat(0xFF00C000u, 256 * 256).ToArray(), 256, 256);
        var requests = 0;
        var renderer = new LogMapRenderer(EsriWorldImageryProvider.Instance, (_, _, _, _) =>
        {
            Interlocked.Increment(ref requests);
            return [green];
        });

        var vm = new MapLogsViewModel(FakeUi.Services(out _));
        await vm.MapFolderNowAsync(dir, renderer);

        StringAssert.StartsWith(vm.Status, "3 logs: 2 mapped, 1 without GPS");
        Assert.IsGreaterThan(0, requests);

        using (var map = SKBitmap.Decode(flight + ".jpg"))
        {
            Assert.IsGreaterThan(100, map.Width);
            var corner = map.GetPixel(12, 12);
            Assert.IsTrue(corner.Green > 150 && corner.Red < 60, "imagery drawn: " + corner);
        }

        Assert.AreEqual(new DateTime(2024, 5, 1, 13, 0, 0), File.GetLastWriteTime(flight + ".jpg"), "dated as the log");
        using (var text = SKBitmap.Decode(noGps + ".jpg"))
            Assert.AreEqual((100, 100), (text.Width, text.Height));
        Assert.IsTrue(File.Exists(bin + ".jpg"));

        // pictures already there are kept
        await vm.MapFolderNowAsync(dir, renderer);
        StringAssert.Contains(vm.Status, "3 already had a picture");
    }

    [TestMethod]
    public void TlogTracks_AreSplitPerVehicle()
    {
        var path = Path.Combine(NewFolder("tracks"), "two.tlog");
        var w = new LogTestFiles.TlogWriter(DateTime.UtcNow);
        for (var i = 0; i < 12; i++)
        {
            foreach (byte sys in new byte[] { 1, 2 })
                w.Write(i, MAVLink.MAVLINK_MSG_ID.GLOBAL_POSITION_INT,
                    new MAVLink.mavlink_global_position_int_t((uint)i, (int)((-35 - sys) * 1e7), (int)(149e7 + i), 0, 0, 0, 0, 0, 0), sys, 1);
        }

        File.WriteAllBytes(path, w.ToArray());
        var (tracks, sitl) = LogMapRenderer.ReadTracks(path);
        Assert.HasCount(2, tracks);
        Assert.HasCount(12, tracks[0]);
        Assert.AreEqual(-36, tracks[0][0].Lat, 1e-6);
        Assert.IsFalse(sitl);
    }

    // ---- Geo ref images

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task Photos_AreMatchedToCamMessages_AndGeotagged()
    {
        var dir = NewFolder("georef");
        var log = Path.Combine(dir, "survey.log");
        var lines = new List<string>
        {
            "FMT, 128, 89, FMT, BBnNZ, Type,Length,Name,Format,Columns",
            "FMT, 146, 43, CAM, QIHLLeeeccC, TimeUS,GPSTime,GPSWeek,Lat,Lng,Alt,RelAlt,GPSAlt,Roll,Pitch,Yaw",
        };
        for (var i = 0; i < 3; i++)
        {
            lines.Add(string.Format(CultureInfo.InvariantCulture, "CAM, {0}, {1}, 2300, {2}, {3}, 650, {4}, 651, 1, 2, 90",
                (i + 1) * 2_000_000, 345_600_000 + i * 2000, -35.3600 - i * 0.0005, 149.1600 + i * 0.0005, 50 + i));
        }

        File.WriteAllText(log, string.Join("\n", lines) + "\n");
        for (var i = 0; i < 3; i++)
        {
            using var bmp = new SKBitmap(32, 24);
            using (var c = new SKCanvas(bmp))
                c.Clear(SKColors.Gray);
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Jpeg, 80);
            File.WriteAllBytes(Path.Combine(dir, $"IMG_{i:0000}.jpg"), data.ToArray());
        }

        var vm = new GeoRefImagesViewModel(FakeUi.Services(out _)) { LogFile = log, UseAmslAlt = false };
        vm.SetImageFolder(dir);
        Assert.AreEqual(GeoRefMode.CamMessage, vm.Mode);

        await vm.ProcessCommand.ExecuteAsync(null);

        Assert.IsFalse(vm.StatusIsError, vm.Status + "\n" + vm.Output);
        Assert.HasCount(3, vm.Photos, vm.Output);
        Assert.HasCount(3, vm.Shots);
        CollectionAssert.AreEquivalent(new[] { -35.3600, -35.3605, -35.3610 }, vm.Photos.Select(p => Math.Round(p.Lat, 4)).ToArray());
        CollectionAssert.AreEquivalent(new[] { 50.0, 51.0, 52.0 }, vm.Photos.Select(p => p.RelAlt).ToArray());
        Assert.IsTrue(Directory.GetFiles(dir, "*.kml").Length > 0 || Directory.GetFiles(dir, "*.txt").Length > 0, "reports written");
        Assert.IsTrue(vm.CanGeoTag);

        await vm.GeoTagCommand.ExecuteAsync(null);
        var tagged = Directory.GetFiles(Path.Combine(dir, "geotagged"), "*_geotag.jpg");
        Assert.HasCount(3, tagged, vm.Output);
        StringAssert.Contains(vm.Output, "GeoTagging FINISHED");
    }

    [TestMethod]
    public async Task GeoRef_NeedsItsInputs_AndTheOffsetIsANumber()
    {
        var vm = new GeoRefImagesViewModel(FakeUi.Services(out _));
        await vm.ProcessCommand.ExecuteAsync(null);
        Assert.IsTrue(vm.StatusIsError);

        var dir = NewFolder("georef-in");
        File.WriteAllText(Path.Combine(dir, "location.txt"), "seconds_offset: 42\n");
        File.WriteAllText(Path.Combine(dir, "a.log"), "");
        vm.LogFile = Path.Combine(dir, "a.log");
        vm.SetImageFolder(dir);
        Assert.AreEqual("42", vm.OffsetSeconds, "read from location.txt");

        vm.SelectedMode = vm.Modes.Single(m => m.Mode == GeoRefMode.TimeOffset);
        Assert.IsTrue(vm.IsTimeOffset);
        vm.OffsetSeconds = "1,5";
        await vm.ProcessCommand.ExecuteAsync(null);
        Assert.IsTrue(vm.StatusIsError);
        StringAssert.Contains(vm.Output, "Use . as decimal separator");
    }
}
