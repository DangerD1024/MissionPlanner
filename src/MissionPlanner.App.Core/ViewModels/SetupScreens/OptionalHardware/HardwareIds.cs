using System.Globalization;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Calibration;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.OptionalHardware;

/// <summary>The kind of sensor a device-id parameter names (it decides which device type table applies).</summary>
public enum SensorKind
{
    Other,
    Imu,
    Compass,
    Baro,
    Airspeed,
}

/// <summary>One decoded device-id parameter (a row of HW ID).</summary>
public sealed record HardwareIdRow(string ParamName, uint Raw, SensorKind Kind)
{
    private DeviceId Id => new(Raw);

    public string DevId => Raw.ToString(CultureInfo.InvariantCulture);

    public string Hex => "0x" + Raw.ToString("X6", CultureInfo.InvariantCulture);

    public bool IsSet => Raw != 0;

    public string Sensor => Kind switch
    {
        SensorKind.Imu => "IMU",
        SensorKind.Compass => "Compass",
        SensorKind.Baro => "Baro",
        SensorKind.Airspeed => "Airspeed",
        _ => "",
    };

    public string BusType => IsSet ? Id.BusTypeName : "";

    public string Bus => IsSet ? Id.Bus.ToString(CultureInfo.InvariantCulture) : "";

    public string Address => IsSet ? $"{Id.Address} (0x{Id.Address:X2})" : "";

    /// <summary>The driver's device type: a name from the sensor's table, "SENSOR_ID#n" on DroneCAN, the number otherwise.</summary>
    public string DevType
    {
        get
        {
            if (!IsSet)
                return "";
            if (Id.BusType == Device.BusType.BUS_TYPE_UAVCAN || Kind != SensorKind.Other)
            {
                var name = Id.DevTypeName(Kind switch
                {
                    SensorKind.Compass => "COMPASS",
                    SensorKind.Baro => "BARO",
                    SensorKind.Airspeed => "ARSPD",
                    _ => ParamName,
                });
                // an unknown type number comes back as the number itself
                return name;
            }

            return Id.DevType.ToString(CultureInfo.InvariantCulture);
        }
    }
}

/// <summary>
/// HW ID (ConfigHWIDs): every parameter that holds a device id, decoded into bus type, bus,
/// address and device type (AP_HAL::Device's packing, as in the compass page).
/// </summary>
public static class HardwareIds
{
    /// <summary>Mission Planner's filter: names with _ID or _DEVID, without _IDX and FRSKY.</summary>
    public static bool IsDeviceIdParam(string name) =>
        (name.Contains("_ID", StringComparison.Ordinal) || name.Contains("_DEVID", StringComparison.Ordinal))
        && !name.Contains("_IDX", StringComparison.Ordinal)
        && !name.Contains("FRSKY", StringComparison.Ordinal);

    public static SensorKind KindOf(string name)
    {
        if (name.Contains("COMP", StringComparison.Ordinal) || name.Contains("_MAG", StringComparison.Ordinal))
            return SensorKind.Compass;
        if (name.Contains("BARO", StringComparison.Ordinal))
            return SensorKind.Baro;
        if (name.Contains("ASP", StringComparison.Ordinal))
            return SensorKind.Airspeed;
        if (name.StartsWith("INS_", StringComparison.Ordinal))
            return SensorKind.Imu;
        return SensorKind.Other;
    }

    /// <summary>The device-id parameters, by name.</summary>
    public static IReadOnlyList<HardwareIdRow> Rows(IEnumerable<KeyValuePair<string, double>> parameters) =>
        parameters
            .Where(p => IsDeviceIdParam(p.Key))
            .Select(p => new HardwareIdRow(p.Key, ToId(p.Value), KindOf(p.Key)))
            .OrderBy(r => r.ParamName, StringComparer.Ordinal)
            .ToList();

    // ids are 24-bit values sent as floats; negative junk is not an id
    private static uint ToId(double v) => double.IsFinite(v) && v > 0 && v <= uint.MaxValue ? (uint)Math.Round(v) : 0;
}
