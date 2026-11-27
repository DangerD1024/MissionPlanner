using GMap.NET;
using MissionPlanner.App.Core.Maps;
using MissionPlanner.App.Core.ViewModels.ConfigScreens;
using MissionPlanner.App.Core.ViewModels.FlightData;
using MissionPlanner.App.Tests.FlightPlan;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Tests.FlightData;

/// <summary>
/// The map settings page (gear button on the DATA and PLAN maps, SETTINGS > Map): Mission Planner's
/// keys, and the DATA map model and the tile access following each change at once.
/// </summary>
[TestClass]
public class MapSettingsTests
{
    private static readonly string[] Keys =
    [
        MapOptions.TrackLengthKey, MapOptions.DisplayCogKey, MapOptions.DisplayHeadingKey, MapOptions.DisplayNavBearingKey,
        MapOptions.DisplayTargetKey, MapOptions.LineLengthKey, MapOptions.AccessModeKey, PlannerSettingsViewModel.MapProviderKey,
    ];

    private Dictionary<string, string?> _saved = new();

    [ClassInitialize]
    public static void Init(TestContext _) =>
        MapTiles.Initialize(Path.Combine(TestEnvironment.DataRoot, "gmapcache") + Path.DirectorySeparatorChar);

    [TestInitialize]
    public void Save()
    {
        _saved = Keys.ToDictionary(k => k, k => Settings.Instance[k]);
        foreach (var k in Keys)
            Settings.Instance.Remove(k);
    }

    [TestCleanup]
    public void Restore()
    {
        foreach (var (k, v) in _saved)
        {
            if (v == null)
                Settings.Instance.Remove(k);
            else
                Settings.Instance[k] = v;
        }

        MapTiles.SetAccessMode(AccessMode.ServerAndCache);
    }

    [TestMethod]
    public void DefaultsAreMissionPlannersAndTheTrackKeepsItsLength()
    {
        var services = FakeUi.Services(out _);
        var page = services.MapSettings;
        Assert.AreSame(page, services.MapSettings, "one page for the app");
        Assert.IsTrue(page.DisplayCog && page.DisplayHeading && page.DisplayNavBearing && page.DisplayTarget);
        Assert.AreEqual(500m, page.LineLength);
        Assert.AreEqual(MapOptions.DefaultTrackLength, page.TrackLength);
        Assert.AreEqual(nameof(AccessMode.ServerAndCache), page.AccessMode.Key);

        var data = new FlightDataViewModel(services);
        var m = data.MapModel;
        Assert.AreEqual(MapOptions.DefaultTrackLength, m.Track.Capacity);
        Assert.IsTrue(m.ShowCog && m.ShowHeading && m.ShowNavBearing && m.ShowTarget);
        Assert.AreEqual(500, m.LineLength);
    }

    [TestMethod]
    public void EachOptionWritesMissionPlannersKeyAndReachesTheMap()
    {
        var services = FakeUi.Services(out _);
        var data = new FlightDataViewModel(services);
        var m = data.MapModel;
        var page = services.MapSettings;

        var v = m.Version;
        page.DisplayCog = false;
        Assert.AreEqual("False", Settings.Instance["GMapMarkerBase_DisplayCOG"]);
        Assert.IsFalse(m.ShowCog);
        Assert.IsTrue(m.Version > v, "the map redraws");

        page.DisplayHeading = false;
        page.DisplayNavBearing = false;
        page.DisplayTarget = false;
        Assert.AreEqual("False", Settings.Instance["GMapMarkerBase_DisplayHeading"]);
        Assert.AreEqual("False", Settings.Instance["GMapMarkerBase_DisplayNavBearing"]);
        Assert.AreEqual("False", Settings.Instance["GMapMarkerBase_DisplayTarget"]);
        Assert.IsFalse(m.ShowHeading || m.ShowNavBearing || m.ShowTarget);

        page.DisplayTarget = true;
        Assert.IsTrue(m.ShowTarget);
        Assert.AreEqual("True", Settings.Instance["GMapMarkerBase_DisplayTarget"]);

        page.LineLength = 250;
        Assert.AreEqual("250", Settings.Instance["GMapMarkerBase_length"], "the spelling Mission Planner saves and reads at start");
        Assert.AreEqual(250, m.LineLength);
        page.LineLength = 5;
        Assert.AreEqual("250", Settings.Instance["GMapMarkerBase_length"], "below Mission Planner's 10: not taken");
        page.LineLength = null;
        Assert.AreEqual(250, m.LineLength);

        page.TrackLength = 500;
        Assert.AreEqual("500", Settings.Instance["NUM_tracklength"]);
        Assert.AreEqual(500, m.Track.Capacity);

        // a DATA screen made later starts with them
        var later = new FlightDataViewModel(services);
        Assert.AreEqual(500, later.MapModel.Track.Capacity);
        Assert.IsFalse(later.MapModel.ShowCog);
        Assert.IsTrue(later.MapModel.ShowTarget);
        Assert.AreEqual(250, later.MapModel.LineLength);
    }

    [TestMethod]
    public void ShorterTrackKeepsTheNewestPoints()
    {
        var services = FakeUi.Services(out _);
        var data = new FlightDataViewModel(services);
        var track = data.MapModel.Track;
        for (var i = 0; i < 600; i++)
            Assert.IsTrue(track.Add(-35 + i * 0.0001, 149));
        Assert.AreEqual(600, track.Count);

        services.MapSettings.TrackLength = 200;
        Assert.AreEqual(200, track.Capacity);
        Assert.AreEqual(200, track.Count);
        Assert.AreEqual(-35 + 400 * 0.0001, track[0].Lat, 1e-9, "the oldest are dropped");
        Assert.AreEqual(-35 + 599 * 0.0001, track.Last!.Value.Lat, 1e-9, "the newest stay");

        services.MapSettings.TrackLength = 1000;
        Assert.AreEqual(1000, track.Capacity);
        Assert.AreEqual(200, track.Count, "growing keeps what is there");
        Assert.IsTrue(track.Add(-34, 149));
        Assert.AreEqual(201, track.Count);
    }

