using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.ViewModels.SetupScreens;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;
using MissionPlanner.App.Tests.ConfigScreens;

namespace MissionPlanner.App.Tests.SetupScreens.OptionalHardware;

/// <summary>The Optional Hardware pages against a parameter store in memory.</summary>
[TestClass]
public sealed class OptionalHardwarePageTests
{
    private static (AppServices Services, FakeDialogs Dialogs, FakeParamSource Source) Create(params (string Name, double Value)[] values)
    {
        var (services, dialogs, _) = ParamTestSupport.CreateServices();
        var src = new FakeParamSource();
        foreach (var (n, v) in values)
            src.Values[n] = v;
        return (services, dialogs, src);
    }

    private static void AddServos(FakeParamSource src, int count = 16)
    {
        for (var n = 1; n <= count; n++)
        {
            src.Values[$"SERVO{n}_FUNCTION"] = 0;
            src.Values[$"SERVO{n}_MIN"] = 1100;
            src.Values[$"SERVO{n}_MAX"] = 1900;
            src.Values[$"SERVO{n}_REVERSED"] = 0;
        }
    }

    [TestMethod]
    public void Rangefinder_ListsInstancesWithTheirSettings_AndSharedOnes()
    {
        var (services, _, src) = Create(
            ("RNGFND1_TYPE", 100), ("RNGFND1_ORIENT", 25), ("RNGFND1_MIN", 0.2), ("RNGFND1_MAX", 7), ("RNGFND1_WSP_AVG", 4),
            ("RNGFND2_TYPE", 0), ("RNGFND2_ORIENT", 25),
            ("RNGFND_FILT", 0.5));
        var vm = new RangefinderViewModel(services, src);
        vm.OnShown();

        Assert.HasCount(2, vm.Instances);
        var first = vm.Instances[0];
        Assert.AreSame(first, vm.Selected, "the first fitted sensor is shown");
        Assert.IsTrue(first.IsEnabled);
        Assert.IsFalse(vm.Instances[1].IsEnabled);
        CollectionAssert.AreEqual(new[] { "RNGFND1_ORIENT", "RNGFND1_MIN", "RNGFND1_MAX" }, first.Main.Select(f => f.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "RNGFND1_WSP_AVG" }, first.Rest.Select(f => f.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "RNGFND_FILT" }, vm.General.Select(f => f.Name).ToArray());

        Assert.AreEqual("no reading", RangefinderViewModel.ReadingFor(new CurrentState(), 1));
        var cs = new CurrentState { rangefinder2 = 123 };
        Assert.AreEqual("1.23 m", RangefinderViewModel.ReadingFor(cs, 2));
    }

    [TestMethod]
    public async Task Rangefinder_NewType_AsksForAReboot()
    {
        var (services, _, src) = Create(("RNGFND1_TYPE", 0));
        var vm = new RangefinderViewModel(services, src);
        vm.OnShown();
        await vm.Instances[0].Type!.WriteAsync(100);
        Assert.IsTrue(vm.TypeChanged);
        Assert.IsTrue(vm.RebootRequired);
        Assert.IsTrue(vm.Instances[0].IsEnabled);
    }

    [TestMethod]
    public void Airspeed_SplitsSensorAndLibrarySettings_AndOffersMissionPlannersPins()
    {
        var (services, _, src) = Create(
            ("ARSPD_TYPE", 100), ("ARSPD_USE", 1), ("ARSPD_PIN", 15), ("ARSPD_RATIO", 2), ("ARSPD_ENABLE", 1),
            ("ARSPD_PRIMARY", 0), ("ARSPD_WIND_MAX", 0), ("ARSPD2_TYPE", 0), ("ARSPD2_USE", 0));
        var vm = new AirspeedViewModel(services, src);
        vm.OnShown();

        Assert.HasCount(2, vm.Sensors);
        Assert.IsTrue(vm.HasSeveralSensors);
        var s1 = vm.Sensors[0];
        CollectionAssert.AreEqual(new[] { "ARSPD_USE", "ARSPD_PIN", "ARSPD_RATIO" }, s1.Main.Select(f => f.Name).ToArray());
        Assert.AreEqual("ARSPD_ENABLE", vm.Enable!.Name);
        CollectionAssert.AreEqual(new[] { "ARSPD_PRIMARY", "ARSPD_WIND_MAX" }, vm.General.Select(f => f.Name).ToArray());

        var pin = s1.Main.Single(f => f.Name == "ARSPD_PIN");
        Assert.IsTrue(pin.IsOptions, "no documentation: Mission Planner's pin list");
        Assert.AreEqual("Pixhawk Analog AS Port", pin.SelectedOption!.Label);
    }

