using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;
using MissionPlanner.ArduPilot;

namespace MissionPlanner.App.Tests.SetupScreens;

[TestClass]
public class HardwareSetupFlightModeTests
{
    [TestMethod]
    [DataRow(1000.0, 0)]
    [DataRow(1230.0, 0)]
    [DataRow(1231.0, 1)]
    [DataRow(1360.0, 1)]
    [DataRow(1361.0, 2)]
    [DataRow(1490.0, 2)]
    [DataRow(1491.0, 3)]
    [DataRow(1620.0, 3)]
    [DataRow(1621.0, 4)]
    [DataRow(1749.0, 4)]
    [DataRow(1750.0, 5)]
    [DataRow(2000.0, 5)]
    [DataRow(2199.0, 5)]
    public void PwmSelectsMissionPlannersSlots(double pwm, int slot) =>
        Assert.AreEqual(slot, FlightModeLogic.PwmToSlot(pwm));

    [TestMethod]
    [DataRow(0.0)]
    [DataRow(800.0)]
    [DataRow(2200.0)]
    [DataRow(65535.0)]
    public void NoSignalSelectsNoSlot(double pwm) => Assert.AreEqual(-1, FlightModeLogic.PwmToSlot(pwm));

    [TestMethod]
    public void SlotBandsCoverEverySlot()
    {
        Assert.HasCount(FlightModeLogic.SlotCount, FlightModeLogic.BandText);
        Assert.AreEqual("PWM 0 - 1230", FlightModeLogic.BandText[0]);
        Assert.AreEqual("PWM 1750 +", FlightModeLogic.BandText[5]);
    }

    [TestMethod]
    public void ModeParametersFollowTheVehicle()
    {
        Assert.AreEqual("FLTMODE", FlightModeLogic.ModeParamPrefix(n => n == "FLTMODE1"));
        Assert.AreEqual("MODE", FlightModeLogic.ModeParamPrefix(n => n == "MODE1"));
        Assert.AreEqual("COM_FLTMODE", FlightModeLogic.ModeParamPrefix(n => n == "COM_FLTMODE1"));
        Assert.IsNull(FlightModeLogic.ModeParamPrefix(_ => false));
        Assert.AreEqual("FLTMODE_CH", FlightModeLogic.SwitchChannelParam(n => n is "FLTMODE_CH" or "MODE_CH"));
        Assert.AreEqual("MODE_CH", FlightModeLogic.SwitchChannelParam(n => n == "MODE_CH"));
    }

    [TestMethod]
    public void SimpleBitsAreSlots()
    {
        Assert.AreEqual(1 + 4 + 32, FlightModeLogic.SlotMask([true, false, true, false, false, true]));
        Assert.IsTrue(FlightModeLogic.SlotBit(37, 0));
        Assert.IsFalse(FlightModeLogic.SlotBit(37, 1));
        Assert.IsTrue(FlightModeLogic.SlotBit(37, 5));
    }

    [TestMethod]
    public void SimpleOnlyForPositionModes()
    {
        Assert.IsTrue(FlightModeLogic.SupportsSimple("Stabilize"));
        Assert.IsTrue(FlightModeLogic.SupportsSimple("PosHold"));
        Assert.IsTrue(FlightModeLogic.SupportsSimple("Smart_RTL"));
        Assert.IsFalse(FlightModeLogic.SupportsSimple("Acro"));
        Assert.IsFalse(FlightModeLogic.SupportsSimple("Guided"));
        Assert.IsFalse(FlightModeLogic.SupportsSimple(null));
    }