    [TestMethod]
    public void TrackBufferResizeKeepsOrderAcrossTheWrap()
    {
        var t = new TrackBuffer(capacity: 4, minSpacingMetres: 0);
        for (var i = 1; i <= 6; i++)
            t.Add(i, i); // the ring wrapped: 3, 4, 5, 6
        var version = t.Version;
        t.Resize(3);
        CollectionAssert.AreEqual(new[] { 4.0, 5.0, 6.0 }, Enumerable.Range(0, t.Count).Select(i => t[i].Lat).ToArray());
        Assert.IsTrue(t.Version > version);
        t.Add(7, 7);
        CollectionAssert.AreEqual(new[] { 5.0, 6.0, 7.0 }, Enumerable.Range(0, t.Count).Select(i => t[i].Lat).ToArray());
        t.Resize(3);
        Assert.AreEqual(3, t.Count);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => t.Resize(1));
    }

    [TestMethod]
    public void StoredValuesFromMissionPlannerAreShown()
    {
        Settings.Instance["NUM_tracklength"] = "300";
        Settings.Instance["GMapMarkerBase_DisplayHeading"] = "False";
        Settings.Instance["GMapMarkerBase_length"] = "120";
        Settings.Instance["mapCache"] = "CacheOnly";
        var page = FakeUi.Services(out _).MapSettings;
        Assert.AreEqual(300, page.TrackLength);
        Assert.Contains(300, page.TrackLengths, "a length not in the list is offered too");
        Assert.IsFalse(page.DisplayHeading);
        Assert.AreEqual(120m, page.LineLength);
        Assert.AreEqual(nameof(AccessMode.CacheOnly), page.AccessMode.Key);

        Settings.Instance["NUM_tracklength"] = "5";
        Assert.AreEqual(MapOptions.MinTrackLength, MapOptions.TrackLength, "kept inside Mission Planner's range");
        Settings.Instance["mapCache"] = "rubbish";
        Assert.AreEqual(AccessMode.ServerAndCache, MapOptions.AccessMode);
    }

    [TestMethod]
    public void AccessModeReachesTheTileLoader()
    {
        var services = FakeUi.Services(out _);
        var page = services.MapSettings;
        var reloads = 0;
        void Count() => reloads++;
        MapTiles.AccessModeChanged += Count;
        try
        {
            page.AccessMode = MapSettingsViewModel.AccessModes.Single(m => m.Key == nameof(AccessMode.CacheOnly));
            Assert.AreEqual("CacheOnly", Settings.Instance["mapCache"], "Mission Planner stores the enum name");
            Assert.AreEqual(AccessMode.CacheOnly, MapTiles.AccessMode);
            Assert.AreEqual(AccessMode.CacheOnly, GMaps.Instance.Mode);
            Assert.AreEqual(1, reloads, "the maps fetch their tiles again");

            page.AccessMode = MapSettingsViewModel.AccessModes.Single(m => m.Key == nameof(AccessMode.ServerOnly));
            Assert.AreEqual(AccessMode.ServerOnly, MapTiles.AccessMode);
            page.AccessMode = MapSettingsViewModel.AccessModes[0];
            Assert.AreEqual(AccessMode.ServerAndCache, MapTiles.AccessMode);
            Assert.AreEqual("ServerAndCache", Settings.Instance["mapCache"]);
            Assert.AreEqual(3, reloads);
        }
        finally
        {
            MapTiles.AccessModeChanged -= Count;
        }
    }

    [TestMethod]
    public void TheGearButtonsAndSettingsOpenTheSamePage()
    {
        var services = FakeUi.Services(out var ui);
        var data = new FlightDataViewModel(services);
        data.OpenMapSettingsCommand.Execute(null);
        Assert.AreSame(services.MapSettings, ui.Dialogs.Last(), "DATA's gear button");

        var settings = new PlannerSettingsViewModel(services);
        Assert.AreSame(services.MapSettings, settings.Map, "the SETTINGS Map card shows the same page's default map");
        settings.OpenMapSettingsCommand.Execute(null);
        Assert.AreSame(services.MapSettings, ui.Dialogs.Last(), "SETTINGS > Map settings...");

        var (plan, _, windows) = PlanTestSupport.CreatePage();
        plan.OpenMapSettingsCommand.Execute(null);
        Assert.AreSame(plan.Services.MapSettings, windows.Shown.Last(), "PLAN's gear button");

        var closed = (bool?)null;
        services.MapSettings.CloseRequested += (_, r) => closed = r;
        services.MapSettings.CloseCommand.Execute(null);
        Assert.IsTrue(closed);
    }

    [TestMethod]
    public void DefaultMapIsTheSettingTheScreensStartWith()
    {
        var services = FakeUi.Services(out _);
        var settings = new PlannerSettingsViewModel(services);
        var name = MapTiles.CommonProviders[2].Name;
        settings.Map.SelectedMapProvider = name;
        Assert.AreEqual(name, Settings.Instance[PlannerSettingsViewModel.MapProviderKey]);
        Assert.AreEqual(name, services.MapSettings.SelectedMapProvider);
        Assert.IsTrue(settings.Map.MapProviders.Count >= MapTiles.CommonProviders.Count);
    }
}
