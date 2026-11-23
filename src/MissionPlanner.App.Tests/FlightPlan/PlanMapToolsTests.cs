using GMap.NET;
using MissionPlanner.App.Core.Mission;
using MissionPlanner.App.Core.ViewModels.FlightPlan;
using MissionPlanner.App.Tests.FlightData;

namespace MissionPlanner.App.Tests.FlightPlan;

[TestClass]
public class PlanMapToolsTests
{
    [TestMethod]
    public void UtmRoundTripsAndKnownPoints()
    {
        // on a central meridian at the equator: easting 500 000, northing 0
        var eq = Utm.FromLatLng(0, 3);
        Assert.AreEqual((31, true), (eq.Zone, eq.North));
        Assert.AreEqual(500000, eq.Easting, 0.01);
        Assert.AreEqual(0, eq.Northing, 0.01);

        foreach (var (lat, lng) in new[] { (-35.363261, 149.165230), (51.4778, -0.0015), (64.1, -21.9), (-0.5, 33.2) })
        {
            var u = Utm.FromLatLng(lat, lng);
            var (la, lo) = Utm.ToLatLng(u);
            Assert.AreEqual(lat, la, 1e-7, $"{lat},{lng}");
            Assert.AreEqual(lng, lo, 1e-7, $"{lat},{lng}");
        }

        var cmac = Utm.FromLatLng(-35.363261, 149.165230);
        Assert.AreEqual((55, false), (cmac.Zone, cmac.North));
        var parsed = Utm.Parse(cmac.ToString())!.Value;
        Assert.AreEqual((55, false), (parsed.Zone, parsed.North));
        Assert.AreEqual(Math.Round(cmac.Easting), parsed.Easting, 1e-9);
        Assert.AreEqual(Math.Round(cmac.Northing), parsed.Northing, 1e-9);
    }

    [TestMethod]
    public void UtmTextForms()
    {
        Assert.AreEqual(new UtmPoint(55, false, 694000, 6084000), Utm.Parse("55S 694000 6084000"));
        Assert.AreEqual(new UtmPoint(55, false, 694000, 6084000), Utm.Parse("55 S 694000, 6084000"));
        Assert.AreEqual(new UtmPoint(30, true, 699316, 5709446), Utm.Parse("30U 699316 5709446"), "a band letter north of the equator");
        Assert.AreEqual(new UtmPoint(55, false, 1, 2), Utm.Parse("55H 1 2"), "band H is south");
        Assert.IsNull(Utm.Parse("61N 1 2"));
        Assert.IsNull(Utm.Parse("55S east north"));
    }

    [TestMethod]
    public async Task MenuActionsEditTheMission()
    {
        var services = FakeUi.Services(out var ui);
        var vm = new FlightPlanViewModel(services);
        vm.Store.Mission.Clear();
        vm.Store.Mission.Add(vm.Store.NewWaypoint(-35.1, 149.1));
        vm.Store.Mission.Add(vm.Store.NewWaypoint(-35.2, 149.2));
        vm.Store.Mission.Add(new MissionItem { Command = MAVLink.MAV_CMD.LAND, Lat = -35.3, Lng = 149.3, Alt = 0 });

        var before = vm.Store.Mission[0].Alt;
        ui.PromptAnswer = "10";
        await vm.ModifyAltitudesAsync();
        Assert.AreEqual(before + 10, vm.Store.Mission[0].Alt, 1e-4);
        Assert.AreEqual(0f, vm.Store.Mission[2].Alt, "a landing at 0 stays on the ground");

        vm.ReverseWaypoints();
        Assert.AreEqual(MAVLink.MAV_CMD.LAND, vm.Store.Mission[0].Command);

        ui.PromptAnswer = "2";
        await vm.AddJumpAsync(toStart: false);
        var jump = vm.Store.Mission.Last();
        Assert.AreEqual((MAVLink.MAV_CMD.DO_JUMP, 2f, 2f), (jump.Command, jump.P1, jump.P2));

        vm.AddRoiHere(new PointLatLng(-35.5, 149.5));
        Assert.AreEqual(MAVLink.MAV_CMD.DO_SET_ROI, vm.Store.Mission.Last().Command);

        ui.PromptAnswer = "55S 694000 6084000";
        await vm.AddUtmWaypointAsync();
        var utm = vm.Store.Mission.Last();
        Assert.AreEqual(-35.36, utm.Lat, 0.05);
        Assert.AreEqual(149.16, utm.Lng, 0.05);

        await vm.MeasureDistanceAsync(new PointLatLng(-35, 149));
        Assert.IsTrue(vm.IsMeasuring);
        await vm.MeasureDistanceAsync(new PointLatLng(-35.001, 149));
        Assert.IsFalse(vm.IsMeasuring);
        StringAssert.Contains(vm.Status, "bearing 180");
        vm.Store.Mission.Clear();
    }
}
