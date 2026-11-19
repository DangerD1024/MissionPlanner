using MissionPlanner.App.Core.ViewModels.SetupScreens.Calibration;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;
using MissionPlanner.App.Core.ViewModels.SetupScreens.OptionalHardware;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Tests.SetupScreens.OptionalHardware;

[TestClass]
public sealed class ChannelAssignmentTests
{
    private static FakeParamSource Servos(params (int N, double Function)[] functions)
    {
        var src = new FakeParamSource();
        for (var n = 1; n <= 16; n++)
            src.Values[$"SERVO{n}_FUNCTION"] = 0;
        foreach (var (n, f) in functions)
            src.Values[$"SERVO{n}_FUNCTION"] = f;
        return src;
    }

    [TestMethod]
    public void ServoOutputs_PreferServoNOverOldRcN()
    {
        var src = Servos();
        Assert.HasCount(16, ChannelAssignment.ServoOutputs(src));
        Assert.AreEqual("SERVO1", ChannelAssignment.ServoOutputs(src)[0]);

        var old = new FakeParamSource();
        old.Values["RC5_FUNCTION"] = 0;
        old.Values["RC6_FUNCTION"] = 0;
        CollectionAssert.AreEqual(new[] { "RC5", "RC6" }, ChannelAssignment.ServoOutputs(old).ToArray());
    }

    [TestMethod]
    public void Plan_GivesTheFunctionToOneOutputAndClearsTheOthers()
    {
        var src = Servos((9, 27), (11, 27), (3, 33));
        var outputs = ChannelAssignment.ServoOutputs(src);
        Assert.AreEqual("SERVO9", ChannelAssignment.Find(src, outputs, "_FUNCTION", 27));

        var plan = ChannelAssignment.Plan(src, outputs, "_FUNCTION", 27, "SERVO10");
        CollectionAssert.AreEqual(new[]
        {
            new KeyValuePair<string, double>("SERVO9_FUNCTION", 0),
            new KeyValuePair<string, double>("SERVO11_FUNCTION", 0),
            new KeyValuePair<string, double>("SERVO10_FUNCTION", 27),
        }, plan.ToArray());

        // already right: only the duplicate goes
        CollectionAssert.AreEqual(new[] { new KeyValuePair<string, double>("SERVO11_FUNCTION", 0) },
            ChannelAssignment.Plan(src, outputs, "_FUNCTION", 27, "SERVO9").ToArray());

        // none: every holder is cleared
        Assert.HasCount(2, ChannelAssignment.Plan(src, outputs, "_FUNCTION", 27, null));
    }

    [TestMethod]
    public async Task Picker_SyncsWithoutWriting_AndAppliesTheUsersChoice()
    {
        var src = Servos((10, 27));
        string? applied = "none yet";
        var picker = new ChannelPicker("Servo output", "_FUNCTION", 27, ChannelAssignment.ServoOutputs(src), ch =>
        {
            applied = ch;
            return Task.CompletedTask;
        });

        picker.Sync(src);
        Assert.AreEqual("SERVO10", picker.Channel);
        Assert.AreEqual("none yet", applied, "showing the vehicle's value is not an edit");
        Assert.AreEqual("Disabled", picker.Options[0].Label);

        picker.Selected = picker.Options.First(o => o.Channel == "SERVO12");
        await Task.Yield();
        Assert.AreEqual("SERVO12", applied);

        picker.Selected = picker.Options[0];
        Assert.IsNull(applied);
    }
}

[TestClass]
public sealed class HardwareIdsTests
{
    [TestMethod]
    public void Filter_IsMissionPlanners()
    {
        Assert.IsTrue(HardwareIds.IsDeviceIdParam("COMPASS_DEV_ID"));
        Assert.IsTrue(HardwareIds.IsDeviceIdParam("INS_ACC2_ID"));
        Assert.IsTrue(HardwareIds.IsDeviceIdParam("BARO1_DEVID"));
        Assert.IsTrue(HardwareIds.IsDeviceIdParam("ARSPD_DEVID"));
        Assert.IsFalse(HardwareIds.IsDeviceIdParam("OSD5_PARAM1_IDX"));
        Assert.IsFalse(HardwareIds.IsDeviceIdParam("FRSKY_DNLINK_ID"));
        Assert.IsFalse(HardwareIds.IsDeviceIdParam("SYSID_THISMAV"));
    }

