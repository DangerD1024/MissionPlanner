using GMap.NET;
using MissionPlanner.App.Core.Mission;
using MissionPlanner.App.Core.ViewModels.FlightPlan;
using static MissionPlanner.App.Tests.FlightPlan.PlanTestSupport;

namespace MissionPlanner.App.Tests.FlightPlan;

[TestClass]
public class FlightPlanViewModelTests
{
    private static readonly PointLatLng Base = P(-35.363261, 149.165230);

    [TestMethod]
    public void ClickAddsAWaypointWithTheDefaults()
    {
        var (vm, _, _) = CreatePage();
        vm.DefaultAlt = 75;
        vm.DefaultFrame = AltFrame.Terrain;

        vm.MapClick(Base, hit: null);

        var item = vm.Store.Mission.Single();
        Assert.AreEqual(MAVLink.MAV_CMD.WAYPOINT, item.Command);
        Assert.AreEqual(Base.Lat, item.Lat);
        Assert.AreEqual(Base.Lng, item.Lng);
        Assert.AreEqual(75f, item.Alt);
        Assert.AreEqual(AltFrame.Terrain, item.Frame);
        Assert.AreSame(item, vm.SelectedItem);
        Assert.HasCount(1, vm.MissionGeometry.Markers);
    }

    [TestMethod]
    public void ClickInRallyAndFenceModesAddsTheirItems()
    {
        var (vm, _, _) = CreatePage();
        vm.ShowList(MAVLink.MAV_MISSION_TYPE.RALLY);
        vm.MapClick(Base, null);
        vm.ShowList(MAVLink.MAV_MISSION_TYPE.FENCE);
        vm.MapClick(Offset(Base, 50, 0), null);

        Assert.IsEmpty(vm.Store.Mission);
        Assert.AreEqual(MAVLink.MAV_CMD.RALLY_POINT, vm.Store.Rally.Single().Command);
        var fence = vm.Store.Fence.Single();
        Assert.AreEqual(MAVLink.MAV_CMD.FENCE_CIRCLE_EXCLUSION, fence.Command);
        Assert.AreEqual(5f, fence.P1, "Mission Planner's small exclusion circle");
        Assert.AreSame(vm.Store.Fence, vm.CurrentList);
    }

    [TestMethod]
    public void ClickOnAMarkerSelectsIt()
    {
        var (vm, _, _) = CreatePage();
        vm.MapClick(Base, null);
        vm.MapClick(Offset(Base, 100, 0), null);
        var first = vm.Store.Mission[0];

        vm.MapClick(Base, first);

        Assert.HasCount(2, vm.Store.Mission, "no waypoint added");
        Assert.AreSame(first, vm.SelectedItem);
    }

    [TestMethod]
    public void HeadersFollowTheSelectedRowsCommand()
    {
        var (vm, _, _) = CreatePage();
        Assert.AreEqual("Delay", vm.Headers[0], "a mission starts with WAYPOINT columns");

        vm.MapClick(Base, null);
        var item = vm.Store.Mission[0];
        item.Command = MAVLink.MAV_CMD.LOITER_TURNS;
        Assert.AreEqual("Turns", vm.Headers[0]);
        Assert.AreEqual("Radius", vm.Headers[2]);

        vm.ShowList(MAVLink.MAV_MISSION_TYPE.FENCE);
        Assert.AreEqual("Points", vm.Headers[0]);
        CollectionAssert.Contains(vm.CommandOptions.ToList(), MAVLink.MAV_CMD.FENCE_RETURN_POINT);
    }

    [TestMethod]
    public void CommandListKeepsCommandsTheSectionLacks()
    {
        var (vm, _, _) = CreatePage();
        Assert.AreEqual(MavCmdSection.Copter, vm.Catalog.Section);
        vm.Store.Mission.Add(new MissionItem { Command = MAVLink.MAV_CMD.DO_VTOL_TRANSITION, P1 = 3 });
        CollectionAssert.Contains(vm.CommandOptions.ToList(), MAVLink.MAV_CMD.DO_VTOL_TRANSITION);
        Assert.AreEqual(MAVLink.MAV_CMD.WAYPOINT, vm.CommandOptions[0]);
    }

