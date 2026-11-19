using System.Globalization;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.OptionalHardware;

/// <summary>One COMPASSMOT_STATUS report.</summary>
/// <param name="ThrottlePercent">Throttle, 0-100 % (the message sends tenths of a percent).</param>
/// <param name="CurrentAmps">Battery current, A.</param>
/// <param name="InterferencePercent">Magnetic interference as a percentage of the earth's field.</param>
/// <param name="CompX">Compensation found so far, X.</param>
/// <param name="CompY">Compensation found so far, Y.</param>
/// <param name="CompZ">Compensation found so far, Z.</param>
public readonly record struct CompassMotSample(
    double ThrottlePercent, double CurrentAmps, double InterferencePercent, double CompX, double CompY, double CompZ)
{
    public static CompassMotSample From(MAVLink.mavlink_compassmot_status_t s) =>
        new(s.throttle / 10.0, s.current, s.interference, s.CompensationX, s.CompensationY, s.CompensationZ);
}

/// <summary>How bad the interference at full throttle is, by ArduPilot's guidance.</summary>
public enum InterferenceRating
{
    None,
    Good,
    Acceptable,
    Bad,
}

/// <summary>
/// The compass/motor interference calibration's data (Mission Planner's ConfigCompassMot):
/// interference and current against throttle, kept sorted by throttle for the graph, plus the
/// latest report for the status text.
/// </summary>
public sealed class CompassMotRecorder
{
    private readonly object _gate = new();
    private readonly List<CompassMotSample> _samples = new();

    /// <summary>Most samples kept (COMPASSMOT_STATUS comes at about 20 Hz).</summary>
    public int Capacity { get; init; } = 20000;

    public CompassMotSample? Latest { get; private set; }

    /// <summary>Highest interference seen.</summary>
    public double MaxInterference { get; private set; }

    public int Count
    {
        get
        {
            lock (_gate)
                return _samples.Count;
        }
    }

    /// <summary>Adds a report; safe to call from the link's reader thread.</summary>
    public void Add(CompassMotSample sample)
    {
        lock (_gate)
        {
            if (_samples.Count >= Capacity)
                _samples.RemoveAt(0);
            _samples.Add(sample);
            Latest = sample;
            if (sample.InterferencePercent > MaxInterference)
                MaxInterference = sample.InterferencePercent;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _samples.Clear();
            Latest = null;
            MaxInterference = 0;
        }
    }

    /// <summary>A copy of the samples ordered by throttle (for plotting against throttle).</summary>
    public IReadOnlyList<CompassMotSample> ByThrottle()
    {
        lock (_gate)
            return _samples.OrderBy(s => s.ThrottlePercent).ToList();
    }

    /// <summary>Mission Planner's status text for a report.</summary>
    public static string Describe(CompassMotSample s) => string.Create(CultureInfo.InvariantCulture,
        $"Current: {s.CurrentAmps:0.00} A\nx,y,z {s.CompX:0.00},{s.CompY:0.00},{s.CompZ:0.00}\nThrottle: {s.ThrottlePercent:0.#} %\nInterference: {s.InterferencePercent:0} %");

    /// <summary>
    /// ArduPilot's advice for compassmot: under 30 % interference is fine, 31-60 % may be
    /// acceptable, above 60 % the compass should be moved away from the power wiring.
    /// </summary>
    public static InterferenceRating Rate(double interferencePercent, bool any) => !any
        ? InterferenceRating.None
        : interferencePercent <= 30 ? InterferenceRating.Good
        : interferencePercent <= 60 ? InterferenceRating.Acceptable
        : InterferenceRating.Bad;

    public static string RatingText(InterferenceRating rating) => rating switch
    {
        InterferenceRating.Good => "Interference is low (30 % or less): good.",
        InterferenceRating.Acceptable => "Interference is 31-60 %: may be acceptable; consider moving the compass away from power wires.",
        InterferenceRating.Bad => "Interference is above 60 %: move the compass further from the motors, ESCs and power wires.",
        _ => "",
    };
}