    [TestMethod]
    public void Rows_DecodeByTheSensorsDeviceTable()
    {
        var compass = DeviceId.Make(Device.BusType.BUS_TYPE_I2C, 1, 0x0c, (int)Device.DeviceStructure.compass_type.DEVTYPE_AK09916);
        var imu = DeviceId.Make(Device.BusType.BUS_TYPE_SPI, 1, 0, (int)Device.DeviceStructure.imu_types.DEVTYPE_INS_ICM20689);
        var baro = DeviceId.Make(Device.BusType.BUS_TYPE_SPI, 4, 0, (int)Device.DeviceStructure.baro_types.DEVTYPE_BARO_MS5611);
        var can = DeviceId.Make(Device.BusType.BUS_TYPE_UAVCAN, 0, 125, 1);

        var rows = HardwareIds.Rows(
        [
            new("INS_ACC_ID", imu.Raw), new("COMPASS_DEV_ID", compass.Raw), new("BARO1_DEVID", baro.Raw),
            new("COMPASS_DEV_ID2", can.Raw), new("COMPASS_DEV_ID3", 0), new("RC_OPTIONS", 3),
        ]);

        CollectionAssert.AreEqual(new[] { "BARO1_DEVID", "COMPASS_DEV_ID", "COMPASS_DEV_ID2", "COMPASS_DEV_ID3", "INS_ACC_ID" },
            rows.Select(r => r.ParamName).ToArray());

        var c = rows.Single(r => r.ParamName == "COMPASS_DEV_ID");
        Assert.AreEqual(SensorKind.Compass, c.Kind);
        Assert.AreEqual("I2C", c.BusType);
        Assert.AreEqual("1", c.Bus);
        Assert.AreEqual("12 (0x0C)", c.Address);
        Assert.AreEqual("AK09916", c.DevType);

        Assert.AreEqual("INS_ICM20689", rows.Single(r => r.ParamName == "INS_ACC_ID").DevType);
        Assert.AreEqual("IMU", rows.Single(r => r.ParamName == "INS_ACC_ID").Sensor);
        Assert.AreEqual("BARO_MS5611", rows.Single(r => r.ParamName == "BARO1_DEVID").DevType);
        Assert.AreEqual("SENSOR_ID#1", rows.Single(r => r.ParamName == "COMPASS_DEV_ID2").DevType);

        var unset = rows.Single(r => r.ParamName == "COMPASS_DEV_ID3");
        Assert.IsFalse(unset.IsSet);
        Assert.AreEqual("", unset.BusType);
        Assert.AreEqual("", unset.DevType);
    }

    [TestMethod]
    public void OtherIds_ShowTheRawDeviceType()
    {
        var row = HardwareIds.Rows([new("GPS1_FAKE_ID", DeviceId.Make(Device.BusType.BUS_TYPE_SERIAL, 2, 0, 7).Raw)]).Single();
        Assert.AreEqual(SensorKind.Other, row.Kind);
        Assert.AreEqual("SERIAL", row.BusType);
        Assert.AreEqual("7", row.DevType);
    }
}

[TestClass]
public sealed class CompassMotRecorderTests
{
    [TestMethod]
    public void Samples_AreKeptByThrottle_WithTheLatestAndPeak()
    {
        var rec = new CompassMotRecorder();
        rec.Add(CompassMotSample.From(new MAVLink.mavlink_compassmot_status_t(5.5f, 1, 2, 3, 400, 25)));
        rec.Add(CompassMotSample.From(new MAVLink.mavlink_compassmot_status_t(2.0f, 1, 2, 3, 150, 10)));
        rec.Add(CompassMotSample.From(new MAVLink.mavlink_compassmot_status_t(9.0f, 4, 5, 6, 650, 42)));

        var points = rec.ByThrottle();
        CollectionAssert.AreEqual(new[] { 15.0, 40.0, 65.0 }, points.Select(p => p.ThrottlePercent).ToArray());
        Assert.AreEqual(42, rec.MaxInterference);
        Assert.AreEqual(65, rec.Latest!.Value.ThrottlePercent);
        Assert.AreEqual(9.0, rec.Latest.Value.CurrentAmps, 1e-6);

        var text = CompassMotRecorder.Describe(rec.Latest.Value);
        StringAssert.Contains(text, "Current: 9.00 A");
        StringAssert.Contains(text, "x,y,z 4.00,5.00,6.00");
        StringAssert.Contains(text, "Throttle: 65 %");
        StringAssert.Contains(text, "Interference: 42 %");

        rec.Clear();
        Assert.AreEqual(0, rec.Count);
        Assert.IsNull(rec.Latest);
    }