    [TestMethod]
    public async Task OpticalFlow_ShowsYawInDegrees_AndLegacyEnable()
    {
        var (services, _, src) = Create(("FLOW_TYPE", 10), ("FLOW_ORIENT_YAW", 4500), ("FLOW_FXSCALER", 0), ("FLOW_POS_X", 0.1), ("FLOW_ADDR", 0));
        var vm = new OpticalFlowViewModel(services, src);
        vm.OnShown();
        Assert.IsTrue(vm.IsEnabled);
        Assert.IsFalse(vm.IsLegacy);
        Assert.AreEqual(45m, vm.Yaw!.Value);
        Assert.IsNull(vm.HeightOverride, "Rover only");
        CollectionAssert.AreEqual(new[] { "FLOW_ADDR" }, vm.Other.Select(f => f.Name).ToArray());

        await vm.Type!.WriteAsync(0);
        Assert.IsFalse(vm.IsEnabled);
        Assert.IsTrue(vm.RebootRequired);

        var (s2, _, legacy) = Create(("FLOW_ENABLE", 1));
        var old = new OpticalFlowViewModel(s2, legacy);
        old.OnShown();
        Assert.IsTrue(old.IsLegacy);
        Assert.IsTrue(old.IsEnabled);
        Assert.IsTrue(old.Enable!.IsToggle);
    }

    [TestMethod]
    public void Parachute_PicksTheServoOutput_AndClearsTheOldOne()
    {
        var (services, _, src) = Create(("CHUTE_ENABLED", 1), ("CHUTE_TYPE", 10), ("CHUTE_SERVO_ON", 1900), ("CHUTE_SERVO_OFF", 1100),
            ("CHUTE_ALT_MIN", 10), ("CHUTE_DELAY_MS", 500));
        AddServos(src);
        src.Values["SERVO9_FUNCTION"] = 27;
        var vm = new ParachuteViewModel(services, src);
        vm.OnShown();

        Assert.IsTrue(vm.IsEnabled);
        Assert.IsTrue(vm.IsServo);
        Assert.AreEqual("Servo", vm.Type!.SelectedOption!.Label, "Mission Planner's CHUTE_TYPE list without documentation");
        Assert.AreEqual("SERVO9", vm.Output!.Channel);
        CollectionAssert.AreEqual(new[] { "CHUTE_DELAY_MS" }, vm.Other.Select(f => f.Name).ToArray());
        Assert.IsEmpty(src.Writes);

        vm.Output.Selected = vm.Output.Options.Single(o => o.Channel == "SERVO11");
        CollectionAssert.AreEqual(new[] { ("SERVO9_FUNCTION", 0.0), ("SERVO11_FUNCTION", 27.0) }, src.Writes);
        Assert.AreEqual("SERVO11", vm.Output.Channel);
    }