    [TestMethod]
    public void PlaneModesGainInitialising()
    {
        var documented = new List<KeyValuePair<double, string>> { new(0, "Manual"), new(10, "Auto") };
        var plane = FlightModeLogic.Modes(Firmwares.ArduPlane, documented);
        Assert.HasCount(3, plane);
        Assert.AreEqual("INITIALISING", plane.Single(m => m.Value == 16).Label);

        var copter = FlightModeLogic.Modes(Firmwares.ArduCopter2, documented);
        Assert.HasCount(2, copter);
        Assert.IsEmpty(FlightModeLogic.Modes(Firmwares.ArduCopter2, null));
        Assert.IsNotEmpty(FlightModeLogic.Modes(Firmwares.ArduTracker, null));
    }
}

[TestClass]
public class HardwareSetupMotorLayoutTests
{
    private const string Json = """
        {
          "Version": "AP_Motors library test ver 1.2",
          "layouts": [
            { "Class": 1, "ClassName": "QUAD", "Type": 1, "TypeName": "X", "motors": [
              { "Number": 1, "TestOrder": 1, "Rotation": "CCW", "Roll": -0.5, "Pitch": 0.5 },
              { "Number": 2, "TestOrder": 3, "Rotation": "CCW", "Roll": 0.5, "Pitch": -0.5 },
              { "Number": 3, "TestOrder": 4, "Rotation": "CW", "Roll": 0.5, "Pitch": 0.5 },
              { "Number": 4, "TestOrder": 2, "Rotation": "CW", "Roll": -0.5, "Pitch": -0.5 } ] },
            { "Class": 2, "ClassName": "HEXA", "Type": 1, "TypeName": "X", "motors": [
              { "Number": 1, "TestOrder": 2, "Rotation": "CW", "Roll": -1, "Pitch": 0 },
              { "Number": 2, "TestOrder": 5, "Rotation": "CCW", "Roll": 1, "Pitch": 0 },
              { "Number": 3, "TestOrder": 6, "Rotation": "CW", "Roll": 0.5, "Pitch": 0.866 },
              { "Number": 4, "TestOrder": 3, "Rotation": "CCW", "Roll": -0.5, "Pitch": -0.866 },
              { "Number": 5, "TestOrder": 1, "Rotation": "CCW", "Roll": -0.5, "Pitch": 0.866 },
              { "Number": 6, "TestOrder": 4, "Rotation": "CW", "Roll": 0.5, "Pitch": -0.866 } ] },
            { "Class": 7, "ClassName": "TRI", "Type": 0, "TypeName": "default", "motors": [
              { "Number": 1, "TestOrder": 1, "Rotation": "?", "Roll": -0.866, "Pitch": 0.5 } ] }
          ]
        }
        """;

    private static readonly MotorLayoutCatalog Catalog = MotorLayoutCatalog.Parse(Json);

    private static Func<string, double?> Params(params (string Name, double Value)[] values)
    {
        var d = values.ToDictionary(v => v.Name, v => v.Value);
        return n => d.TryGetValue(n, out var v) ? v : null;
    }

    [TestMethod]
    public void ParsesLayouts()
    {
        Assert.HasCount(3, Catalog.Layouts);
        var quad = Catalog.Find(1, 1);
        Assert.IsNotNull(quad);
        Assert.AreEqual("X", quad.TypeName);
        var a = quad.Motors.Single(m => m.TestOrder == 1);
        Assert.AreEqual(1, a.Number);
        Assert.AreEqual("A", a.TestLetter);
        Assert.IsTrue(a.IsCounterClockwise);
        // front right: right of centre and above it on screen
        Assert.IsGreaterThan(0, a.X);
        Assert.IsLessThan(0, a.Y);
        Assert.IsNull(Catalog.Find(1, 0));
    }

    [TestMethod]
    public void OtherFormatVersionsAreIgnored()
    {
        var other = MotorLayoutCatalog.Parse("""{ "Version": "AP_Motors library test ver 2.0", "layouts": [ { "Class": 1, "Type": 1, "motors": [] } ] }""");
        Assert.IsEmpty(other.Layouts);
    }

