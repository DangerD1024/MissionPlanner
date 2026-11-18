using MissionPlanner.App.Core.ViewModels.FlightData;
using MissionPlanner.ArduPilot;

namespace MissionPlanner.App.Tests.FlightData;

[TestClass]
public class FlightActionTests
{
    [TestMethod]
    public void NullModeTableGivesNoModes()
    {
        Assert.IsEmpty(FlightModes.For(Firmwares.ArduSub, _ => null));
        Assert.IsEmpty(FlightModes.Names(null));
    }

    [TestMethod]
    public void ArduSubHasNoModeTableInTheLibrary()
    {
        // getModesList returns null for ArduSub; the combo must just be empty
        Assert.IsEmpty(FlightModes.For(Firmwares.ArduSub));
    }

    [TestMethod]
    public void FailingModeTableGivesNoModes() =>
        Assert.IsEmpty(FlightModes.For(Firmwares.ArduCopter2, _ => throw new InvalidOperationException("no metadata")));

    [TestMethod]
    public void ModeNamesKeepOrderAndDropBlanksAndDuplicates()
    {
        var table = new List<KeyValuePair<int, string>>
        {
            new(0, "Stabilize"), new(3, "Auto"), new(4, ""), new(5, "Loiter"), new(6, "RTL"), new(7, "auto"), new(8, null!),
        };
        CollectionAssert.AreEqual(new[] { "Stabilize", "Auto", "Loiter", "RTL" }, FlightModes.For(Firmwares.ArduCopter2, _ => table).ToArray());
    }

    [TestMethod]
    public void ModeLookupIgnoresCase()
    {
        IReadOnlyList<string> modes = ["Stabilize", "AUTO", "Loiter", "RTL"];
        Assert.AreEqual("AUTO", FlightModes.Find(modes, "Auto"));
        Assert.AreEqual("RTL", FlightModes.Find(modes, "rtl"));
        Assert.IsNull(FlightModes.Find(modes, "Guided"));
        Assert.IsNull(FlightModes.Find([], "Auto"));
    }

    [TestMethod]
    public void TrackerTableComesWithoutMetadata()
    {
        // hardcoded in the library, so it works in tests without parameter metadata
        var modes = FlightModes.For(Firmwares.ArduTracker);
        Assert.IsNotEmpty(modes);
        Assert.IsNotNull(FlightModes.Find(modes, "Auto"));
    }

    [TestMethod]
    public void ActionsListMatchesMissionPlannersOrder()
    {
        var names = FlightActions.Options.Select(o => o.Action.ToString()).ToArray();
        Assert.AreEqual("Loiter_Unlim", names[0]);
        Assert.AreEqual("Reboot_Mass_Storage", names[^1]);
        Assert.HasCount(20, names);
        Assert.AreEqual("Return To Launch", FlightActions.Options[1].Name, "shown with spaces");
    }

    [TestMethod]
    public void GenericActionsMapToTheirCommands()
    {
        Assert.AreEqual(MAVLink.MAV_CMD.LOITER_UNLIM, FlightActions.GenericCommand(FlightAction.Loiter_Unlim, Firmwares.ArduCopter2)!.Value.Command);
        Assert.AreEqual(MAVLink.MAV_CMD.RETURN_TO_LAUNCH, FlightActions.GenericCommand(FlightAction.Return_To_Launch, Firmwares.ArduCopter2)!.Value.Command);
        Assert.AreEqual(MAVLink.MAV_CMD.MISSION_START, FlightActions.GenericCommand(FlightAction.Mission_Start, Firmwares.ArduCopter2)!.Value.Command);
        Assert.AreEqual(MAVLink.MAV_CMD.BATTERY_RESET, FlightActions.GenericCommand(FlightAction.Battery_Reset, Firmwares.ArduCopter2)!.Value.Command);
        // the WinForms name lookup misses this one
        Assert.AreEqual(MAVLink.MAV_CMD.DO_ADSB_OUT_IDENT, FlightActions.GenericCommand(FlightAction.ADSB_Out_Ident, Firmwares.ArduPlane)!.Value.Command);
    }

    [TestMethod]
    public void EveryActionHasACall()
    {
        foreach (var a in Enum.GetValues<FlightAction>())
        {
            if (!FlightActions.HasDedicatedCall(a))
                Assert.IsNotNull(FlightActions.GenericCommand(a, Firmwares.ArduCopter2), a.ToString());
            else
                Assert.IsNull(FlightActions.GenericCommand(a, Firmwares.ArduCopter2), a + " has its own call");
        }
    }

    [TestMethod]
    public void CalibrationAndBatteryResetPresets()
    {
        var copter = FlightActions.GenericCommand(FlightAction.Preflight_Calibration, Firmwares.ArduCopter2)!.Value;
        Assert.AreEqual(1f, copter.P1, "gyro on copter");
        Assert.AreEqual(1f, copter.P3, "baro");

        var plane = FlightActions.GenericCommand(FlightAction.Preflight_Calibration, Firmwares.ArduPlane)!.Value;
        Assert.AreEqual(0f, plane.P1);
        Assert.AreEqual(1f, plane.P3);

        var batt = FlightActions.GenericCommand(FlightAction.Battery_Reset, Firmwares.ArduRover)!.Value;
        Assert.AreEqual(255f, batt.P1, "all batteries");
        Assert.AreEqual(100f, batt.P2, "to 100 %");
    }

    [TestMethod]
    public void NumbersParseWithEitherDecimalMark()
    {
        Assert.IsTrue(FlightDataViewModel.TryParseNumber("12.5", out var a));
        Assert.AreEqual(12.5, a);
        Assert.IsTrue(FlightDataViewModel.TryParseNumber(" 12,5 ", out var b));
        Assert.AreEqual(12.5, b);
        Assert.IsTrue(FlightDataViewModel.TryParseNumber("-3", out var c));
        Assert.AreEqual(-3, c);
        Assert.IsFalse(FlightDataViewModel.TryParseNumber("ten", out _));
        Assert.IsFalse(FlightDataViewModel.TryParseNumber("", out _));
        Assert.IsFalse(FlightDataViewModel.TryParseNumber("NaN", out _));
    }
}
