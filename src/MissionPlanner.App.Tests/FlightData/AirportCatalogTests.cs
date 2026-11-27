using MissionPlanner.App.Core.Maps;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Tests.FlightData;

/// <summary>The maps' airports (Map settings > Show airports), read with Mission Planner's rules.</summary>
[TestClass]
public class AirportCatalogTests
{
    private const string Csv = """"
        "id","ident","type","name","latitude_deg","longitude_deg","elevation_ft"
        1,"EGLL","large_airport","London Heathrow Airport",51.4706,-0.461941,83
        2,"EGLW","heliport","London Heliport",51.469,-0.179,18
        3,"EG01","small_airport","A Farm Strip",52.1,-1.2,300
        4,"EGXX","closed","Closed Field",52.2,-1.3,300
        5,"EGTH","seaplane_base","Lake",52.3,-1.4,10
        6,"00AA","medium_airport","Ident Too Short",38.7,-101.4,3435
        7,"KJFK","large_airport","John F Kennedy International Airport, ""JFK""",40.639801,-73.7789,13
        8,"YSSY","large_airport","Sydney Kingsford Smith International Airport",-33.9461,151.177,21
        9,"NZCH","medium_airport","Christchurch",-43.4894,172.532,123
        10,"NFFN","large_airport","Nadi",-17.7554,177.443,59
        11,"NSFA","medium_airport","Faleolo",-13.83,-172.008,58
        12,"EGKK","large_airport","Gatwick",not-a-number,-0.19,202
        """";

    private static List<Airport> Parse() => AirportCatalog.Parse(new StringReader(Csv)).ToList();

    [TestMethod]
    public void OnlyTheAirportsMissionPlannerShows()
    {
        var idents = Parse().Select(a => a.Ident).ToList();
        CollectionAssert.AreEqual(new[] { "EGLL", "00AA", "KJFK", "YSSY", "NZCH", "NFFN", "NSFA" }, idents,
            "no heliports, small airfields, seaplane bases, closed or unreadable ones; four-letter codes only");
    }

    [TestMethod]
    public void QuotedNamesKeepTheirCommasAndQuotes()
    {
        var jfk = Parse().Single(a => a.Ident == "KJFK");
        Assert.AreEqual("John F Kennedy International Airport, \"JFK\"", jfk.Name);
        Assert.AreEqual(40.639801, jfk.Lat, 1e-9);
        Assert.AreEqual(-73.7789, jfk.Lng, 1e-9);
    }

    [TestMethod]
    public void CirclesAreMissionPlannersSizes()
    {
        var all = Parse().ToDictionary(a => a.Ident);
        Assert.AreEqual(9000, all["EGLL"].RadiusM);
        Assert.AreEqual(9000, all["KJFK"].RadiusM);
        Assert.AreEqual(5559, all["YSSY"].RadiusM, "Australia");
        Assert.AreEqual(5559, all["NZCH"].RadiusM, "Mission Planner's box takes New Zealand in too");
    }

    [TestMethod]
    public async Task TheMapAsksForItsAreaIncludingAcrossTheDateLine()
    {
        var file = Path.Combine(TestEnvironment.DataRoot, "airports-" + Guid.NewGuid().ToString("N") + ".csv");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, Csv);
        try
        {
            var catalog = new AirportCatalog(file);
            var loaded = 0;
            catalog.Loaded += (_, _) => Interlocked.Increment(ref loaded);
            Assert.IsFalse(catalog.IsLoaded);
            await catalog.LoadAsync();
            await catalog.LoadAsync();
            Assert.IsTrue(catalog.IsLoaded);
            Assert.AreEqual(1, loaded, "read once");

            CollectionAssert.AreEqual(new[] { "EGLL" }, catalog.Within(51, 52, -1, 0).Select(a => a.Ident).ToList());
            Assert.AreEqual(0, catalog.Within(10, 20, 0, 10).Count);
            // west of 180 to east of -180: Fiji and Samoa
            CollectionAssert.AreEquivalent(new[] { "NFFN", "NSFA" },
                catalog.Within(-20, -10, 170, -170).Select(a => a.Ident).ToList());
            Assert.AreEqual(2, catalog.Within(-90, 90, -180, 180, max: 2).Count, "capped");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [TestMethod]
    public async Task AMissingFileMeansNoAirportsNotAnError()
    {
        var catalog = new AirportCatalog(Path.Combine(TestEnvironment.DataRoot, "no-such-airports.csv"));
        await catalog.LoadAsync();
        Assert.IsTrue(catalog.IsLoaded);
        Assert.AreEqual(0, catalog.Count);
    }

    [TestMethod]
    public async Task TheShippedListHasTheWorldsLargerAirports()
    {
        // airports.csv at the repository root is copied next to the app by the build
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "airports.csv")))
            dir = dir.Parent;
        if (dir == null)
            Assert.Inconclusive("airports.csv not found above " + AppContext.BaseDirectory);

        var catalog = new AirportCatalog(Path.Combine(dir.FullName, "airports.csv"));
        await catalog.LoadAsync();
        Assert.IsTrue(catalog.Count is > 3000 and < 10000, catalog.Count.ToString());
        var heathrow = catalog.Within(51.3, 51.6, -0.7, -0.2).Select(a => a.Ident).ToList();
        CollectionAssert.Contains(heathrow, "EGLL");
    }

    [TestMethod]
    public void TheMapSettingsSwitchUsesMissionPlannersKey()
    {
        Settings.Instance.Remove(MapOptions.ShowAirportsKey);
        try
        {
            Assert.IsTrue(MapOptions.ShowAirports, "on unless switched off, as in Mission Planner");
            var services = FakeUi.Services(out _);
            var page = new MissionPlanner.App.Core.ViewModels.FlightData.MapSettingsViewModel(services);
            Assert.IsTrue(page.ShowAirports);
            var changed = 0;
            services.MapSettingsChanged += (_, _) => changed++;

            page.ShowAirports = false;

            Assert.AreEqual("False", Settings.Instance[MapOptions.ShowAirportsKey]);
            Assert.IsFalse(MapOptions.ShowAirports);
            Assert.AreEqual(1, changed, "the maps redraw");
        }
        finally
        {
            Settings.Instance.Remove(MapOptions.ShowAirportsKey);
        }
    }
}
