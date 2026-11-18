using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Calibration;

/// <summary>
/// An ArduPilot sensor device id (the *_DEV_ID parameters), decoded as AP_HAL::Device packs it
/// (Tools/scripts/decode_devid.py): bus type in bits 0-2, bus instance in bits 3-7, bus address in
/// bits 8-15 and the driver's device type in bits 16-23. Names follow Mission Planner's
/// DeviceInfo.cs; the device type tables are the library's (<see cref="Device.DeviceStructure"/>).
/// </summary>
public readonly record struct DeviceId(uint Raw)
{
    public Device.BusType BusType => (Device.BusType)(Raw & 0x7);

    public int Bus => (int)((Raw >> 3) & 0x1f);

    public int Address => (int)((Raw >> 8) & 0xff);

    public int DevType => (int)((Raw >> 16) & 0xff);

    /// <summary>"I2C", "SPI", "UAVCAN"…</summary>
    public string BusTypeName => BusType.ToString().Replace("BUS_TYPE_", "");

    /// <summary>
    /// The device type for the sensor class the parameter belongs to ("AK09916" for a COMPASS
    /// parameter); DroneCAN sensors have no driver type and show "SENSOR_ID#n".
    /// </summary>
    public string DevTypeName(string paramName)
    {
        if (BusType == Device.BusType.BUS_TYPE_UAVCAN)
            return "SENSOR_ID#" + DevType;

        var type = (byte)DevType;
        string name;
        if (paramName.Contains("COMP", StringComparison.Ordinal))
            name = ((Device.DeviceStructure.compass_type)type).ToString();
        else if (paramName.Contains("BARO", StringComparison.Ordinal))
            name = ((Device.DeviceStructure.baro_types)type).ToString();
        else if (paramName.Contains("ASP", StringComparison.Ordinal))
            name = ((Device.DeviceStructure.airspeed_types)type).ToString();
        else
            name = ((Device.DeviceStructure.imu_types)type).ToString();
        return name.Replace("DEVTYPE_", "");
    }

    /// <summary>Packs the fields back into an id (the inverse of the accessors).</summary>
    public static DeviceId Make(Device.BusType busType, int bus, int address, int devType) =>
        new(((uint)busType & 0x7) | (((uint)bus & 0x1f) << 3) | (((uint)address & 0xff) << 8) | (((uint)devType & 0xff) << 16));

    public override string ToString() => Raw.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
