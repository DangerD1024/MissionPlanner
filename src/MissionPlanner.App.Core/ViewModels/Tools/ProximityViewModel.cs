using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.Utilities;
using static MAVLink;

namespace MissionPlanner.App.Core.ViewModels.Tools;

/// <summary>
/// One obstacle reading around the vehicle: a sector centred on a bearing (degrees clockwise from the
/// nose), its width, and the distance in centimetres. Sensors on the eight yaw orientations cover 45°;
/// OBSTACLE_DISTANCE readings (<see cref="IsCustom"/>) cover their own increment.
/// </summary>
public readonly record struct ProximitySector(double CenterDeg, double WidthDeg, double DistanceCm, bool IsCustom, uint SensorId)
{
    public double StartDeg => CenterDeg - WidthDeg / 2;

    public double EndDeg => CenterDeg + WidthDeg / 2;
}

/// <summary>What the radar draws at one moment.</summary>
public sealed record ProximityFrame(IReadOnlyList<ProximitySector> Sectors, double RangeCm, double VehicleSizeCm)
{
    public static readonly ProximityFrame Empty = new([], 500, 80);
}

/// <summary>The maths of Mission Planner's ProximityControl, apart from the drawing.</summary>
public static class ProximityGeometry
{
    /// <summary>Bearing of a DISTANCE_SENSOR orientation in the horizontal plane, or null (up, down, pitched).</summary>
    public static double? OrientationAngle(MAV_SENSOR_ORIENTATION orientation) => orientation switch
    {
        MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_NONE => 0,
        MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_YAW_45 => 45,
        MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_YAW_90 => 90,
        MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_YAW_135 => 135,
        MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_YAW_180 => 180,
        MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_YAW_225 => 225,
        MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_YAW_270 => 270,
        MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_YAW_315 => 315,
        _ => null,
    };

    /// <summary>A reading as a sector, or null for a sensor that does not look sideways.</summary>
    public static ProximitySector? ToSector(Proximity.directionState.data d)
    {
        if (d.Orientation == MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_CUSTOM)
            return new ProximitySector(Normalize(d.Angle), d.Size, d.Distance, true, d.SensorId);
        return OrientationAngle(d.Orientation) is { } a ? new ProximitySector(a, 45, d.Distance, false, d.SensorId) : null;
    }

    public static IReadOnlyList<ProximitySector> Sectors(IEnumerable<Proximity.directionState.data> readings) =>
        readings.Select(ToSector).OfType<ProximitySector>().ToList();

    /// <summary>0 ≤ angle &lt; 360.</summary>
    public static double Normalize(double deg)
    {
        deg %= 360;
        return deg < 0 ? deg + 360 : deg;
    }

    /// <summary>Centimetres per pixel so the range (and half a metre to spare) fits the smaller side.</summary>
    public static double CmPerPixel(double rangeCm, double width, double height) =>
        (rangeCm + 50) * 2 / Math.Max(1, Math.Min(width, height));

    /// <summary>Radius in pixels of a reading, held at the edge of the range.</summary>
    public static double RadiusPx(double distanceCm, double rangeCm, double cmPerPixel) =>
        Math.Min(distanceCm, rangeCm) / cmPerPixel;

    /// <summary>The point at a bearing (0 = up, clockwise) and radius from the centre, in screen coordinates.</summary>
    public static (double X, double Y) Point(double cx, double cy, double bearingDeg, double radius)
    {
        var a = bearingDeg * Math.PI / 180;
        return (cx + radius * Math.Sin(a), cy - radius * Math.Cos(a));
    }

    /// <summary>
    /// Distances of the range rings in centimetres: every 50 cm as Mission Planner draws them, or a
    /// wider round step so there are no more than ten.
    /// </summary>
    public static IReadOnlyList<double> Rings(double rangeCm)
    {
        double[] steps = [50, 100, 200, 250, 500, 1000, 2000, 2500, 5000, 10000];
        var step = steps.FirstOrDefault(s => rangeCm / s <= 10, steps[^1]);
        var list = new List<double>();
        for (var d = step; d <= rangeCm + 1e-9; d += step)
            list.Add(d);
        return list;
    }

    /// <summary>The closest reading in centimetres, or null.</summary>
    public static double? Closest(IEnumerable<ProximitySector> sectors) =>
        sectors.Select(s => (double?)s.DistanceCm).Min();
}