    [TestMethod]
    public void Mount_BuildsAxes_AndGivingAnAxisAnOutputShowsThatServo()
    {
        var (services, _, src) = Create(
            ("MNT1_TYPE", 1), ("MNT1_DEFLT_MODE", 3), ("MNT1_RC_RATE", 0), ("MNT1_OPTIONS", 0),
            ("MNT1_ROLL_MIN", -30), ("MNT1_ROLL_MAX", 30), ("MNT1_PITCH_MIN", -90), ("MNT1_PITCH_MAX", 20),
            ("MNT1_YAW_MIN", -180), ("MNT1_YAW_MAX", 180), ("MNT1_NEUTRAL_X", 0), ("MNT1_NEUTRAL_Y", 0), ("MNT1_NEUTRAL_Z", 0),
            ("MNT1_RETRACT_X", 0), ("MNT1_RETRACT_Y", 0), ("MNT1_RETRACT_Z", 0), ("MNT1_LEAD_RLL", 0),
            ("MNT2_TYPE", 0),
            ("CAM1_TYPE", 1), ("CAM1_SERVO_ON", 1900), ("CAM1_SERVO_OFF", 1100), ("CAM1_DURATION", 0.1));
        AddServos(src);
        for (var n = 1; n <= 16; n++)
            src.Values[$"RC{n}_OPTION"] = 0;
        src.Values["RC6_OPTION"] = 213;
        var vm = new MountViewModel(services, src);
        vm.OnShown();

        Assert.HasCount(2, vm.Mounts);
        Assert.IsTrue(vm.HasSeveral);
        var m = vm.Selected!;
        Assert.IsTrue(m.IsFitted);
        Assert.IsTrue(m.UsesServos);
        Assert.HasCount(3, m.Axes);
        var pitch = m.Axes[1];
        Assert.AreEqual(-90m, pitch.Min!.Value);
        Assert.AreEqual(20m, pitch.Max!.Value);
        Assert.AreEqual("RC6", pitch.Input!.Channel);
        Assert.IsNull(pitch.Output!.Channel);
        Assert.IsFalse(pitch.HasServo);
        CollectionAssert.AreEqual(new[] { "MNT1_LEAD_RLL" }, m.Other.Select(f => f.Name).ToArray());
        Assert.IsTrue(vm.HasCamera);
        Assert.IsTrue(vm.IsServoShutter);

        pitch.Output.Selected = pitch.Output.Options.Single(o => o.Channel == "SERVO10");
        CollectionAssert.AreEqual(new[] { ("SERVO10_FUNCTION", 7.0) }, src.Writes);

        // the page rebuilt: the pitch axis now edits SERVO10's range and direction
        var rebuilt = vm.Selected!.Axes[1];
        Assert.AreEqual("SERVO10", rebuilt.Output!.Channel);
        Assert.AreEqual("SERVO10_MIN", rebuilt.ServoMin!.Name);
        Assert.AreEqual("SERVO10_REVERSED", rebuilt.ServoReversed!.Name);

        // RC input: RC7 takes Mount1 Pitch from RC6
        src.Writes.Clear();
        rebuilt.Input!.Selected = rebuilt.Input.Options.Single(o => o.Channel == "RC7");
        CollectionAssert.AreEqual(new[] { ("RC6_OPTION", 0.0), ("RC7_OPTION", 213.0) }, src.Writes);
    }

    [TestMethod]
    public void Mount_OldSingleMount_ShowsCentidegreesAsDegrees()
    {
        var (services, _, src) = Create(("MNT_TYPE", 1), ("MNT_ANGMIN_TIL", -4500), ("MNT_ANGMAX_TIL", 0), ("MNT_RC_IN_TILT", 6),
            ("MNT_STAB_TILT", 1));
        AddServos(src, 8);
        var vm = new MountViewModel(services, src);
        vm.OnShown();
        var pitch = vm.Selected!.Axes[1];
        Assert.AreEqual(-45m, pitch.Min!.Value);
        Assert.AreEqual("MNT_RC_IN_TILT", pitch.InputField!.Name);
        Assert.IsTrue(pitch.Stabilise!.IsOn);
        Assert.IsNull(pitch.Input);
    }