    [TestMethod]
    public void FrameClassAndTypeChooseTheLayout()
    {
        var plan = MotorTestLogic.Resolve(Params(("FRAME_CLASS", 2), ("FRAME_TYPE", 1)), MAVLink.MAV_TYPE.HEXAROTOR, Catalog);
        Assert.IsTrue(plan.Enabled);
        Assert.AreEqual(6, plan.MotorCount);
        Assert.AreEqual(2, plan.Layout?.Class);
        Assert.AreEqual("FRAME_CLASS", plan.ClassParam);
    }

    [TestMethod]
    public void QuadPlaneUsesQFrameParameters()
    {
        var plan = MotorTestLogic.Resolve(Params(("Q_FRAME_CLASS", 1), ("Q_FRAME_TYPE", 1)), MAVLink.MAV_TYPE.FIXED_WING, Catalog);
        Assert.AreEqual(4, plan.MotorCount);
        Assert.AreEqual("Q_FRAME_TYPE", plan.TypeParam);
        Assert.IsNotNull(plan.Layout);
    }

    [TestMethod]
    public void UnknownLayoutFallsBackToVehicleType()
    {
        // octa has no layout in this catalogue: count by MAV type
        var plan = MotorTestLogic.Resolve(Params(("FRAME_CLASS", 3), ("FRAME_TYPE", 1)), MAVLink.MAV_TYPE.OCTOROTOR, Catalog);
        Assert.IsTrue(plan.Enabled);
        Assert.AreEqual(8, plan.MotorCount);
        Assert.IsNull(plan.Layout);

        // QuadPlane class numbers map to a type: 2 hexa, 6 heli, 7 tri
        Assert.AreEqual(6, MotorTestLogic.Resolve(Params(("Q_FRAME_CLASS", 2), ("Q_FRAME_TYPE", 0)), MAVLink.MAV_TYPE.FIXED_WING, Catalog).MotorCount);
        Assert.AreEqual(0, MotorTestLogic.Resolve(Params(("Q_FRAME_CLASS", 6), ("Q_FRAME_TYPE", 0)), MAVLink.MAV_TYPE.FIXED_WING, Catalog).MotorCount);
        Assert.AreEqual(4, MotorTestLogic.Resolve(Params(("Q_FRAME_CLASS", 7), ("Q_FRAME_TYPE", 1)), MAVLink.MAV_TYPE.FIXED_WING, Catalog).MotorCount);

        // old copters: FRAME only
        Assert.AreEqual(6, MotorTestLogic.Resolve(Params(("FRAME", 1)), MAVLink.MAV_TYPE.HEXAROTOR, Catalog).MotorCount);
        Assert.AreEqual(12, MotorTestLogic.Resolve(Params(("FRAME", 1)), MAVLink.MAV_TYPE.DODECAROTOR, Catalog).MotorCount);
    }

    [TestMethod]
    public void RoversHaveFourMotorsAndPlanesNone()
    {
        Assert.AreEqual(4, MotorTestLogic.Resolve(Params(("FRAME_CLASS", 1), ("FRAME_TYPE", 0)), MAVLink.MAV_TYPE.GROUND_ROVER, Catalog).MotorCount);
        Assert.AreEqual(4, MotorTestLogic.Resolve(Params(), MAVLink.MAV_TYPE.SURFACE_BOAT, Catalog).MotorCount);
        Assert.IsFalse(MotorTestLogic.Resolve(Params(), MAVLink.MAV_TYPE.FIXED_WING, Catalog).Enabled);
    }

    [TestMethod]
    public void ShippedLayoutFileHasCommonFrames()
    {
        var path = RepoFile(MotorLayoutCatalog.FileName);
        var shipped = MotorLayoutCatalog.Load(path);
        Assert.IsGreaterThan(50, shipped.Layouts.Count);

        // Quad X: test A is motor 1 (front right), then clockwise: 4, 2, 3
        var quadX = shipped.Find(1, 1)!;
        var order = quadX.Motors.OrderBy(m => m.TestOrder).Select(m => m.Number).ToArray();
        CollectionAssert.AreEqual(new[] { 1, 4, 2, 3 }, order);
        Assert.HasCount(8, shipped.Find(3, 1)!.Motors);
        Assert.HasCount(12, shipped.Find(12, 1)!.Motors);
    }

