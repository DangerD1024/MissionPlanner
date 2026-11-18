using MissionPlanner.App.Core.ViewModels.SetupScreens.Calibration;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Tests.SetupScreens;

[TestClass]
public sealed class DeviceIdTests
{
    [TestMethod]
    public void ExternalIst8310OnI2c()
    {
        // 658945 = 0x0A0E01: I2C bus 0, address 0x0E, IST8310 - a common external GPS/compass module
        var id = new DeviceId(658945);

        Assert.AreEqual(Device.BusType.BUS_TYPE_I2C, id.BusType);
        Assert.AreEqual("I2C", id.BusTypeName);
        Assert.AreEqual(0, id.Bus);
        Assert.AreEqual(0x0E, id.Address);
        Assert.AreEqual(0x0A, id.DevType);
        Assert.AreEqual("IST8310", id.DevTypeName("COMPASS_DEV_ID"));
    }

    [TestMethod]
    public void SpiCompassInsideAnImu()
    {
        // 590114 = 0x090122: SPI bus 4, address 1, AK09916 (the ICM20948's magnetometer)
        var id = new DeviceId(590114);

        Assert.AreEqual("SPI", id.BusTypeName);
        Assert.AreEqual(4, id.Bus);
        Assert.AreEqual(1, id.Address);
        Assert.AreEqual("AK09916", id.DevTypeName("COMPASS_PRIO1_ID"));
    }

    [TestMethod]
    public void DroneCanSensor_ShowsItsSensorId()
    {
        // 97539 = 0x017D03: UAVCAN, node 125, sensor 1
        var id = new DeviceId(97539);

        Assert.AreEqual("UAVCAN", id.BusTypeName);
        Assert.AreEqual(125, id.Address);
        Assert.AreEqual("SENSOR_ID#1", id.DevTypeName("COMPASS_DEV_ID2"));
    }

    [TestMethod]
    public void DevTypeTable_FollowsTheParameterClass()
    {
        var raw = DeviceId.Make(Device.BusType.BUS_TYPE_SPI, 1, 0, 0x0B).Raw;

        Assert.AreEqual("BARO_MS5611", new DeviceId(raw).DevTypeName("BARO1_DEVID"));
        Assert.AreEqual("INS_ICM20948", new DeviceId(DeviceId.Make(Device.BusType.BUS_TYPE_SPI, 1, 0, 0x2C).Raw).DevTypeName("INS_ACC_ID"));
        Assert.AreEqual("AIRSPEED_MS4525", new DeviceId(DeviceId.Make(Device.BusType.BUS_TYPE_I2C, 1, 0x28, 0x02).Raw).DevTypeName("ASP_DEVID"));
        Assert.AreEqual("ICM20948", new DeviceId(DeviceId.Make(Device.BusType.BUS_TYPE_SPI, 1, 0, 0x0B).Raw).DevTypeName("COMPASS_DEV_ID"));
    }

    [TestMethod]
    public void Make_IsTheInverseOfTheAccessors()
    {
        var id = DeviceId.Make(Device.BusType.BUS_TYPE_SERIAL, 31, 0xAB, 0xCD);

        Assert.AreEqual(Device.BusType.BUS_TYPE_SERIAL, id.BusType);
        Assert.AreEqual(31, id.Bus);
        Assert.AreEqual(0xAB, id.Address);
        Assert.AreEqual(0xCD, id.DevType);
        Assert.AreEqual(0x00CDAB00u | 6u | (31u << 3), id.Raw);
    }

    [TestMethod]
    public void UnknownValues_ShowAsNumbers()
    {
        var id = new DeviceId(0x00FE0007);

        Assert.AreEqual("7", id.BusTypeName);
        Assert.AreEqual("254", id.DevTypeName("COMPASS_DEV_ID"));
    }
}

[TestClass]
public sealed class CompassPriorityTests
{
    private const uint Ist8310 = 658945;
    private const uint Can = 97539;
    private const uint Ak09916 = 590114;

    private static Dictionary<string, double> Params() => new()
    {
        ["COMPASS_PRIO1_ID"] = Can,
        ["COMPASS_PRIO2_ID"] = Ist8310,
        ["COMPASS_PRIO3_ID"] = 0,
        ["COMPASS_DEV_ID"] = Ist8310,
        ["COMPASS_DEV_ID2"] = Can,
        ["COMPASS_DEV_ID3"] = 0,
        ["COMPASS_DEV_ID4"] = Ak09916,
        ["COMPASS_ORIENT"] = 6,
        ["COMPASS_ORIENT2"] = 0,
        ["COMPASS_EXTERNAL"] = 1,
        ["COMPASS_EXTERN2"] = 2,
        ["COMPASS_USE"] = 1,
    };

    [TestMethod]
    public void Build_PutsPrioritiesFirst_ThenOtherDetectedCompasses()
    {
        var rows = CompassPriority.Build(Params(), out var missing);

        Assert.IsFalse(missing);
        CollectionAssert.AreEqual(new[] { Can, Ist8310, Ak09916 }, rows.Select(r => r.DevId).ToArray());
        CollectionAssert.AreEqual(new[] { "COMPASS_PRIO1_ID", "COMPASS_PRIO2_ID", "COMPASS_DEV_ID4" }, rows.Select(r => r.ParamName).ToArray());
        Assert.IsTrue(rows.All(r => !r.Missing));
    }