    [TestMethod]
    public async Task Adsb_HoldsEditsUntilWrite()
    {
        var (services, dialogs, src) = Create(("ADSB_TYPE", 1), ("ADSB_ICAO_ID", 0), ("ADSB_SQUAWK", 1200), ("AVD_ENABLE", 0), ("AVD_F_ACTION", 2));
        var vm = new AdsbSetupViewModel(services, src);
        vm.OnShown();

        CollectionAssert.AreEqual(new[] { "ADS-B receiver / transponder", "Avoidance" }, vm.Groups.Select(g => g.Title).ToArray());
        Assert.AreEqual("ADSB_TYPE", vm.Groups[0].Fields[0].Name, "the switch first");

        var squawk = vm.Groups[0].Fields.Single(f => f.Name == "ADSB_SQUAWK");
        squawk.NumberValue = 7000;
        Assert.AreEqual(1, vm.ChangeCount);
        Assert.IsEmpty(src.Writes, "nothing written before Write Params");

        vm.Filter = "avd";
        Assert.AreEqual("Avoidance", vm.Groups.Single().Title);

        await vm.WriteCommand.ExecuteAsync(null);
        CollectionAssert.AreEqual(new[] { ("ADSB_SQUAWK", 7000.0) }, src.Writes);
        Assert.AreEqual(0, vm.ChangeCount);

        await vm.WriteCommand.ExecuteAsync(null);
        Assert.AreEqual("No changes to write.", dialogs.Messages.Last().Message);
    }

    [TestMethod]
    public void HardwareId_ListsSetIdsByDefault()
    {
        var (services, _, src) = Create(("COMPASS_DEV_ID", 97539), ("COMPASS_DEV_ID2", 0), ("INS_ACC_ID", 2753028), ("SERIAL1_BAUD", 57));
        var vm = new HardwareIdViewModel(services, src);
        vm.OnShown();
        CollectionAssert.AreEqual(new[] { "COMPASS_DEV_ID", "INS_ACC_ID" }, vm.Rows.Select(r => r.ParamName).ToArray());
        vm.ShowUnset = true;
        Assert.HasCount(3, vm.Rows);
        vm.Filter = "ins";
        Assert.AreEqual("INS_ACC_ID", vm.Rows.Single().ParamName);
        Assert.AreEqual("2 device ids set, 1 unset", vm.Summary);
    }

    [TestMethod]
    public void InitialParams_PreviewsAgainstTheVehicle_AndInvalidatesOnInputChange()
    {
        var (services, _, src) = Create(("ATC_ACCEL_P_MAX", 110000), ("ATC_ACCEL_R_MAX", 110000), ("ATC_ACCEL_Y_MAX", 27000),
            ("ATC_RAT_PIT_FLTD", 20), ("INS_GYRO_FILTER", 46), ("BATT_ARM_VOLT", 0), ("MOT_THST_EXPO", 0.65));
        var vm = new InitialParamsViewModel(services, src);
        vm.OnShown();
        Assert.AreEqual(9m, vm.PropInches);
        Assert.AreEqual(4.2m, vm.CellMax);

        vm.CalculateCommand.Execute(null);
        Assert.IsTrue(vm.HasPreview);
        var gyro = vm.Rows.Single(r => r.Name == "INS_GYRO_FILTER");
        Assert.IsFalse(gyro.IsChange, "already 46");
        Assert.IsFalse(gyro.Include);
        var expo = vm.Rows.SingleOrDefault(r => r.Name == "MOT_THST_EXPO");
        Assert.IsNotNull(expo, string.Join(",", vm.Rows.Select(r => r.Name)) + " fw " + services.Session.Cs.firmware);
        Assert.IsTrue(expo.IsChange);
        Assert.IsTrue(expo.Include);
        Assert.AreEqual("0.65", expo.CurrentText);
        var acro = vm.Rows.Single(r => r.Name == "ACRO_YAW_P");
        Assert.IsFalse(acro.OnVehicle);
        Assert.AreEqual("not on this vehicle", acro.Note);

        vm.Chemistry = Core.ViewModels.SetupScreens.OptionalHardware.BatteryChemistry.LiIon;
        Assert.AreEqual(4.1m, vm.CellMax);
        Assert.AreEqual(2.8m, vm.CellMin);
        Assert.IsFalse(vm.HasPreview, "a preview for other inputs is dropped");

        vm.PropInches = 0;
        vm.CalculateCommand.Execute(null);
        Assert.IsFalse(vm.HasPreview);
        Assert.AreEqual("Prop size must be larger than zero.", vm.InputError);
    }