    [TestMethod]
    public void FrameTypesDependOnTheClass()
    {
        var shipped = MotorLayoutCatalog.Load(RepoFile(MotorLayoutCatalog.FileName));
        var quad = FrameCatalog.ValidTypes(1, shipped);
        foreach (var t in new[] { 0, 1, 2, 3, 4, 5, 12, 13, 14 })
            Assert.Contains(t, quad);
        CollectionAssert.AreEqual(new[] { 0, 1, 3, 13, 14 }, FrameCatalog.ValidTypes(2, shipped).ToArray());
        var y6 = FrameCatalog.ValidTypes(5, shipped);
        CollectionAssert.AreEqual(new[] { 1, 10, 11 }, y6.ToArray());
        Assert.IsEmpty(FrameCatalog.ValidTypes(6, shipped), "heli ignores FRAME_TYPE");
        Assert.IsEmpty(FrameCatalog.ValidTypes(7, shipped), "tri ignores FRAME_TYPE");

        // keep a valid type, else X, else the first valid one
        Assert.AreEqual(0, FrameCatalog.TypeForClass(2, 0, shipped));
        Assert.AreEqual(1, FrameCatalog.TypeForClass(2, 10, shipped));
        Assert.AreEqual(10, FrameCatalog.TypeForClass(5, 10, shipped));
        Assert.AreEqual(3, FrameCatalog.TypeForClass(6, 3, shipped));

        Assert.IsNotNull(FrameCatalog.Preview(5, 1, shipped));
        Assert.IsNotNull(FrameCatalog.ClassPreview(3, shipped));
        Assert.IsNull(FrameCatalog.ClassPreview(6, shipped));
    }

    internal static string RepoFile(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, name);
            if (File.Exists(candidate))
                return candidate;
        }

        Assert.Inconclusive(name + " not found above " + AppContext.BaseDirectory);
        return "";
    }
}

[TestClass]
public class HardwareSetupSerialTests
{
    private const string Rules = """
        {
          "1": { "PresetBaudRate": 115, "PresetOptionsByte": 0, "Comment": "MAVLink sensor note" },
          "2": { "PresetBaudRate": -1, "PresetOptionsByte": 0, "Comment": "MAVLink2 note" },
          "5": { "PresetBaudRate": 999, "PresetOptionsByte": -1, "Comment": "" }
        }
        """;

    [TestMethod]
    public void ParsesRules()
    {
        var rules = SerialPortLogic.ParseRules(Rules);
        Assert.HasCount(3, rules);
        Assert.AreEqual(new SerialOptionRule(115, 0, "MAVLink sensor note"), rules[1]);
        Assert.AreEqual(-1, rules[2].BaudRate);
    }

    [TestMethod]
    public void ShippedRulesParse()
    {
        var rules = SerialPortLogic.LoadRules(HardwareSetupMotorLayoutTests.RepoFile(SerialPortLogic.RulesFileName));
        Assert.IsTrue(rules.ContainsKey(1));
        Assert.IsTrue(rules.ContainsKey(2));
    }

    [TestMethod]
    public void UnofferedBaudRatesAreDropped()
    {
        var rules = SerialPortLogic.Sanitize(SerialPortLogic.ParseRules(Rules), [57, 115, 921]);
        Assert.AreEqual(115, rules[1].BaudRate);
        Assert.AreEqual(-1, rules[5].BaudRate);
        Assert.AreEqual(-1, rules[2].BaudRate);
    }

