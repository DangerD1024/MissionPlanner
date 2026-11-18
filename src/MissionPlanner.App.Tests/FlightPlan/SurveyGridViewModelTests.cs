using GMap.NET;
using MissionPlanner.App.Core.Maps;
using MissionPlanner.App.Core.Mission;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.ViewModels.FlightPlan;
using MissionPlanner.Utilities;
using static MissionPlanner.App.Tests.FlightPlan.PlanTestSupport;
using Cmd = MAVLink.MAV_CMD;

namespace MissionPlanner.App.Tests.FlightPlan;

[TestClass]
public class SurveyGridViewModelTests
{
    private static readonly PointLatLng Base = P(-35.363261, 149.165230);

    /// <summary>A dialog over a 300 m x 200 m rectangle, with fixed terrain and settings kept in a dictionary.</summary>
    private static (SurveyGridViewModel Vm, AppServices Services, Dictionary<string, string> Saved) Create(SurveyOptions? options = null)
    {
        MapTiles.Initialize();
        var services = new AppServices(new InlineDispatcher(), new ScriptedDialogs(), new NoWindows());
        var saved = new Dictionary<string, string>();
        var vm = new SurveyGridViewModel(services)
        {
            TerrainLookup = (_, _) => 600,
            SettingsWriter = (k, v) => saved[k] = v,
        };
        vm.Polygon.AddRange(new[] { Offset(Base, 0, 0), Offset(Base, 300, 0), Offset(Base, 300, 200), Offset(Base, 0, 200) }
            .Select(p => new PointLatLngAlt(p.Lat, p.Lng)));
        vm.ApplyOptions(options ?? new SurveyOptions
        {
            AutoAngle = false, Angle = 0, LaneSpacing = 50, TriggerDistance = 25, StartFrom = Grid.StartPosition.BottomLeft,
        });
        return (vm, services, saved);
    }

    [TestMethod]
    public async Task GeneratesPreviewAndStatistics()
    {
        var (vm, _, _) = Create();
        await vm.RegenerateAsync();

        Assert.IsNull(vm.Error);
        Assert.IsGreaterThanOrEqualTo(4, vm.Stats.Lanes);
        Assert.AreEqual(vm.Stats.Lanes.ToString(), vm.LanesText);
        Assert.IsGreaterThan(0, vm.Stats.Images);
        Assert.AreEqual(60_000, vm.Stats.AreaM2, 300);
        StringAssert.EndsWith(vm.AreaText, " m² · 6.00 ha");
        Assert.HasCount(vm.Stats.Lanes, vm.Preview.Lanes);
        Assert.HasCount(4, vm.PolygonPoints);
        Assert.AreEqual("600 – 600 m ASL", vm.GroundText);
        Assert.AreEqual("Polygon centre (no home set)", vm.HomeText);
        Assert.IsFalse(vm.IsBusy);
    }

    [TestMethod]
    public async Task AcceptBuildsTheItemsClosesAndRemembersTheInputs()
    {
        var (vm, _, saved) = Create(new SurveyOptions
        {
            AutoAngle = false, Angle = 0, LaneSpacing = 50, TriggerDistance = 25, Altitude = 70,
            AddTakeoff = true, TakeoffAltitude = 20, Finish = SurveyFinish.ReturnToLaunch, Frame = AltFrame.Terrain,
        });
        bool? closed = null;
        vm.CloseRequested += (_, r) => closed = r;
        await vm.RegenerateAsync();

        await vm.AcceptCommand.ExecuteAsync(null);

        Assert.IsTrue(closed);
        var items = vm.Result;
        Assert.AreEqual(Cmd.TAKEOFF, items[0].Command);
        Assert.AreEqual(Cmd.WAYPOINT, items[1].Command);
        Assert.AreEqual(70f, items[1].Alt);
        Assert.AreEqual(Cmd.DO_SET_CAM_TRIGG_DIST, items[2].Command);
        Assert.AreEqual(25f, items[2].P1);
        Assert.AreEqual(Cmd.RETURN_TO_LAUNCH, items[^1].Command);
        Assert.IsTrue(items.All(i => i.Frame == AltFrame.Terrain));
        Assert.AreEqual(vm.ItemsText, items.Count.ToString());
        Assert.AreEqual("70", saved["next_survey_alt"]);
        Assert.AreEqual("ReturnToLaunch", saved["next_survey_finish"]);
    }

    [TestMethod]
    public async Task AcceptUsesTheLatestInputsEvenBeforeTheDebounce()
    {
        var (vm, _, _) = Create();
        await vm.RegenerateAsync();
        vm.Altitude = 42; // the debounced regeneration has not run yet

        await vm.AcceptCommand.ExecuteAsync(null);

        Assert.IsTrue(vm.Result.Where(i => i.Command == Cmd.WAYPOINT).All(i => i.Alt == 42f));
    }