    [TestMethod]
    public void Build_TakesOrientationAndExternalFromTheMatchingInstance()
    {
        var rows = CompassPriority.Build(Params(), out _);

        var ist = rows.Single(r => r.DevId == Ist8310);
        Assert.AreEqual(6, ist.Orientation);
        Assert.IsTrue(ist.External);

        var can = rows.Single(r => r.DevId == Can);
        Assert.AreEqual(0, can.Orientation);
        Assert.IsTrue(can.External);

        var extra = rows.Single(r => r.DevId == Ak09916);
        Assert.IsNull(extra.Orientation, "DEV_ID4+ have no orientation parameter");
        Assert.IsFalse(extra.External);
    }

    [TestMethod]
    public void Build_UnderstandsTheNewParameterNames()
    {
        var p = new Dictionary<string, double>
        {
            ["COMPASS_PRIO1_ID"] = Ist8310,
            ["COMPASS1_DEV_ID"] = Ist8310,
            ["COMPASS1_ORIENT"] = 4,
            ["COMPASS1_EXTERN"] = 0,
        };

        var row = CompassPriority.Build(p, out _).Single();

        Assert.AreEqual(4, row.Orientation);
        Assert.IsFalse(row.External);
    }

    [TestMethod]
    public void PrioritisedCompassThatIsGone_IsMissing()
    {
        var p = Params();
        p["COMPASS_PRIO3_ID"] = 131874; // configured once, not detected now

        var rows = CompassPriority.Build(p, out var missing);

        Assert.IsTrue(missing);
        Assert.AreEqual(131874u, rows[2].DevId);
        Assert.IsTrue(rows[2].Missing);
        Assert.AreEqual(Ak09916, rows[3].DevId);
    }

    [TestMethod]
    public void Reordering_GivesThePriorityParameters()
    {
        var rows = CompassPriority.Build(Params(), out _);

        Assert.IsTrue(CompassPriority.MoveDown(rows, 0));
        CollectionAssert.AreEqual(new[] { Ist8310, Can, Ak09916 }, rows.Select(r => r.DevId).ToArray());

        var values = CompassPriority.PriorityValues(rows);
        Assert.AreEqual((double)Ist8310, values["COMPASS_PRIO1_ID"]);
        Assert.AreEqual((double)Can, values["COMPASS_PRIO2_ID"]);
        Assert.AreEqual((double)Ak09916, values["COMPASS_PRIO3_ID"]);

        Assert.IsTrue(CompassPriority.MoveUp(rows, 2));
        values = CompassPriority.PriorityValues(rows);
        Assert.AreEqual((double)Ak09916, values["COMPASS_PRIO2_ID"]);
        Assert.AreEqual((double)Can, values["COMPASS_PRIO3_ID"]);
    }

    [TestMethod]
    public void MovesPastTheEnds_AreRefused()
    {
        var rows = CompassPriority.Build(Params(), out _);

        Assert.IsFalse(CompassPriority.MoveUp(rows, 0));
        Assert.IsFalse(CompassPriority.MoveDown(rows, rows.Count - 1));
        Assert.IsFalse(CompassPriority.MoveUp(rows, 7));
        CollectionAssert.AreEqual(new[] { Can, Ist8310, Ak09916 }, rows.Select(r => r.DevId).ToArray());
    }

    [TestMethod]
    public void FewerThanThreeCompasses_ClearTheUnusedSlots()
    {
        var p = Params();
        p["COMPASS_PRIO3_ID"] = 131874;
        var rows = CompassPriority.Build(p, out _);
        rows.RemoveAll(r => r.Missing || r.DevId == Ak09916);

        var values = CompassPriority.PriorityValues(rows);

        Assert.AreEqual((double)Can, values["COMPASS_PRIO1_ID"]);
        Assert.AreEqual((double)Ist8310, values["COMPASS_PRIO2_ID"]);
        Assert.AreEqual(0d, values["COMPASS_PRIO3_ID"]);

        values = CompassPriority.PriorityValues(rows.Take(1).ToList());
        Assert.AreEqual(0d, values["COMPASS_PRIO2_ID"]);
        Assert.AreEqual(0d, values["COMPASS_PRIO3_ID"]);
    }

    [TestMethod]
    public void NoRows_DoNotTouchPriority1()
    {
        var values = CompassPriority.PriorityValues([]);

        Assert.IsFalse(values.ContainsKey("COMPASS_PRIO1_ID"));
        Assert.AreEqual(0d, values["COMPASS_PRIO2_ID"]);
    }

    [TestMethod]
    public void OldFirmware_HasNoPriorities()
    {
        var p = new Dictionary<string, double> { ["COMPASS_DEV_ID"] = Ist8310, ["COMPASS_OFS_X"] = 10 };

        Assert.IsFalse(CompassPriority.HasPriorities(p));
        Assert.IsTrue(CompassPriority.HasPriorities(Params()));
        Assert.AreEqual(Ist8310, CompassPriority.Build(p, out _).Single().DevId);
    }
}