    [TestMethod]
    public void ChoosingAProtocolAppliesItsPreset()
    {
        var rules = SerialPortLogic.ParseRules(Rules);

        var mav1 = SerialPortLogic.Apply(2, 1, rules);
        Assert.AreEqual(115.0, mav1.Baud);
        Assert.AreEqual(0.0, mav1.Options);
        Assert.AreEqual("SERIAL2 : MAVLink sensor note", mav1.Note);

        var mav2 = SerialPortLogic.Apply(3, 2, rules);
        Assert.IsNull(mav2.Baud, "-1 keeps the baud rate");
        Assert.AreEqual(0.0, mav2.Options);

        var gps = SerialPortLogic.Apply(4, 5, rules);
        Assert.AreEqual(999.0, gps.Baud);
        Assert.IsNull(gps.Options, "-1 keeps the options");
        Assert.AreEqual("", gps.Note);

        var none = SerialPortLogic.Apply(4, 23, rules);
        Assert.IsNull(none.Baud);
        Assert.IsNull(none.Options);
        Assert.AreEqual("", none.Note);
    }

    [TestMethod]
    public void WarnsAtFourMavlinkPorts()
    {
        Assert.IsFalse(SerialPortLogic.TooManyMavlink([1, 2, 2, 5, 0]));
        Assert.IsTrue(SerialPortLogic.TooManyMavlink([1, 2, 2, 1, 5]));
        Assert.AreEqual(3, SerialPortLogic.CountMavlink([2, 2, 1, 23, -1]));
    }

    [TestMethod]
    public void FindsPortsFromBaudParameters()
    {
        var ports = SerialPortLogic.PortNumbers(["SERIAL0_BAUD", "SERIAL1_BAUD", "SERIAL1_PROTOCOL", "SERIAL10_BAUD", "SERIAL3_BAUD", "SERIAL_PASS1"]);
        CollectionAssert.AreEqual(new[] { 1, 3, 10 }, ports.ToArray());
    }

    [TestMethod]
    public void ParsesUartNames()
    {
        var names = SerialPortLogic.ParseUarts("UARTV1\nSERIAL0 OTG1  TX*      0 RX*      0 TXBD 0\r\nSERIAL1 UART7 TX  1234 RX 9\nSERIAL2 UART5 TX\nbad line\nSERIALX FOO\n");
        Assert.HasCount(2, names);
        Assert.AreEqual("UART7", names[1]);
        Assert.AreEqual("UART5", names[2]);
    }
}

[TestClass]
public class HardwareSetupBatteryTests
{
    [TestMethod]
    public void VoltageMultiplierScalesByMeasuredOverReported()
    {
        // the autopilot reads 12.0 V while the meter shows 12.6 V
        var updated = BatteryLogic.CalibrateVoltMult(12.6, 12.0, 10.1);
        Assert.IsNotNull(updated);
        Assert.AreEqual(10.605, updated.Value, 1e-9);

        // after the change the autopilot would report measured
        Assert.AreEqual(12.6, 12.0 * updated.Value / 10.1, 1e-9);
    }

    [TestMethod]
    public void AmpsPerVoltScalesTheSameWay()
    {
        Assert.AreEqual(20.0, BatteryLogic.CalibrateAmpPerVolt(5, 4.5, 18)!.Value, 1e-9);
    }

    [TestMethod]
    public void CalibrationNeedsReadings()
    {
        Assert.IsNull(BatteryLogic.CalibrateVoltMult(12.6, 0, 10.1), "no reported voltage");
        Assert.IsNull(BatteryLogic.CalibrateVoltMult(0, 12, 10.1), "no measurement");
        Assert.IsNull(BatteryLogic.CalibrateVoltMult(12.6, 12, 0), "no multiplier to scale");
        Assert.IsNull(BatteryLogic.CalibrateAmpPerVolt(double.NaN, 3, 17));
    }