/// <summary>
/// Mission Planner's Proximity window (Ctrl+F > "Proximity"): a radar of the DISTANCE_SENSOR and
/// OBSTACLE_DISTANCE readings around the vehicle, nose up. Range and vehicle size are adjustable
/// (Mission Planner's +/- and [/] keys).
/// </summary>
public sealed partial class ProximityViewModel(AppServices services)
    : ToolPageViewModel("Proximity",
        "Obstacles the vehicle's proximity sensors see (DISTANCE_SENSOR and OBSTACLE_DISTANCE), nose up. Red: a sensor's 45° sector; yellow: obstacle distance readings.",
        services)
{
    private const string RangeKey = "next_proximity_range_cm";
    private const string SizeKey = "next_proximity_vehicle_cm";
    private int _ticks;

    [ObservableProperty]
    private ProximityFrame _frame = ProximityFrame.Empty with { RangeCm = Stored(RangeKey, 500), VehicleSizeCm = Stored(SizeKey, 80) };

    [ObservableProperty]
    private string _summary = "";

    /// <summary>Readings from sensors looking up or down, which the radar cannot show.</summary>
    [ObservableProperty]
    private string _verticalText = "";

    public string RangeText => L.Format("Radius {0} m, vehicle {1} m",
        (Frame.RangeCm / 100).ToString("0.0", CultureInfo.CurrentCulture), (Frame.VehicleSizeCm / 100).ToString("0.00", CultureInfo.CurrentCulture));

    partial void OnFrameChanged(ProximityFrame value) => OnPropertyChanged(nameof(RangeText));

    [RelayCommand]
    private void ZoomIn() => SetRange(Frame.RangeCm - RangeStep(Frame.RangeCm - 1));

    [RelayCommand]
    private void ZoomOut() => SetRange(Frame.RangeCm + RangeStep(Frame.RangeCm));

    [RelayCommand]
    private void Larger() => SetSize(Frame.VehicleSizeCm + 10);

    [RelayCommand]
    private void Smaller() => SetSize(Frame.VehicleSizeCm - 10);

    /// <summary>50 cm steps as Mission Planner, bigger ones further out.</summary>
    internal static double RangeStep(double rangeCm) => rangeCm switch
    {
        < 1000 => 50,
        < 5000 => 500,
        _ => 2500,
    };

    private void SetRange(double cm)
    {
        // Mission Planner puts a range below 1 cm back to 50 cm
        cm = cm < 1 ? 50 : Math.Min(cm, 100_000);
        Frame = Frame with { RangeCm = cm };
        Store(RangeKey, cm);
    }

    private void SetSize(double cm)
    {
        cm = cm < 1 ? 1 : Math.Min(cm, 2000);
        Frame = Frame with { VehicleSizeCm = cm };
        Store(SizeKey, cm);
    }

    public override void Tick()
    {
        base.Tick();
        // Mission Planner redraws every 100 ms
        if (++_ticks % 2 != 0)
            return;

        var readings = Snapshot();
        var sectors = ProximityGeometry.Sectors(readings);
        if (sectors.Count > 0 || Frame.Sectors.Count > 0)
            Frame = Frame with { Sectors = sectors };

        var closest = ProximityGeometry.Closest(sectors);
        Summary = !IsConnected ? "" :
            closest is { } c ? L.Format("{0} readings, closest {1} m", sectors.Count, (c / 100).ToString("0.00", CultureInfo.CurrentCulture)) :
            L.Tr("No proximity data from the vehicle.");

        var vertical = readings
            .Where(r => r.Orientation is MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_PITCH_90 or MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_PITCH_270)
            .Select(r => (r.Orientation == MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_PITCH_90 ? L.Tr("Up") : L.Tr("Down")) + " " +
                         (r.Distance / 100).ToString("0.00", CultureInfo.CurrentCulture) + " m");
        VerticalText = string.Join(", ", vertical);
    }

    /// <summary>A copy of the vehicle's current readings (the link's reader adds to the list while we read it).</summary>
    private List<Proximity.directionState.data> Snapshot()
    {
        if (!IsConnected && !Services.Session.Mav.logreadmode)
            return [];
        var state = Services.Session.Vehicle.Proximity?.DirectionState;
        if (state == null)
            return [];
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                lock (state)
                    return state.GetRaw().Where(d => d != null).ToList();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException)
            {
                // changed while copying: try again
            }
        }

        return [];
    }

    private static double Stored(string key, double fallback) =>
        double.TryParse(Settings.Instance[key], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;

    private static void Store(string key, double value) => Settings.Instance[key] = value.ToString(CultureInfo.InvariantCulture);
}