    [TestMethod]
    public void Osd_BuildsScreensAndEditsTheLayout()
    {
        var (services, dialogs, src) = Create(
            ("OSD_TYPE", 1), ("OSD_UNITS", 0), ("SR0_EXTRA1", 4),
            ("OSD1_ENABLE", 1), ("OSD1_TXT_RES", 1),
            ("OSD1_ALTITUDE_EN", 1), ("OSD1_ALTITUDE_X", 23), ("OSD1_ALTITUDE_Y", 8),
            ("OSD1_BAT_VOLT_EN", 0), ("OSD1_BAT_VOLT_X", 24), ("OSD1_BAT_VOLT_Y", 1),
            ("OSD2_ENABLE", 0), ("OSD2_ALTITUDE_EN", 0), ("OSD2_ALTITUDE_X", 0), ("OSD2_ALTITUDE_Y", 0));
        var vm = new OsdViewModel(services, src);
        vm.OnShown();

        Assert.AreEqual("OSD_TYPE", vm.OsdType!.Name);
        CollectionAssert.AreEqual(new[] { "OSD_UNITS" }, vm.General.Select(f => f.Name).ToArray());
        Assert.HasCount(2, vm.Screens);
        Assert.IsTrue(vm.HasTelemetryRates);
        var s1 = vm.SelectedScreen!;
        Assert.AreEqual(1, s1.Number);
        CollectionAssert.AreEqual(new[] { "OSD1_ENABLE", "OSD1_TXT_RES" }, s1.Settings.Select(f => f.Name).ToArray());
        Assert.AreEqual(Core.ViewModels.SetupScreens.OptionalHardware.OsdGrid.Hd50, vm.SelectedGrid!.Grid, "the screen's text resolution");
        Assert.AreEqual(1, s1.EnabledCount);

        var alt = s1.Items[0];
        vm.SelectedItem = alt;
        Assert.IsTrue(alt.IsSelected);
        var version = s1.LayoutVersion;
        vm.Move(alt, 70, 3);
        Assert.AreEqual(49, alt.X, "kept on the 50-column grid");
        Assert.AreEqual(3, alt.Y);
        Assert.IsGreaterThan(version, s1.LayoutVersion);
        Assert.AreEqual(1, vm.ChangeCount);

        vm.CopyLayoutCommand.Execute(null);
        vm.SelectedScreen = vm.Screens[1];
        Assert.IsFalse(alt.IsSelected, "changing screen clears the selection");
        Assert.AreEqual(Core.ViewModels.SetupScreens.OptionalHardware.OsdGrid.Sd, vm.SelectedGrid!.Grid);
        vm.PasteLayoutCommand.Execute(null);
        var alt2 = vm.Screens[1].Items[0];
        Assert.IsTrue(alt2.Enabled);
        Assert.AreEqual(49, alt2.X);
        Assert.AreEqual(2, vm.ChangeCount);

        vm.ClearAllCommand.Execute(null);
        Assert.IsFalse(alt2.Enabled);
        Assert.AreEqual(2, vm.ChangeCount, "OSD2 ALTITUDE moved but is off again");
        Assert.IsEmpty(src.Writes, "layout edits wait for Write");
        Assert.IsEmpty(dialogs.Messages);
    }

    [TestMethod]
    public async Task Osd_EnableTelemetry_SetsTheStreamRatesThatExist()
    {
        var (services, _, src) = Create(("OSD_TYPE", 0), ("SR0_EXTRA1", 4), ("SR0_POSITION", 2), ("SR1_EXTRA1", 4));
        var vm = new OsdViewModel(services, src);
        vm.OnShown();
        await vm.EnableTelemetryCommand.ExecuteAsync(null);
        CollectionAssert.AreEquivalent(new[] { ("SR0_EXTRA1", 2.0), ("SR0_POSITION", 2.0), ("SR1_EXTRA1", 2.0) }, src.Writes);
    }
}
