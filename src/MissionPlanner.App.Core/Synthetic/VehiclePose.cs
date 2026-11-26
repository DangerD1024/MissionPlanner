namespace MissionPlanner.App.Core.Synthetic;

/// <summary>What the vehicle looks like to the 3D view.</summary>
public enum VehicleShape
{
    Multirotor,
    Plane,
    Rover,
}

/// <summary>One telemetry sample: position (MSL), attitude in degrees, NED velocity in m/s.</summary>
public readonly record struct VehiclePose(
    double Lat,
    double Lng,
    double AltMsl,
    double Roll,
    double Pitch,
    double Yaw,
    double VelNorth,
    double VelEast,
    double VelDown,
    bool Armed,
    bool HasPosition,
    VehicleShape Shape,
    double GroundGuessMsl = 0)
{
    // GroundGuessMsl: the home ground (MSL minus height above home), for flat terrain when no
    // elevation tiles can be had
    public double GroundSpeed => Math.Sqrt(VelNorth * VelNorth + VelEast * VelEast);
}

/// <summary>How a <see cref="PoseTracker"/> predicts and eases; the defaults are the 3D view's.</summary>
public sealed record PoseSmoothing
{
    /// <summary>Time constant of the ease from what was shown to the prediction, position (s).</summary>
    public double PositionBlend { get; init; } = 0.12;

    /// <summary>Time constant of the attitude ease (s).</summary>
    public double AttitudeBlend { get; init; } = 0.05;

    /// <summary>A new position further than this from the last one is a jump: shown at once, not glided to (m).</summary>
    public double ResetDistance { get; init; } = 250;

    /// <summary>
    /// Measures <see cref="ResetDistance"/> from where the last position was predicted to be by now
    /// rather than from the last position itself, so a fast vehicle after a short gap is no jump.
    /// </summary>
    public bool ResetFromPrediction { get; init; }

    /// <summary>Longest extrapolation of a position by its velocity (s).</summary>
    public double MaxPredictPosition { get; init; } = 0.5;

    /// <summary>Longest extrapolation of the attitude by its rate (s).</summary>
    public double MaxPredictAttitude { get; init; } = 0.12;

    /// <summary>
    /// After this long without a new sample the stream has stopped (link lost, log paused): the last
    /// sample is shown where it was, not moved on (s). Infinity keeps the extrapolated pose.
    /// </summary>
    public double StaleAfter { get; init; } = double.PositiveInfinity;
}

/// <summary>
/// Smooths telemetry for a 60 fps view (as ArduDeck's synthetic vision does). Attitude and position
/// arrive as separate messages at their own rates, so each is tracked on its own: the newest sample
/// is extrapolated forward (position by velocity, attitude by its last rate of change, for at most
/// 0.12 s) and what is shown eases towards it over a short time, so the picture neither jumps nor
/// lags. A jump of more than 250 m (a new vehicle, a GPS glitch) starts over. Those times and
/// distances are the default <see cref="PoseSmoothing"/>; the 2D map uses its own.
/// </summary>
public sealed class PoseTracker(PoseSmoothing? smoothing = null)
{
    private readonly PoseSmoothing _o = smoothing ?? new PoseSmoothing();

    private readonly object _gate = new();

    // newest samples and when they arrived
    private VehiclePose? _last;
    private double _attTime = double.NaN, _posTime = double.NaN;
    private double _rollRate, _pitchRate, _yawRate;

    // what was shown last, to ease from
    private VehiclePose? _shown;
    private double _shownTime;

    public VehiclePose? Latest
    {
        get
        {
            lock (_gate)
                return _last;
        }
    }

    /// <summary>A full sample (attitude and position at once) at <paramref name="time"/> seconds.</summary>
    public void Add(VehiclePose pose, double time)
    {
        lock (_gate)
        {
            AddAttitudeLocked(pose.Roll, pose.Pitch, pose.Yaw, time);
            AddPositionLocked(pose, time);
            _last = _last!.Value with
            {
                Armed = pose.Armed,
                Shape = pose.Shape,
                GroundGuessMsl = pose.GroundGuessMsl,
            };
        }
    }

    /// <summary>A new attitude (degrees) only.</summary>
    public void AddAttitude(double roll, double pitch, double yaw, double time)
    {
        lock (_gate)
        {
            if (_last == null)
                return; // nothing to attach it to until the first full sample
            AddAttitudeLocked(roll, pitch, yaw, time);
        }
    }

    /// <summary>A new position and velocity only (attitude fields of <paramref name="pose"/> are ignored).</summary>
    public void AddPosition(VehiclePose pose, double time)
    {
        lock (_gate)
        {
            if (_last == null)
                return;
            AddPositionLocked(pose, time);
        }
    }

    /// <summary>Updates what is not telemetry rate: armed, shape, ground guess.</summary>
    public void SetState(bool armed, VehicleShape shape, double groundGuessMsl)
    {
        lock (_gate)
        {
            if (_last is { } l)
                _last = l with { Armed = armed, Shape = shape, GroundGuessMsl = groundGuessMsl };
        }
    }

    private void AddAttitudeLocked(double roll, double pitch, double yaw, double time)
    {
        if (_last is { } prev && !double.IsNaN(_attTime) && time > _attTime + 0.005)
        {
            var dt = time - _attTime;
            _rollRate = (roll - prev.Roll) / dt;
            _pitchRate = (pitch - prev.Pitch) / dt;
            _yawRate = WrapDeg(yaw - prev.Yaw) / dt;
        }

        _attTime = time;
        _last = (_last ?? default) with { Roll = roll, Pitch = pitch, Yaw = yaw };
    }

