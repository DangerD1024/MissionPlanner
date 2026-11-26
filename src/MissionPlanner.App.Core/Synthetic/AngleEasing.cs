namespace MissionPlanner.App.Core.Synthetic;

/// <summary>
/// Glides an angle to its newest reading instead of jumping there: a critically damped follower,
/// advanced once per rendered frame, that eases in and out and is about 90 % of the way after one
/// reading interval. It never passes the reading and never moves away from it, so a jump becomes one
/// smooth glide without overshoot and a steady reading stays put. Readings that keep moving (a tilt
/// knob turned) are trailed by about one reading's step, and that lag does not grow. Not thread-safe.
/// </summary>
public sealed class AngleEasing
{
    /// <summary>
    /// ω·T: a step from rest is 91 % done after one interval T (95 % after 1.2 T). Stiffer pulses
    /// visibly while the readings keep moving; softer trails further behind.
    /// </summary>
    private const double SettleOmegaT = 4.0;

    /// <summary>Degrees: this close, the angle sits on the reading.</summary>
    private const double Snap = 0.01;

    /// <summary>Shortest and longest reading intervals the glide is timed for, seconds.</summary>
    public const double MinInterval = 0.02;

    public const double MaxInterval = 1.0;

    private double _time = double.NaN;
    private double _velocity;

    /// <summary>The newest reading (NaN until there is one).</summary>
    public double Target { get; private set; } = double.NaN;

    /// <summary>The angle to draw now (NaN until the first reading).</summary>
    public double Value { get; private set; } = double.NaN;

    /// <summary>A new reading to glide to. The first one is taken as it is.</summary>
    public void SetTarget(double angle)
    {
        if (!double.IsFinite(angle))
            return;
        Target = angle;
        if (double.IsNaN(Value))
        {
            Value = angle;
            _velocity = 0;
        }
    }

    /// <summary>Goes straight to the angle (settings changed): no glide. NaN forgets it, so the next reading is taken as it is.</summary>
    public void Jump(double angle)
    {
        Target = Value = double.IsFinite(angle) ? angle : double.NaN;
        _velocity = 0;
    }

    /// <summary>
    /// The angle for a frame drawn at <paramref name="now"/> (seconds on any steady clock), gliding over
    /// about <paramref name="interval"/> seconds (the time between readings). NaN before the first reading.
    /// </summary>
    public double At(double now, double interval)
    {
        if (!double.IsFinite(now))
            return Value;
        if (double.IsNaN(_time) || now < _time - MaxInterval)
        {
            // first frame, or another clock: start timing here
            _time = now;
            return Value;
        }

        var dt = now - _time;
        if (dt <= 0)
            return Value;
        _time = now;
        if (!double.IsNaN(Target) && !double.IsNaN(Value))
            Advance(Math.Min(dt, MaxInterval), double.IsFinite(interval) ? Math.Clamp(interval, MinInterval, MaxInterval) : MaxInterval);
        return Value;
    }

    private void Advance(double dt, double interval)
    {
        var x = Value - Target;
        if (Math.Abs(x) < Snap)
        {
            Value = Target;
            _velocity = 0;
            return;
        }

        // still heading the way of an older reading, away from this one: start from rest
        if (_velocity * x > 0)
            _velocity = 0;

        // the exact step of a critically damped spring towards a fixed reading: frame rate independent
        var omega = SettleOmegaT / interval;
        var decay = Math.Exp(-omega * dt);
        var push = (_velocity + omega * x) * dt;
        var next = (x + push) * decay;
        var velocity = (_velocity - omega * push) * decay;
        if (next * x <= 0 || Math.Abs(next) < Snap)
        {
            // there (or it would pass the reading): stop on it
            Value = Target;
            _velocity = 0;
            return;
        }

        Value = Target + next;
        _velocity = velocity;
    }
}
