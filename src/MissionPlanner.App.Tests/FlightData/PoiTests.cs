using MissionPlanner.App.Core.Mission;

namespace MissionPlanner.App.Tests.FlightData;

[TestClass]
public class PoiTests
{
    private static string TempFile(string? content = null)
    {
        var path = Path.Combine(TestEnvironment.DataRoot, "poi-" + Guid.NewGuid().ToString("N") + ".txt");
        if (content != null)
            File.WriteAllText(path, content);
        return path;
    }

    [TestMethod]
    public void MissionPlannersFileIsReadAndWrittenTheSameWay()
    {
        // Mission Planner's poi.txt: lat TAB lng TAB name
        var path = TempFile("-35.3632\t149.1652\tCMAC\r\nnot a line\r\n-35.37\t149.17\tTower\r\n");
        var store = new PoiStore(path);
        Assert.HasCount(2, store.Items);
        Assert.AreEqual(new Poi(-35.3632, 149.1652, "CMAC"), store.Items[0]);

        store.Add(-35.38, 149.18, "Gate\tB");
        var lines = File.ReadAllLines(path);
        Assert.HasCount(3, lines);
        Assert.AreEqual("-35.3800000\t149.1800000\tGate B", lines[2], "saved on change, tabs in names replaced");

        store.Rename(store.Items[1], "Old tower");
        store.Remove(store.Items[0]);
        Assert.AreEqual("Old tower", new PoiStore(path).Items[0].Name, "a new store reads the same list back");
    }

    [TestMethod]
    public void LoadingAFileTwiceAddsNothingTwice()
    {
        var store = new PoiStore(TempFile());
        var other = TempFile("1\t2\tA\r\n1\t2\tA\r\n3\t4\tB\r\n");
        Assert.AreEqual(2, store.LoadFile(other));
        Assert.AreEqual(0, store.LoadFile(other));
        Assert.HasCount(2, store.Items);
        Assert.AreEqual("B", store.Near(3.00001, 4, 50)!.Name);
        Assert.IsNull(store.Near(3.1, 4, 50));
    }

    [TestMethod]
    public async Task MenuActionsAddRenameAndDelete()
    {
        var services = FakeUi.Services(out var ui);
        var store = new PoiStore(TempFile());
        services.UsePois(store);
        var changes = 0;
        store.Changed += (_, _) => changes++;
        var actions = new PoiActions(services);

        ui.PromptAnswer = "Landing zone";
        await actions.AddHereAsync(-35.1, 149.1);
        ui.PromptAnswer = "-35.2;149.2;Camp";
        await actions.AddAtCoordsAsync();
        Assert.AreEqual(new Poi(-35.2, 149.2, "Camp"), store.Items[1]);

        ui.PromptAnswer = "LZ";
        await actions.EditAsync(store.Items[0]);
        Assert.AreEqual("LZ", store.Items[0].Name);
        actions.Delete(store.Items[1]);
        Assert.HasCount(1, store.Items);
        Assert.AreEqual(4, changes, "the maps redraw after each change");
    }
}
