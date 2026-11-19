using CommunityToolkit.Mvvm.ComponentModel;
using MissionPlanner.ArduPilot;

namespace MissionPlanner.App.Core.ViewModels.FlightData;

/// <summary>Dial ranges: round numbers that grow with the value and come back down once it is well below.</summary>
public static class GaugeScale
{
    /// <summary>The smallest 1-2-5 step (1, 2, 5, 10, 20, 50...) at or above both values.</summary>
    public static double NiceMax(double value, double minimum)
    {
        var v = Math.Max(Math.Abs(value), Math.Max(minimum, 1e-6));
        if (double.IsNaN(v) || double.IsInfinity(v))
            return minimum;
        var decade = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (var m in new[] { 1.0, 2.0, 5.0, 10.0 })
        {
            if (m * decade >= v - 1e-9)
                return m * decade;
        }

        return 10 * decade;
    }

    /// <summary>
    /// The next range for a dial showing <paramref name="value"/>: grows (with 10 % headroom)
    /// when the needle would pass the end, shrinks when it stays below a quarter of the range.
    /// </summary>
    public static double Next(double current, double value, double minimum)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return current;
        var need = Math.Abs(value) * 1.1;
        if (need > current || Math.Abs(value) < current * 0.25)
            return Math.Max(minimum, NiceMax(need, minimum));
        return current;
    }
}

/// <summary>
/// Gauges tab, after Mission Planner's: speed (ground and air), altitude, vertical speed and
/// heading dials. Values are copied at 10 Hz while the tab is visible.
/// </summary>
public sealed partial class GaugesViewModel : PageViewModel
{
    private long _tick;

    public GaugesViewModel(AppServices services)
        : base("Gauges")
    {
        Services = services;
    }

    public AppServices Services { get; }

    [ObservableProperty]
    private double _groundspeed;

    [ObservableProperty]
    private double _airspeed;

    /// <summary>Planes, or anything reporting an airspeed, get a second needle.</summary>
    [ObservableProperty]
    private bool _hasAirspeed;

    [ObservableProperty]
    private double _speedMax = 20;

    [ObservableProperty]
    private double _altitude;

    [ObservableProperty]
    private double _altitudeMax = 100;

    [ObservableProperty]
    private double _verticalSpeed;

    [ObservableProperty]
    private double _verticalSpeedMax = 5;

    [ObservableProperty]
    private double _heading;

    [ObservableProperty]
    private double _navBearing;

    [ObservableProperty]
    private bool _showNavBearing;

    [ObservableProperty]
    private string _speedUnit = Units.Speed;

    [ObservableProperty]
    private string _altitudeUnit = Units.Altitude;

    public override void OnShown() => Update();

    public override void Tick()
    {
        if (_tick++ % 2 == 0)
            Update();
    }

    public void Update()
    {
        var cs = Services.Session.Cs;
        Groundspeed = Round(cs.groundspeed);
        Airspeed = Round(cs.airspeed);
        HasAirspeed = cs.firmware == Firmwares.ArduPlane || Math.Abs(cs.airspeed) > 0.05;
        SpeedMax = GaugeScale.Next(SpeedMax, Math.Max(Groundspeed, HasAirspeed ? Airspeed : 0), 10);

        Altitude = Round(cs.alt);
        AltitudeMax = GaugeScale.Next(AltitudeMax, Altitude, 50);

        VerticalSpeed = Round(cs.verticalspeed);
        VerticalSpeedMax = GaugeScale.Next(VerticalSpeedMax, VerticalSpeed, 5);

        Heading = Round(cs.yaw);
        NavBearing = Round(cs.nav_bearing);
        ShowNavBearing = cs.armed || cs.wp_dist > 0;

        SpeedUnit = Units.Speed;
        AltitudeUnit = Units.Altitude;
    }

    // a hundredth is below what a needle can show; rounding stops redraws for sensor noise
    private static double Round(double v) => double.IsNaN(v) ? 0 : Math.Round(v, 2);
}
