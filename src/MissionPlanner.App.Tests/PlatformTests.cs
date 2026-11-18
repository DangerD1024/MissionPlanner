using GMap.NET;
using GMap.NET.MapProviders;
using MissionPlanner.App.Core.Maps;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Tests;

[TestClass]
public class PlatformTests
{
    [TestMethod]
    public void DataDirectoryIsWritableOnThisRuntime()
    {
        // on Linux/macOS under .NET this used to resolve to /usr/share/Mission Planner/
        var dir = Settings.GetDataDirectory();

        Assert.StartsWith(TestEnvironment.DataRoot, dir, "data directory ignores the per-user location");
        Directory.CreateDirectory(dir);
        var probe = Path.Combine(dir, "write-probe");
        File.WriteAllText(probe, "ok");
        Assert.AreEqual("ok", File.ReadAllText(probe));
    }

    [TestMethod]
    public void UnixDataDirectoryIsNotSystemWide()
    {
        if (OperatingSystem.IsWindows())
            Assert.Inconclusive("Windows keeps the shared ProgramData location");

        var saved = Settings.CustomUserDataDirectory;
        Settings.CustomUserDataDirectory = "";
        try
        {
            var dir = Settings.GetDataDirectory();
            Assert.IsFalse(dir.StartsWith("/usr/", StringComparison.Ordinal), "resolved to " + dir);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.StartsWith(home, dir);
        }
        finally
        {
            Settings.CustomUserDataDirectory = saved;
        }
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task TileCacheRoundTripsWithoutNetwork()
    {
        MapTiles.Initialize(Path.Combine(TestEnvironment.DataRoot, "gmapcache") + Path.DirectorySeparatorChar);

        // a 1x1 PNG
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYGBgAAAABQABpfZFQAAAAABJRU5ErkJggg==");
        var provider = GMapProviders.OpenStreetMap;
        var pos = new GPoint(3, 5);

        Assert.IsTrue(GMaps.Instance.PrimaryCache.PutImageToCache(png, provider.DbId, pos, 4));

        var previous = GMaps.Instance.Mode;
        GMaps.Instance.Mode = AccessMode.CacheOnly;
        try
        {
            var bytes = await MapTiles.GetTileAsync(provider, pos.X, pos.Y, 4);
            Assert.IsNotNull(bytes, "tile not served from the file cache");
            CollectionAssert.AreEqual(png, bytes);

            Assert.IsNull(await MapTiles.GetTileAsync(provider, 9, 9, 4), "cache-only mode returned a tile it never stored");
        }
        finally
        {
            GMaps.Instance.Mode = previous;
        }
    }

    [TestMethod]
    [DataRow("iVBORw0KGgoAAAANSUhEUg==", true)]
    [DataRow("/9j/4AAQSkZJRgABAQ==", true)]
    [DataRow("PCFET0NUWVBFIGh0bWw+", false)]
    public void TileProxyAcceptsOnlyImages(string base64, bool expected)
    {
        Assert.AreEqual(expected, MapTiles.TileBytesProxy.LooksLikeImage(Convert.FromBase64String(base64)));
    }
}
