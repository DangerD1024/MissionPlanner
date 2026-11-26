using MissionPlanner.App.Core.Synthetic;

namespace MissionPlanner.App.Core.ViewModels.FlightData;

/// <summary>
/// Where the 2D map draws the vehicle between position reports, which come only 2–10 times a
/// second: the newest report moved on by its velocity and corrections eased out, as the 3D view
/// does (<see cref="PoseTracker"/>, here with the map's <see cref="Smoothing"/>). A jump (a new
/// SITL home, a teleport) is shown at once; when reports stop the vehicle settles where it was last
/// reported. The heading is predicted by its rate and turns the short way round; the course and
/// navigation bearings, drawn as lines from the vehicle, are eased.
/// <para>
/// Reports may come from any thread. <see cref="Advance"/>, <see cref="SetBearings"/> and the
/// shown values belong to one thread (the UI's). Times are seconds on one monotonic clock.
/// </para>
/// </summary>
public sealed class MapVehicleMotion
{
    /// <summary>
    /// The map's tuning: position at 2–10 Hz and attitude at about 4 Hz (the 3D view asks for faster
    /// rates), so predictions run longer; a report 100 m away from where the vehicle was expected is
    /// a jump; after 2 s without reports the vehicle is shown where it was last reported.
    /// </summary>
    public static PoseSmoothing Smoothing { get; } = new()
    {
        PositionBlend = 0.12,
        AttitudeBlend = 0.08,
        ResetDistance = 100,
        ResetFromPrediction = true,
        MaxPredictPosition = 1.0,
        MaxPredictAttitude = 0.3,
        StaleAfter = 2.0,
    };

    private const double BearingBlend = 0.12; // s

    /// <summary>Smaller changes than these are not worth drawing.</summary>
    private const double RedrawPixels = 0.5;
    private const double RedrawDegrees = 0.25;

    /// <summary>Faster than this is worth drawing every display frame (<see cref="IsMoving"/>).</summary>
    private const double MovingPixelsPerSecond = 2;
    private const double MovingDegreesPerSecond = 2;

    /// <summary>The shortest time over which <see cref="IsMoving"/> is measured (calls may come close together).</summary>
    private const double RateWindow = 0.01; // s

    private readonly object _gate = new();
    private readonly PoseTracker _tracker = new(Smoothing);

    // reports, under _gate
    private bool _hasHeading;
    private double _heading;
    private VehiclePose _lastPosition;

    // UI thread: bearings
    private readonly double[] _bearingTarget = new double[3];
    private readonly double[] _bearing = new double[3];
    private bool _hasBearings;
    private double _lastAdvance = double.NaN;

    // UI thread: what was drawn last, and the pose IsMoving is measured from
    private bool _drawnHas;
    private double _drawnLat, _drawnLng, _drawnHeading;
    private readonly double[] _drawnBearing = new double[3];
    private double _rateTime = double.NaN;
    private double _rateLat, _rateLng, _rateHeading;
    private readonly double[] _rateBearing = new double[3];

    /// <summary>Whether a position has been reported (since the last <see cref="Clear"/>).</summary>
    public bool HasReports => _tracker.Latest != null;

    /// <summary>Whether there is a vehicle to draw, as of the last <see cref="Advance"/>.</summary>
    public bool HasPosition { get; private set; }

    public double Lat { get; private set; }

    public double Lng { get; private set; }

    /// <summary>Degrees, 0..360.</summary>
    public double Heading { get; private set; }

    public double GroundCourse => _bearing[0];

    public double NavBearing => _bearing[1];

    public double TargetBearing => _bearing[2];

    /// <summary>
    /// Whether what is shown is changing fast enough to be drawn every display frame: the vehicle
    /// gliding or turning, or a correction being eased out. False when it is at rest, or so slow at
    /// this zoom that occasional updates show it as well.
    /// </summary>
    public bool IsMoving { get; private set; }

    /// <summary>A position report: degrees, and the ground velocity north and east in m/s.</summary>
    public void AddPosition(double lat, double lng, double velNorth, double velEast, double time)
    {
        var pose = new VehiclePose(lat, lng, 0, 0, 0, 0, velNorth, velEast, 0, false, true, VehicleShape.Plane);
        lock (_gate)
        {
            _lastPosition = pose;
            if (_tracker.Latest == null)
                _tracker.Add(pose with { Yaw = _heading }, time);
            else
                _tracker.AddPosition(pose, time);
        }
    }

