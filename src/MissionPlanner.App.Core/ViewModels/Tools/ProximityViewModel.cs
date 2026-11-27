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

    /// <summary>The narrowest sector drawn, in degrees (an OBSTACLE_DISTANCE increment of 0).</summary>
    public const double MinWidthDeg = 0.5;

    /// <summary>
    /// A reading as a sector, or null for a sensor that does not look sideways and for a reading that
    /// cannot be drawn: a distance that is not a positive number, or an angle or width that is NaN or
    /// infinite (OBSTACLE_DISTANCE takes them from float fields of the packet). The width is held
    /// between <see cref="MinWidthDeg"/> and 360°; a negative increment (counter-clockwise) is as wide.
    /// </summary>
    public static ProximitySector? ToSector(Proximity.directionState.data? d)
    {
        if (d == null || !double.IsFinite(d.Distance) || d.Distance <= 0)
            return null;
        if (d.Orientation == MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_CUSTOM)
        {
            if (!double.IsFinite(d.Angle) || !double.IsFinite(d.Size))
                return null;
            return new ProximitySector(Normalize(d.Angle), Math.Clamp(Math.Abs(d.Size), MinWidthDeg, 360), d.Distance, true, d.SensorId);
        }

        return OrientationAngle(d.Orientation) is { } a ? new ProximitySector(a, 45, d.Distance, false, d.SensorId) : null;
    }

    public static IReadOnlyList<ProximitySector> Sectors(IEnumerable<Proximity.directionState.data?> readings) =>
        readings.Select(ToSector).OfType<ProximitySector>().ToList();

    /// <summary>0 ≤ angle &lt; 360.</summary>
    public static double Normalize(double deg)
    {
        deg %= 360;
        if (deg < 0)
            deg += 360;
        // a tiny negative angle rounds up to 360
        return deg >= 360 ? 0 : deg;
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
        if (!double.IsFinite(rangeCm) || rangeCm <= 0)
            return [];
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
/// (Mission Planner's +/- and [/] keys). The TOOLS page follows the selected vehicle; as in Mission
/// Planner a radar can also have a window of its own for one vehicle (<see cref="OpenWindow"/>), so
/// several can be watched side by side. Shows a live link or a loaded log; a paused log keeps its picture.
/// </summary>
public sealed partial class ProximityViewModel : ToolPageViewModel
{
    // translated where it is shown (the page header)
    private static readonly string Purpose = L.N(
        "Obstacles the vehicle's proximity sensors see (DISTANCE_SENSOR and OBSTACLE_DISTANCE), nose up. Red: a sensor's 45° sector; yellow: obstacle distance readings.");

    private const string RangeKey = "next_proximity_range_cm";
    private const string SizeKey = "next_proximity_vehicle_cm";
    private const double MaxRangeCm = 100_000;
    private const double MaxSizeCm = 2000;

    private int _ticks;

    // the log time and vehicle the frame was taken at, while a log is loaded: the readings expire by
    // the wall clock, so a paused log would lose its picture within seconds if it were read again
    private DateTime? _shownLogTime;
    private (uint SysId, int CompId)? _shownVehicle;

    /// <param name="services">The app's services.</param>
    /// <param name="pinned">The vehicle to show; null follows the selected vehicle.</param>
    public ProximityViewModel(AppServices services, (byte SysId, byte CompId)? pinned = null)
        : base(pinned is { } p ? L.Format("Proximity – vehicle {0}-{1}", p.SysId, p.CompId) : "Proximity", Purpose, services)
    {
        Pinned = pinned;
    }

    /// <summary>The vehicle this radar is for (a window's), or null for the selected vehicle.</summary>
    public (byte SysId, byte CompId)? Pinned { get; }

    /// <summary>Shown in a window of its own, which has no "Open in window" button.</summary>
    public bool IsWindow { get; init; }

    public bool CanOpenWindow => !IsWindow;

    /// <summary>No commands: the radar only draws what a link or a log brings.</summary>
    public override bool NeedsVehicle => false;

    [ObservableProperty]
    private ProximityFrame _frame = ProximityFrame.Empty with { RangeCm = Stored(RangeKey, 500, MaxRangeCm), VehicleSizeCm = Stored(SizeKey, 80, MaxSizeCm) };

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

    /// <summary>A radar window for the selected vehicle, next to this page.</summary>
    [RelayCommand]
    private void OpenInWindow() => OpenWindow(Services);

    /// <summary>
    /// Mission Planner's <c>new ProximityControl(MainV2.comPort.MAV).Show()</c>: a new radar window for
    /// the selected vehicle, which stays on it when another vehicle is selected (several are allowed).
    /// With no vehicle selected yet the window follows the selection.
    /// </summary>
    public static ProximityViewModel OpenWindow(AppServices services)
    {
        var mav = services.Session.Mav;
        (byte SysId, byte CompId)? pinned = mav.sysidcurrent is > 0 and <= byte.MaxValue
            ? ((byte)mav.sysidcurrent, (byte)mav.compidcurrent)
            : null;
        var page = new ProximityViewModel(services, pinned) { IsWindow = true };
        services.Windows.Show(page, 520, 560);
        return page;
    }

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
        cm = cm < 1 ? 50 : Math.Min(cm, MaxRangeCm);
        Frame = Frame with { RangeCm = cm };
        Store(RangeKey, cm);
    }

    private void SetSize(double cm)
    {
        cm = cm < 1 ? 1 : Math.Min(cm, MaxSizeCm);
        Frame = Frame with { VehicleSizeCm = cm };
        Store(SizeKey, cm);
    }

    public override void Tick()
    {
        base.Tick();
        // Mission Planner redraws every 100 ms
        if (++_ticks % 2 != 0)
            return;

        var mav = Services.Session.Mav;
        var live = IsConnected;
        var replaying = !live && mav.logplaybackfile != null;
        if (!live && !replaying)
        {
            _shownLogTime = null;
            _shownVehicle = null;
            Draw([]);
            Summary = L.Tr("Connect to a vehicle or load a log to see its proximity sensors.");
            return;
        }

        // a paused log brings nothing new: keep the picture it had (a seek or another log moves the log time)
        var id = Pinned is { } p ? ((uint)p.SysId, (int)p.CompId) : (mav.sysidcurrent, mav.compidcurrent);
        var logTime = mav.lastlogread;
        if (replaying && !mav.logreadmode && _shownLogTime == logTime && _shownVehicle == id)
            return;
        _shownLogTime = replaying ? logTime : null;
        _shownVehicle = id;

        var vehicle = Vehicle();
        var sectors = Draw(Snapshot(vehicle));
        var closest = ProximityGeometry.Closest(sectors);
        Summary = vehicle == null ? L.Format("Vehicle {0}-{1} is not on this link or in this log.", id.Item1, id.Item2) :
            closest is { } c ? L.Format("{0} readings, closest {1} m", sectors.Count, (c / 100).ToString("0.00", CultureInfo.CurrentCulture)) :
            L.Tr("No proximity data from the vehicle.");
    }

    /// <summary>Puts the readings on the radar; returns the sectors drawn.</summary>
    private IReadOnlyList<ProximitySector> Draw(List<Proximity.directionState.data> readings)
    {
        var sectors = ProximityGeometry.Sectors(readings);
        if (sectors.Count > 0 || Frame.Sectors.Count > 0)
            Frame = Frame with { Sectors = sectors };

        var vertical = readings
            .Where(r => r.Orientation is MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_PITCH_90 or MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_PITCH_270
                        && double.IsFinite(r.Distance))
            .Select(r => (r.Orientation == MAV_SENSOR_ORIENTATION.MAV_SENSOR_ROTATION_PITCH_90 ? L.Tr("Up") : L.Tr("Down")) + " " +
                         (r.Distance / 100).ToString("0.00", CultureInfo.CurrentCulture) + " m");
        VerticalText = string.Join(", ", vertical);
        return sectors;
    }

    /// <summary>
    /// The vehicle shown, looked up every time (the list is emptied on connect and when a log is
    /// loaded), or null when the pinned vehicle is not in the list.
    /// </summary>
    private MAVState? Vehicle()
    {
        if (Pinned is not { } p)
            return Services.Session.Vehicle;
        // the indexer adds a hidden vehicle for an id it does not know: ask first
        var list = Services.Session.Mav.MAVlist;
        return list.Contains(p.SysId, p.CompId) ? list[p.SysId, p.CompId] : null;
    }

    /// <summary>A copy of the vehicle's current readings (the link's reader adds to the list while we read it).</summary>
    private static List<Proximity.directionState.data> Snapshot(MAVState? vehicle)
    {
        var state = vehicle?.Proximity?.DirectionState;
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

    // an edited settings file could hold "Infinity", which would never finish drawing the rings
    private static double Stored(string key, double fallback, double max) =>
        double.TryParse(Settings.Instance[key], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) && v > 0
            ? Math.Min(v, max)
            : fallback;

    private static void Store(string key, double value) => Settings.Instance[key] = value.ToString(CultureInfo.InvariantCulture);
}