    [TestMethod]
    public async Task InputChangesRegenerateAfterTheDebounce()
    {
        var (vm, _, _) = Create();
        await vm.RegenerateAsync();
        var lanes = vm.Stats.Lanes;
        var changed = new TaskCompletionSource();
        vm.PreviewChanged += (_, _) => changed.TrySetResult();

        vm.LaneSpacing = 25;

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsGreaterThan(lanes, vm.Stats.Lanes);
    }

    [TestMethod]
    public async Task CancelClosesWithoutItems()
    {
        var (vm, _, saved) = Create();
        bool? closed = null;
        vm.CloseRequested += (_, r) => closed = r;
        await vm.RegenerateAsync();

        vm.CancelCommand.Execute(null);

        Assert.IsFalse(closed);
        Assert.IsEmpty(vm.Result);
        Assert.IsTrue(saved.ContainsKey("next_survey_lanespacing"));
    }

    [TestMethod]
    public async Task PlannedHomeIsUsedForLandingAndStatistics()
    {
        var (vm, services, _) = Create(new SurveyOptions { Finish = SurveyFinish.LandAtHome, Trigger = SurveyTrigger.None });
        var home = Offset(Base, -100, -100);
        services.Mission.SetHome(home.Lat, home.Lng, 590);

        await vm.AcceptCommand.ExecuteAsync(null);

        Assert.AreEqual("Planned home", vm.HomeText);
        Assert.AreEqual(home, vm.HomePoint);
        var land = vm.Result[^1];
        Assert.AreEqual(Cmd.LAND, land.Command);
        Assert.AreEqual(home.Lat, land.Lat);
        Assert.AreEqual(home.Lng, land.Lng);
        Assert.IsFalse(vm.Result.Any(i => i.Command == Cmd.DO_SET_CAM_TRIGG_DIST));
    }

    [TestMethod]
    public async Task AutoAngleShowsTheLongestEdge()
    {
        var (vm, _, _) = Create(new SurveyOptions { AutoAngle = true, Angle = 45 });
        await vm.RegenerateAsync();

        Assert.AreEqual((decimal)SurveyPlanner.AutoAngle(vm.Polygon), vm.Angle);
        var angle = (double)vm.Angle!.Value;
        Assert.IsTrue(angle < 2 || angle > 178, $"the 300 m edges run north-south: {angle}");
        Assert.IsFalse(vm.IsAngleEditable);

        vm.AutoAngle = false;
        vm.Angle = 90;
        Assert.IsTrue(vm.IsAngleEditable);
        await vm.RegenerateAsync();
        Assert.AreEqual(90, vm.Options.Angle);
    }

    [TestMethod]
    public async Task TooSmallSpacingShowsWhyAndAcceptStaysOpen()
    {
        var (vm, _, _) = Create(new SurveyOptions { LaneSpacing = 0.01, Trigger = SurveyTrigger.None });
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        await vm.AcceptCommand.ExecuteAsync(null);

        Assert.IsFalse(closed);
        Assert.IsNotNull(vm.Error);
        StringAssert.Contains(vm.Error, "Lane spacing");
        Assert.IsEmpty(vm.GridPoints);
        Assert.AreEqual("-", vm.LanesText);
    }

    [TestMethod]
    public async Task FlightPlanAppendsTheAcceptedSurvey()
    {
        var (plan, _, windows) = CreatePage();
        plan.AddPolygonPoint(Base);
        plan.AddPolygonPoint(Offset(Base, 200, 0));
        plan.AddPolygonPoint(Offset(Base, 200, 200));
        plan.AddPolygonPoint(Offset(Base, 0, 200));
        var accepted = 0;
        windows.DialogResult = page =>
        {
            var survey = (SurveyGridViewModel)page;
            survey.TerrainLookup = (_, _) => null;
            survey.SettingsWriter = (_, _) => { };
            survey.ApplyOptions(new SurveyOptions { LaneSpacing = 40, Trigger = SurveyTrigger.None });
            var closed = false;
            survey.CloseRequested += (_, r) => closed = r;
            survey.AcceptCommand.ExecuteAsync(null).Wait();
            accepted = survey.Result.Count;
            return closed;
        };

        await plan.SurveyGridCommand.ExecuteAsync(null);

        Assert.IsGreaterThan(4, accepted);
        Assert.HasCount(accepted, plan.Store.Mission);
        Assert.IsTrue(plan.Store.Mission.All(i => i.Command == Cmd.WAYPOINT));
    }
}