    [TestMethod]
    public void RecognisesSensorPresetsAtFloatPrecision()
    {
        // parameters arrive as floats
        var sensor = BatteryLogic.DetectSensor((float)18.0018, (float)10.10101);
        Assert.AreEqual("3DR Power Module", sensor.Name);
        Assert.AreEqual(9, BatteryLogic.DetectSensor((float)36.364, (float)18.182).Id);
        Assert.AreEqual(0, BatteryLogic.DetectSensor(17, 13).Id);
        Assert.AreEqual(0, BatteryLogic.DetectSensor(null, 12.02).Id);
    }

    [TestMethod]
    public void RecognisesBoardsByPins()
    {
        Assert.AreEqual("The Cube or Pixhawk", BatteryLogic.DetectBoard(2, 3)?.Name);
        Assert.AreEqual(5, BatteryLogic.DetectBoard(10, 11)?.Id);
        Assert.AreEqual(6, BatteryLogic.DetectBoard(10, -1)?.Id);
        Assert.AreEqual(10, BatteryLogic.DetectBoard(8, 4)?.Id);
        Assert.IsNull(BatteryLogic.DetectBoard(42, 43));
        Assert.IsNull(BatteryLogic.DetectBoard(null, null));
    }

    [TestMethod]
    public void PresetTablesMatchMissionPlanner()
    {
        var atto90 = BatteryLogic.Sensors.Single(s => s.Name == "AttoPilot 90A");
        Assert.AreEqual(27.3224, atto90.AmpPerVolt);
        Assert.AreEqual(15.70105, atto90.VoltMult);
        var orange = BatteryLogic.Boards.Single(b => b.Name == "Cube Orange");
        Assert.AreEqual((14, 15), (orange.VoltPin, orange.CurrPin));
        Assert.IsTrue(BatteryLogic.IsAnalog(4));
        Assert.IsFalse(BatteryLogic.IsAnalog(7));
        Assert.IsFalse(BatteryLogic.MeasuresCurrent(3));
    }
}

[TestClass]
public class HardwareSetupBitmaskTests
{
    [TestMethod]
    public void DecodesSetBits()
    {
        CollectionAssert.AreEqual(new[] { 0, 2 }, BitmaskCodec.Decode(5, [0, 1, 2, 3]).ToArray());
        Assert.IsTrue(BitmaskCodec.IsSet(1 << 20, 20));
        Assert.IsFalse(BitmaskCodec.IsSet(1 << 20, 19));
        Assert.IsFalse(BitmaskCodec.IsSet(5, 40));
    }

    [TestMethod]
    public void EncodeKeepsUndocumentedBits()
    {
        // bit 7 is set on the vehicle but not described in the metadata
        var value = BitmaskCodec.Encode(128 + 1, [new(0, false), new(1, true), new(2, true)]);
        Assert.AreEqual(128 + 2 + 4, value);
    }

    [TestMethod]
    public void RoundTrips()
    {
        var bits = new[] { 0, 3, 31 };
        var value = BitmaskCodec.FromBits(bits);
        Assert.AreEqual(1 + 8 + 2147483648.0, value);
        CollectionAssert.AreEqual(bits, BitmaskCodec.Decode(value, Enumerable.Range(0, 32)).ToArray());
    }

    [TestMethod]
    public void OddValuesBecomeMasks()
    {
        Assert.AreEqual(0u, BitmaskCodec.ToMask(double.NaN));
        Assert.AreEqual(3u, BitmaskCodec.ToMask(2.9999998));
        Assert.AreEqual(uint.MaxValue, BitmaskCodec.ToMask(-1));
        Assert.AreEqual(uint.MaxValue, BitmaskCodec.ToMask(1e12));
    }

    [TestMethod]
    public void SummarisesLabels()
    {
        var labels = new List<KeyValuePair<int, string>> { new(0, "Invert RX"), new(1, "Invert TX"), new(2, "Half Duplex") };
        Assert.AreEqual("Invert RX / Half Duplex", BitmaskCodec.Summary(5, labels));
        Assert.AreEqual("None", BitmaskCodec.Summary(0, labels));
    }
}