    [TestMethod]
    public void DeleteMoveAndRenumber()
    {
        var (vm, _, _) = CreatePage();
        for (var i = 0; i < 4; i++)
            vm.MapClick(Offset(Base, i * 100, 0), null);
        var items = vm.Store.Mission.ToList();

        vm.MoveUp(items[2]);
        CollectionAssert.AreEqual(new[] { items[0], items[2], items[1], items[3] }, vm.Store.Mission.ToList());
        vm.MoveDown(items[0]);
        CollectionAssert.AreEqual(new[] { items[2], items[0], items[1], items[3] }, vm.Store.Mission.ToList());

        vm.SelectedItem = items[1];
        vm.DeleteSelected();
        CollectionAssert.AreEqual(new[] { items[2], items[0], items[3] }, vm.Store.Mission.ToList());
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, vm.Store.Mission.Select(m => m.Seq).ToArray());
        Assert.AreSame(items[3], vm.SelectedItem, "the next row is selected");
    }

    [TestMethod]
    public void AddBelowInsertsAfterTheSelection()
    {
        var (vm, _, _) = CreatePage();
        vm.MapClick(Base, null);
        vm.MapClick(Offset(Base, 100, 0), null);
        vm.SelectedItem = vm.Store.Mission[0];

        vm.AddBelowCommand.Execute(null);

        Assert.HasCount(3, vm.Store.Mission);
        Assert.AreSame(vm.SelectedItem, vm.Store.Mission[1]);
        Assert.AreEqual(0, vm.Store.Mission[1].Lat);
    }

    [TestMethod]
    public async Task InsertWaypointSuggestsTheNearestLeg()
    {
        var (vm, dialogs, _) = CreatePage();
        vm.MapClick(Offset(Base, 0, 0), null);
        vm.MapClick(Offset(Base, 200, 0), null);
        vm.MapClick(Offset(Base, 200, 200), null);

        // the prompt's suggested number is accepted as is
        await vm.InsertWaypointAsync(Offset(Base, 210, 100), spline: true);

        Assert.HasCount(4, vm.Store.Mission);
        Assert.AreEqual(MAVLink.MAV_CMD.SPLINE_WAYPOINT, vm.Store.Mission[2].Command);
        Assert.AreSame(vm.Store.Mission[2], vm.SelectedItem);

        dialogs.Prompts.Enqueue("1");
        await vm.InsertWaypointAsync(Offset(Base, -50, 0), spline: false);
        Assert.AreEqual(MAVLink.MAV_CMD.WAYPOINT, vm.Store.Mission[0].Command);
        Assert.AreEqual(Offset(Base, -50, 0).Lat, vm.Store.Mission[0].Lat, 1e-9);

        dialogs.Prompts.Enqueue(null); // cancelled
        await vm.InsertWaypointAsync(Base, spline: false);
        Assert.HasCount(5, vm.Store.Mission);
    }

    [TestMethod]
    public async Task LoiterAndTakeoffAskForTheirValues()
    {
        var (vm, dialogs, _) = CreatePage();
        dialogs.Prompts.Enqueue("4");
        await vm.AddLoiterTurnsAsync(Base);
        dialogs.Prompts.Enqueue("30");
        await vm.AddLoiterTimeAsync(Base);
        dialogs.Prompts.Enqueue("25");
        await vm.AddTakeoffAsync();
        dialogs.Prompts.Enqueue("abc");
        await vm.AddTakeoffAsync();
        vm.AddLand(Base);
        vm.AddReturnToLaunch();

        var m = vm.Store.Mission;
        CollectionAssert.AreEqual(new[]
        {
            MAVLink.MAV_CMD.LOITER_TURNS, MAVLink.MAV_CMD.LOITER_TIME, MAVLink.MAV_CMD.TAKEOFF, MAVLink.MAV_CMD.LAND,
            MAVLink.MAV_CMD.RETURN_TO_LAUNCH,
        }, m.Select(i => i.Command).ToArray());
        Assert.AreEqual(4f, m[0].P1);
        Assert.AreEqual(30f, m[1].P1);
        Assert.AreEqual(25f, m[2].Alt);
        Assert.AreEqual(0, m[2].Lat, "takeoff where the vehicle is");
        Assert.AreEqual(0f, m[3].Alt);
        Assert.HasCount(1, dialogs.Messages, "the bad number was reported");
    }

    [TestMethod]
    public async Task SetHomeHereTakesTheTerrainAltitude()
    {
        var (vm, _, _) = CreatePage();
        await vm.SetHomeHereAsync(Base);
        Assert.IsTrue(vm.Store.HomeSet);
        Assert.AreEqual(Base.Lat, vm.HomeLat);
        Assert.AreEqual(600, vm.HomeAlt);

        // terrain not downloaded yet: the altitude stays
        vm.TerrainLookup = (_, _) => null;
        await vm.SetHomeHereAsync(Offset(Base, 100, 0));
        Assert.AreEqual(600, vm.HomeAlt);
        Assert.AreEqual(Offset(Base, 100, 0).Lat, vm.HomeLat, 1e-9);
    }

    [TestMethod]
    public void HomeFieldsAndDragMoveHome()
    {
        var (vm, _, _) = CreatePage();
        vm.HomeLat = -35.1;
        vm.HomeLng = 149.2;
        vm.HomeAlt = 580;
        Assert.IsTrue(vm.Store.HomeSet);
        Assert.AreEqual(new PointLatLng(-35.1, 149.2), vm.Home);

        vm.ItemDragged(HomeHandle.Instance, Base);
        Assert.AreEqual(Base, vm.Home);
        Assert.AreEqual(580, vm.HomeAlt);
    }

    [TestMethod]
    public void DraggingAMarkerMovesItsItem()
    {
        var (vm, _, _) = CreatePage();
        vm.MapClick(Base, null);
        var item = vm.Store.Mission[0];
        var to = Offset(Base, 30, 40);

        vm.ItemDragged(item, to);
        vm.ItemDragCompleted(item, to);

        Assert.AreEqual(to.Lat, item.Lat, 1e-12);
        Assert.AreEqual(to.Lng, item.Lng, 1e-12);
        Assert.AreEqual(to, vm.MissionGeometry.Markers[0].Position);
    }

    [TestMethod]
    public void DraggingAnInsertHandleAddsOneWaypointAndMovesIt()
    {
        var (vm, _, _) = CreatePage();
        vm.MapClick(Base, null);
        vm.MapClick(Offset(Base, 200, 0), null);
        var handle = vm.MissionGeometry.Midpoints.Single();

        vm.ItemDragged(handle, Offset(Base, 100, 10));
        vm.ItemDragged(handle, Offset(Base, 100, 50));
        vm.ItemDragCompleted(handle, Offset(Base, 100, 60));

        Assert.HasCount(3, vm.Store.Mission);
        Assert.AreEqual(Offset(Base, 100, 60).Lng, vm.Store.Mission[1].Lng, 1e-9);
        Assert.AreSame(vm.Store.Mission[1], vm.SelectedItem);
    }

    [TestMethod]
    public async Task PolygonModeTurnsClicksIntoPolygonPoints()
    {
        var (vm, _, _) = CreatePage();
        vm.AddPolygonPoint(Base);
        Assert.IsTrue(vm.PolygonMode);
        vm.MapClick(Offset(Base, 100, 0), null);
        vm.MapClick(Offset(Base, 100, 100), null);

        Assert.IsEmpty(vm.Store.Mission);
        Assert.HasCount(3, vm.Polygon);
        Assert.Contains("3 points", vm.PolygonInfo);

        vm.ItemDragged(new PolygonVertexHandle(2), Offset(Base, 120, 120));
        Assert.AreEqual(Offset(Base, 120, 120), vm.Polygon[2]);

        await vm.FenceFromPolygonAsync(inclusion: true);
        Assert.AreSame(vm.Store.Fence, vm.CurrentList, "the fence list is shown");
        Assert.HasCount(3, vm.Store.Fence);
        Assert.IsTrue(vm.Store.Fence.All(i => i.Command == MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION && i.P1 == 3));
        Assert.IsEmpty(vm.Polygon);
        Assert.IsFalse(vm.PolygonMode);
        Assert.HasCount(1, vm.FenceGeometry.Polygons);
    }

    [TestMethod]
    public async Task FenceCirclesAndReturnPoint()
    {
        var (vm, dialogs, _) = CreatePage();
        dialogs.Prompts.Enqueue("150");
        await vm.FenceCircleAsync(Base, inclusion: true);
        vm.FenceReturnPointHere(Offset(Base, 10, 0));
        vm.FenceReturnPointHere(Offset(Base, 20, 0));

        Assert.HasCount(2, vm.Store.Fence, "one return point, moved");
        Assert.AreEqual(150f, vm.Store.Fence[0].P1);
        Assert.AreEqual(Offset(Base, 20, 0).Lat, vm.Store.Fence[1].Lat, 1e-9);

        // a fence vertex deleted from the map keeps the polygon count right
        vm.Polygon.Add(Base);
        vm.Polygon.Add(Offset(Base, 0, 100));
        vm.Polygon.Add(Offset(Base, 100, 100));
        vm.Polygon.Add(Offset(Base, 100, 0));
        await vm.FenceFromPolygonAsync(inclusion: false);
        vm.DeleteHit(vm.Store.Fence[3]);
        Assert.IsTrue(vm.Store.Fence.Skip(2).All(i => i.P1 == 3));
    }

    [TestMethod]
    public async Task SurveyNeedsAPolygonAndAppendsItsResult()
    {
        var (vm, dialogs, windows) = CreatePage();
        await vm.SurveyGridCommand.ExecuteAsync(null);
        Assert.HasCount(1, dialogs.Messages, "asks for a polygon first");
        Assert.IsEmpty(windows.Shown);

        vm.AddPolygonPoint(Base);
        vm.AddPolygonPoint(Offset(Base, 100, 0));
        vm.AddPolygonPoint(Offset(Base, 100, 100));
        windows.DialogResult = page =>
        {
            var survey = (SurveyGridViewModel)page;
            Assert.HasCount(3, survey.Polygon);
            return false; // cancelled: nothing added
        };
        await vm.SurveyGridCommand.ExecuteAsync(null);
        Assert.HasCount(1, windows.Shown);
        Assert.IsEmpty(vm.Store.Mission);
    }

    [TestMethod]
    public async Task ClearAsksFirst()
    {
        var (vm, dialogs, _) = CreatePage();
        vm.MapClick(Base, null);
        dialogs.ConfirmAnswer = false;
        await vm.ClearListCommand.ExecuteAsync(null);
        Assert.HasCount(1, vm.Store.Mission);

        dialogs.ConfirmAnswer = true;
        await vm.ClearListCommand.ExecuteAsync(null);
        Assert.IsEmpty(vm.Store.Mission);
        Assert.IsNull(vm.SelectedItem);
    }

    [TestMethod]
    public async Task ReadAndWriteNeedAVehicle()
    {
        var (vm, dialogs, _) = CreatePage();
        vm.MapClick(Base, null);
        await vm.ReadCommand.ExecuteAsync(null);
        await vm.WriteCommand.ExecuteAsync(null);
        Assert.HasCount(2, dialogs.Messages);
        Assert.IsTrue(dialogs.Messages.All(m => m.Contains("Connect", StringComparison.Ordinal)));
        Assert.HasCount(1, vm.Store.Mission, "nothing touched");
        Assert.IsFalse(vm.IsBusy);
    }

    [TestMethod]
    public async Task LoadAndSaveFilesThroughTheStore()
    {
        var (vm, dialogs, _) = CreatePage();
        vm.MapClick(Base, null);
        vm.MapClick(Offset(Base, 100, 0), null);
        vm.Store.Mission[0].Frame = AltFrame.Terrain;
        vm.Store.Mission[1].Frame = AltFrame.Absolute;
        await vm.SetHomeHereAsync(Base);

        var path = Path.Combine(TestEnvironment.DataRoot, "vm-" + Guid.NewGuid().ToString("N") + ".waypoints");
        dialogs.SavePath = path;
        await vm.SaveFileCommand.ExecuteAsync(null);
        Assert.IsTrue(File.Exists(path));

        var (other, otherDialogs, _) = CreatePage();
        otherDialogs.OpenPath = path;
        other.Store.Mission.Clear();
        await other.LoadFileCommand.ExecuteAsync(null);
        Assert.HasCount(2, other.Store.Mission);
        Assert.AreEqual(AltFrame.Terrain, other.DefaultFrame, "the default frame follows the first waypoint");
        Assert.AreEqual(600, other.Store.HomeAlt);

        var bad = Path.Combine(TestEnvironment.DataRoot, "not-a-mission.txt");
        await File.WriteAllTextAsync(bad, "hello\n");
        Assert.IsFalse(await other.LoadFileFromAsync(bad));
        Assert.HasCount(1, otherDialogs.Messages);
        Assert.HasCount(2, other.Store.Mission, "the list is kept");
    }

    [TestMethod]
    public void DistanceReadoutsFollowThePointer()
    {
        var (vm, _, _) = CreatePage();
        vm.HomeLat = Base.Lat;
        vm.HomeLng = Base.Lng;
        vm.MapClick(Offset(Base, 100, 0), null);

        vm.PointerMoved(Offset(Base, 100, 50));

        Assert.AreEqual("Distance: 200 m", vm.DistanceText, "out and back");
        Assert.StartsWith("Prev: 50 m", vm.PrevText);
        Assert.AreEqual("Home: 112 m", vm.HomeDistanceText);
    }
}