    /// <summary>A heading (yaw) report, degrees.</summary>
    public void AddHeading(double heading, double time)
    {
        heading = PoseTracker.Norm360(heading);
        lock (_gate)
        {
            var first = !_hasHeading;
            _hasHeading = true;
            _heading = heading;
            if (_tracker.Latest == null)
                return; // kept for the first position
            if (first)
            {
                // the vehicle so far had no heading: start again with one, rather than read a turn
                // from nothing to this into the rate the heading is predicted with
                _tracker.Reset();
                _tracker.Add(_lastPosition with { Yaw = heading }, time);
            }
            else
            {
                _tracker.AddAttitude(0, 0, heading, time);
            }
        }
    }

    /// <summary>Course over ground, desired heading and bearing to the waypoint, degrees (UI thread).</summary>
    public void SetBearings(double groundCourse, double navBearing, double targetBearing)
    {
        _bearingTarget[0] = PoseTracker.Norm360(groundCourse);
        _bearingTarget[1] = PoseTracker.Norm360(navBearing);
        _bearingTarget[2] = PoseTracker.Norm360(targetBearing);
        if (!_hasBearings)
        {
            _hasBearings = true;
            Array.Copy(_bearingTarget, _bearing, 3);
        }
    }

    /// <summary>No position (no fix, another vehicle): forgets the reports.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _tracker.Reset();
            _hasHeading = false;
            _heading = 0;
        }
    }

    /// <summary>
    /// Moves what is shown to <paramref name="time"/>. True if that changed what is drawn enough to
    /// draw again, at <paramref name="metresPerPixel"/> (the map's scale at the vehicle).
    /// </summary>
    public bool Advance(double time, double metresPerPixel)
    {
        // the tracker shows its bare prediction when asked twice for one time: no ease, a jump
        if (time <= _lastAdvance)
            return false;
        var mpp = metresPerPixel > 0 && double.IsFinite(metresPerPixel) ? metresPerPixel : 1;

        var dt = double.IsNaN(_lastAdvance) ? 0 : time - _lastAdvance;
        _lastAdvance = time;
        var k = 1 - Math.Exp(-dt / BearingBlend);
        for (var i = 0; i < 3; i++)
            _bearing[i] = PoseTracker.Norm360(_bearing[i] + PoseTracker.WrapDeg(_bearingTarget[i] - _bearing[i]) * k);

        var pose = _tracker.At(time);
        var has = pose is { HasPosition: true };
        HasPosition = has;
        if (has)
        {
            Lat = pose!.Value.Lat;
            Lng = pose.Value.Lng;
            Heading = pose.Value.Yaw;
        }

        MeasureMotion(time, mpp);

        if (has == _drawnHas && (!has || !Changed(_drawnLat, _drawnLng, _drawnHeading, _drawnBearing, mpp, RedrawPixels, RedrawDegrees)))
            return false;

        _drawnHas = has;
        _drawnLat = Lat;
        _drawnLng = Lng;
        _drawnHeading = Heading;
        Array.Copy(_bearing, _drawnBearing, 3);
        return true;
    }

    /// <summary>How fast what is shown changes, over at least <see cref="RateWindow"/>.</summary>
    private void MeasureMotion(double time, double mpp)
    {
        if (!HasPosition)
        {
            IsMoving = false;
            _rateTime = double.NaN;
            return;
        }

        if (!double.IsNaN(_rateTime))
        {
            var span = time - _rateTime;
            if (span < RateWindow)
                return;
            IsMoving = Changed(_rateLat, _rateLng, _rateHeading, _rateBearing, mpp, MovingPixelsPerSecond * span, MovingDegreesPerSecond * span);
        }

        _rateTime = time;
        _rateLat = Lat;
        _rateLng = Lng;
        _rateHeading = Heading;
        Array.Copy(_bearing, _rateBearing, 3);
    }

    private bool Changed(double lat, double lng, double heading, double[] bearings, double mpp, double pixels, double degrees)
    {
        if (TrackBuffer.DistanceMetres(lat, lng, Lat, Lng) / mpp >= pixels)
            return true;
        if (Math.Abs(PoseTracker.WrapDeg(Heading - heading)) >= degrees)
            return true;
        for (var i = 0; i < 3; i++)
        {
            if (Math.Abs(PoseTracker.WrapDeg(_bearing[i] - bearings[i])) >= degrees)
                return true;
        }

        return false;
    }
}
