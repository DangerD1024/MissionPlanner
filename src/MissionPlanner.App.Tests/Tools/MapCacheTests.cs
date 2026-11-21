using MissionPlanner.App.Core.ViewModels.Tools;
using MissionPlanner.App.Tests.ConfigScreens;

namespace MissionPlanner.App.Tests.Tools;

[TestClass]
public class MapCacheTests
{
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task FoldersAreMeasured_AndOldOrAllTilesRemoved()
    {
        var root = Path.Combine(TestEnvironment.DataRoot, "cache-" + Guid.NewGuid().ToString("N"));
        var sat = Directory.CreateDirectory(Path.Combine(root, "GoogleSatelliteMap", "17")).FullName;
        var osm = Directory.CreateDirectory(Path.Combine(root, "OpenStreetMap", "12")).FullName;
        File.WriteAllBytes(Path.Combine(sat, "old.jpg"), new byte[2000]);
        File.SetLastWriteTime(Path.Combine(sat, "old.jpg"), DateTime.Now.AddDays(-40));
        File.WriteAllBytes(Path.Combine(sat, "new.jpg"), new byte[3000]);
        File.WriteAllBytes(Path.Combine(osm, "a.png"), new byte[100]);
        File.WriteAllText(Path.Combine(osm, "notes.txt"), "not a tile");

        var (services, dialogs, _) = ParamTestSupport.CreateServices();
        var vm = new MapCacheViewModel(services, () => root);
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.HasCount(2, vm.Folders);
        Assert.AreEqual("GoogleSatelliteMap", vm.Folders[0].Name, "largest first");
        Assert.AreEqual((5000L, 2), (vm.Folders[0].Bytes, vm.Folders[0].Tiles));
        StringAssert.StartsWith(vm.Total, "3 tiles");

        await vm.RemoveOldCommand.ExecuteAsync(vm.Folders[0]);
        Assert.IsFalse(File.Exists(Path.Combine(sat, "old.jpg")));
        Assert.IsTrue(File.Exists(Path.Combine(sat, "new.jpg")));

        await vm.RemoveAllCommand.ExecuteAsync(vm.Folders.Single(f => f.Name == "OpenStreetMap"));
        Assert.HasCount(1, dialogs.Confirms);
        Assert.IsFalse(File.Exists(Path.Combine(osm, "a.png")));
        Assert.IsTrue(File.Exists(Path.Combine(osm, "notes.txt")), "only tiles are removed");
        Assert.AreEqual(0, vm.Folders.Single(f => f.Name == "OpenStreetMap").Tiles);
    }
}
