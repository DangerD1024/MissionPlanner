using GMap.NET;
using MissionPlanner.App.Core.ViewModels.FlightPlan;
using MissionPlanner.App.Tests.FlightData;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Tests.FlightPlan;

[TestClass]
public class SurveyCameraTests
{
    [TestMethod]
    public void TheBuiltInCameraListIsCompiledIn()
    {
        Assert.IsGreaterThan(20, CameraDatabase.All.Count);
        var nex = CameraDatabase.All.Single(c => c.Name == "Nex5 16mm");
        Assert.AreEqual((16.0, 4592, 23.4), (nex.FocalMm, nex.ImageWidth, nex.SensorWidthMm));
    }

    [TestMethod]
    public void FootprintGsdLanesAndTriggers()
    {
        var nex = CameraDatabase.All.Single(c => c.Name == "Nex5 16mm");
        var (across, along) = SurveyCameraMath.Footprint(nex, 100, portrait: false);
        Assert.AreEqual(146.25, across, 1e-9, "23.4 mm x 100 m / 16 mm");
        Assert.AreEqual(97.5, along, 1e-9);
        Assert.AreEqual(3.185, SurveyCameraMath.GsdCm(nex, 100), 0.001);
        Assert.AreEqual(58.5, SurveyCameraMath.LaneSpacing(nex, 100, 60, false), 1e-9);
        Assert.AreEqual(29.25, SurveyCameraMath.TriggerDistance(nex, 100, 70, false), 1e-9);
        Assert.AreEqual(97.5 * 0.4, SurveyCameraMath.LaneSpacing(nex, 100, 60, portrait: true), 1e-9, "portrait swaps the sides");

        var corners = SurveyCameraMath.Corners(new PointLatLng(-35, 149), across, along, 0);
        var d = new PointLatLngAlt(corners[0].Lat, corners[0].Lng).GetDistance(new PointLatLngAlt(corners[1].Lat, corners[1].Lng));
        Assert.AreEqual(146.25, d, 0.5, "front edge across the lane");
    }

    [TestMethod]
    public void ChoosingACameraSetsLaneSpacingAndTriggerDistance()
    {
        var services = FakeUi.Services(out _);
        var vm = new SurveyGridViewModel(services);
        vm.Polygon.AddRange([new PointLatLngAlt(-35, 149), new PointLatLngAlt(-35, 149.01), new PointLatLngAlt(-35.01, 149.01)]);
        vm.OnShown();
        try
        {
            vm.Altitude = 100;
            vm.Sidelap = 60;
            vm.Overlap = 70;
            vm.Portrait = false;
            vm.SelectedCamera = vm.Cameras.Single(c => c.Camera?.Name == "Nex5 16mm");
            Assert.AreEqual(58.5m, vm.LaneSpacing);
            Assert.AreEqual(29.25m, vm.TriggerDistance);
            StringAssert.StartsWith(vm.CameraText, "GSD 3.18 cm/px");
            vm.Altitude = 50;
            Assert.AreEqual(29.25m, vm.LaneSpacing, "follows the altitude");
        }
        finally
        {
            vm.SelectedCamera = vm.Cameras[0];
        }
    }
}
