using System.Net;
using System.Net.Sockets;
using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.Maps;
using MissionPlanner.App.Core.ViewModels.FlightData;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Tests.FlightData;

/// <summary>HUD options, and the EKF, vibration and pre-arm windows the HUD opens.</summary>
[TestClass]
public class HudExtrasTests
{
    [ClassInitialize]
    public static void Init(TestContext _) =>
        MapTiles.Initialize(Path.Combine(TestEnvironment.DataRoot, "gmapcache") + Path.DirectorySeparatorChar);

    [TestMethod]
    public void OptionsDialogSavesGroundCellsAndUserItems_AndTheHudShowsThem()
    {
        try
        {
            var dialog = new HudOptionsViewModel();
            dialog.SelectedGround = dialog.Grounds.Single(g => g.Name == "Brown");
            dialog.CellCount = 4;
            dialog.Filter = "satcount";
            dialog.SelectedField = dialog.Fields.First(f => f.Name == "satcount");
            dialog.AddItemCommand.Execute(null);
            dialog.AddItemCommand.Execute(null); // not twice
            Assert.HasCount(1, dialog.Items);
            var closed = false;
            dialog.CloseRequested += (_, ok) => closed = ok;
            dialog.SaveCommand.Execute(null);
            Assert.IsTrue(closed);

            Assert.AreEqual("#8a5a2b", HudOptions.GroundColor);
            Assert.AreEqual(4, HudOptions.CellCount);
            CollectionAssert.AreEqual(new[] { "satcount" }, HudOptions.UserItems.ToArray());
            Assert.AreEqual(4.1, HudOptions.PerCell(16.4, 4)!.Value, 1e-9);
            Assert.IsNull(HudOptions.PerCell(16.4, 0));

            var services = FakeUi.Services(out _);
            services.Session.Cs.battery_voltage = 16.4;
            services.Session.Cs.satcount = 12;
            var vm = new FlightDataViewModel(services);
            vm.Tick();
            Assert.AreEqual(4.1, vm.Hud.CellVoltage!.Value, 1e-6);
            Assert.AreEqual("#8a5a2b", vm.Hud.GroundColor);
            Assert.HasCount(1, vm.Hud.UserLines);
            StringAssert.Contains(vm.Hud.UserLines[0], "12.00");
        }
        finally
        {
            HudOptions.GroundColor = "";
            HudOptions.CellCount = 0;
            HudOptions.UserItems = [];
        }
    }

    [TestMethod]
    public void EkfAndVibrationWindowsGradeTheValues()
    {
        var services = FakeUi.Services(out _);
        var cs = services.Session.Cs;
        cs.ekfvelv = 0.2f;
        cs.ekfposhor = 0.6f;
        cs.ekfcompv = 0.9f;
        cs.ekfflags = (int)(MAVLink.EKF_STATUS_FLAGS.EKF_ATTITUDE | MAVLink.EKF_STATUS_FLAGS.EKF_PRED_POS_HORIZ_ABS);
        var ekf = new EkfStatusViewModel(services);
        ekf.OnShown();
        Assert.AreEqual(0, ekf.Bars[0].Level);
        Assert.IsTrue(ekf.Bars[1].IsWarning);
        Assert.IsTrue(ekf.Bars[3].IsBad);
        CollectionAssert.AreEqual(new[] { "attitude", "pred pos horiz abs" }, ekf.Flags.ToArray());

        cs.vibex = 10;
        cs.vibez = 75;
        cs.vibeclip0 = 3;
        var vibe = new VibrationViewModel(services);
        vibe.OnShown();
        Assert.AreEqual(0, vibe.Bars[0].Level);
        Assert.IsTrue(vibe.Bars[2].IsBad);
        StringAssert.Contains(vibe.Clipping, "IMU1 3");
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task PrearmWindowAsksTheVehicleAndListsItsReasons()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync();
        var services = FakeUi.Services(out _);
        var connecting = services.Session.ConnectAsync(LinkSpec.Tcp("127.0.0.1", port), getParams: false);
        using var vehicleSide = await accept;
        listener.Stop();
        await using var vehicle = new ExtrasFakeVehicle(vehicleSide.GetStream());
        await connecting;
        try
        {
            var vm = new PrearmViewModel(services);
            var run = vm.RunCommand.ExecuteAsync(null);
            await Task.Delay(300);
            services.Session.Cs.messages.Add((DateTime.Now, "PreArm: Compass not calibrated"));
            services.Session.Cs.messages.Add((DateTime.Now, "Something else"));
            await run;

            Assert.IsTrue(vehicle.Commands.Any(c => c.command == (ushort)MAVLink.MAV_CMD.RUN_PREARM_CHECKS));
            CollectionAssert.AreEqual(new[] { "PreArm: Compass not calibrated" }, vm.Problems.ToArray());
            Assert.AreEqual("Not ready to arm:", vm.Status);
        }
        finally
        {
            await services.Session.DisconnectAsync();
        }
    }
}