    private void AddPositionLocked(VehiclePose pose, double time)
    {
        if (_last is { HasPosition: true } prev && pose.HasPosition)
        {
            var (e, n) = new LocalFrame(prev.Lat, prev.Lng).ToLocal(pose.Lat, pose.Lng);
            if (_o.ResetFromPrediction && !double.IsNaN(_posTime))
            {
                var ahead = Math.Clamp(time - _posTime, 0, _o.MaxPredictPosition);
                e -= prev.VelEast * ahead;
                n -= prev.VelNorth * ahead;
            }

            if (Math.Sqrt(e * e + n * n) > _o.ResetDistance)
                _shown = null; // somewhere else: no gliding across
        }
        else if (_last?.HasPosition != pose.HasPosition)
        {
            _shown = null;
        }

        _posTime = time;
        _last = (_last ?? pose) with
        {
            Lat = pose.Lat,
            Lng = pose.Lng,
            AltMsl = pose.AltMsl,
            VelNorth = pose.VelNorth,
            VelEast = pose.VelEast,
            VelDown = pose.VelDown,
            HasPosition = pose.HasPosition,
        };
    }

    /// <summary>The pose to draw at <paramref name="time"/>.</summary>
    public VehiclePose? At(double time)
    {
        lock (_gate)
        {
            if (_last is not { } last)
                return null;

            var aheadPos = Ahead(time, _posTime, _o.MaxPredictPosition);
            var aheadAtt = Ahead(time, _attTime, _o.MaxPredictAttitude);
            var (lat, lng) = new LocalFrame(last.Lat, last.Lng).ToGeo(last.VelEast * aheadPos, last.VelNorth * aheadPos);
            var predicted = last with
            {
                Lat = lat,
                Lng = lng,
                AltMsl = last.AltMsl - last.VelDown * aheadPos,
                Roll = Math.Clamp(last.Roll + _rollRate * aheadAtt, -180, 180),
                Pitch = Math.Clamp(last.Pitch + _pitchRate * aheadAtt, -90, 90),
                Yaw = Norm360(last.Yaw + _yawRate * aheadAtt),
            };

            if (_shown is { } shown && time > _shownTime)
            {
                var dt = time - _shownTime;
                var p = 1 - Math.Exp(-dt / _o.PositionBlend);
                var a = 1 - Math.Exp(-dt / _o.AttitudeBlend);
                predicted = predicted with
                {
                    Lat = shown.Lat + (predicted.Lat - shown.Lat) * p,
                    Lng = shown.Lng + (predicted.Lng - shown.Lng) * p,
                    AltMsl = shown.AltMsl + (predicted.AltMsl - shown.AltMsl) * p,
                    Roll = shown.Roll + (predicted.Roll - shown.Roll) * a,
                    Pitch = shown.Pitch + (predicted.Pitch - shown.Pitch) * a,
                    Yaw = Norm360(shown.Yaw + WrapDeg(predicted.Yaw - shown.Yaw) * a),
                };
            }

            _shown = predicted;
            _shownTime = time;
            return predicted;
        }
    }

    /// <summary>How far ahead of a sample taken at <paramref name="sampleTime"/> to extrapolate (s).</summary>
    private double Ahead(double time, double sampleTime, double max)
    {
        if (double.IsNaN(sampleTime) || time - sampleTime > _o.StaleAfter)
            return 0;
        return Math.Clamp(time - sampleTime, 0, max);
    }

    public void Reset()
    {
        lock (_gate)
        {
            _last = null;
            _shown = null;
            _attTime = _posTime = double.NaN;
            _rollRate = _pitchRate = _yawRate = 0;
        }
    }

    /// <summary>-180..180.</summary>
    public static double WrapDeg(double d)
    {
        d %= 360;
        if (d > 180)
            d -= 360;
        if (d < -180)
            d += 360;
        return d;
    }

    public static double Norm360(double d)
    {
        d %= 360;
        return d < 0 ? d + 360 : d;
    }
}

/// <summary>
/// Matches the reported altitude to the terrain model: while the vehicle sits disarmed and still,
/// the difference between the ground height and the reported MSL altitude is learnt and then kept,
/// so the view sits on the terrain at take-off however the GPS or the DEM is off (ArduDeck's datum).
/// </summary>
public sealed class AltitudeDatum
{
    /// <summary>Ground minus reported altitude while on the ground; added to every altitude.</summary>
    public double Offset { get; private set; }

    public bool Learnt { get; private set; }

    /// <summary>Feeds the ground height under the vehicle (null while unknown) with the pose.</summary>
    public void Update(VehiclePose pose, double? ground, double eyeHeight)
    {
        if (ground is not { } g || pose.Armed || pose.GroundSpeed > 0.5 || !pose.HasPosition)
            return;
        var target = g + eyeHeight - pose.AltMsl;
        Offset = Learnt ? Offset + (target - Offset) * 0.2 : target;
        Learnt = true;
    }

    /// <summary>The altitude to draw: the corrected one, never below the ground plus <paramref name="floor"/>.</summary>
    public double EyeAltitude(double altMsl, double? ground, double floor = 2) =>
        ground is { } g ? Math.Max(altMsl + Offset, g + floor) : altMsl + Offset;

    public void Reset()
    {
        Offset = 0;
        Learnt = false;
    }
}