    [TestMethod]
    public void Capacity_DropsTheOldest()
    {
        var rec = new CompassMotRecorder { Capacity = 2 };
        for (ushort i = 1; i <= 3; i++)
            rec.Add(new CompassMotSample(i, 0, 0, 0, 0, 0));
        CollectionAssert.AreEqual(new[] { 2.0, 3.0 }, rec.ByThrottle().Select(p => p.ThrottlePercent).ToArray());
    }

    [TestMethod]
    public void Rating_FollowsArduPilotsGuidance()
    {
        Assert.AreEqual(InterferenceRating.None, CompassMotRecorder.Rate(80, any: false));
        Assert.AreEqual(InterferenceRating.Good, CompassMotRecorder.Rate(30, true));
        Assert.AreEqual(InterferenceRating.Acceptable, CompassMotRecorder.Rate(31, true));
        Assert.AreEqual(InterferenceRating.Acceptable, CompassMotRecorder.Rate(60, true));
        Assert.AreEqual(InterferenceRating.Bad, CompassMotRecorder.Rate(61, true));
    }
}

[TestClass]
public sealed class SensorInstancesTests
{
    [TestMethod]
    public void Find_NumberedUnnumberedAndLettered()
    {
        var names = new HashSet<string> { "RNGFND1_TYPE", "RNGFND2_TYPE", "RNGFNDA_TYPE" };
        var found = SensorInstances.Find(names.Contains, "RNGFND", "TYPE");
        CollectionAssert.AreEqual(new[] { "RNGFND1_", "RNGFND2_", "RNGFNDA_" }, found.Select(f => f.Prefix).ToArray());
        CollectionAssert.AreEqual(new[] { 1, 2, 10 }, found.Select(f => f.Number).ToArray());

        var old = SensorInstances.Find(new HashSet<string> { "ARSPD_TYPE", "ARSPD2_TYPE" }.Contains, "ARSPD", "TYPE");
        CollectionAssert.AreEqual(new[] { "ARSPD_", "ARSPD2_" }, old.Select(f => f.Prefix).ToArray());
        Assert.AreEqual("Airspeed sensor 2", old[1].Title("Airspeed sensor"));
    }

    [TestMethod]
    public void Split_MainKeysFirstByAlias_ThenTheRest()
    {
        var names = new[] { "RNGFND1_TYPE", "RNGFND1_MIN_CM", "RNGFND1_ORIENT", "RNGFND1_ADDR", "RNGFND1_WSP_AVG", "RNGFND2_MIN_CM", "RNGFND_FILT" };
        var (main, rest) = SensorInstances.Split(names, "RNGFND1_", [["ORIENT"], ["MIN", "MIN_CM"], ["ADDR"]], n => n.EndsWith("_TYPE"));
        CollectionAssert.AreEqual(new[] { "RNGFND1_ORIENT", "RNGFND1_MIN_CM", "RNGFND1_ADDR" }, main.ToArray());
        CollectionAssert.AreEqual(new[] { "RNGFND1_WSP_AVG" }, rest.ToArray());
    }
}

[TestClass]
public sealed class ScaledFieldTests
{
    [TestMethod]
    public void ShowsCentidegreesAsDegrees_AndWritesBackScaled()
    {
        var src = new FakeParamSource();
        src.Values["FLOW_ORIENT_YAW"] = 9000;
        src.Integers.Add("FLOW_ORIENT_YAW");
        var field = new ParamField(src, "FLOW_ORIENT_YAW", new ParamFieldSpec { Debounce = TimeSpan.Zero });
        var yaw = new ScaledField(field, 100, "deg", -179, 180, 1, "Yaw orientation");

        Assert.AreEqual(90m, yaw.Value);
        Assert.AreEqual(-179m, yaw.Minimum);
        Assert.AreEqual("Yaw orientation", yaw.Label);

        yaw.Value = -45m;
        Assert.AreEqual(("FLOW_ORIENT_YAW", -4500.0), src.Writes.Single());
        Assert.AreEqual(-45m, yaw.Value);

        // a vehicle value outside the shown range widens it instead of being clamped
        src.Values["FLOW_ORIENT_YAW"] = -18000;
        field.Refresh();
        Assert.AreEqual(-180m, yaw.Value);
        Assert.AreEqual(-180m, yaw.Minimum);
    }
}
