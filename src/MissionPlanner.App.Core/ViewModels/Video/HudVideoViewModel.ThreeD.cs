using MissionPlanner.App.Core.Video;
using MissionPlanner.App.Core.Synthetic;

namespace MissionPlanner.App.Core.ViewModels.Video;

/// <summary>
/// The 3D view behind the HUD (HUD menu > 3D view): on instead of the camera picture, or as a
/// fallback while the video is not playing. The picture itself comes from
/// <see cref="Synthetic.SyntheticScene"/>; this only decides when.
/// </summary>
public sealed partial class HudVideoViewModel
{
    public const string ThreeDKey = "next_3d_on";
    public const string ThreeDFallbackKey = "next_3d_fallback";
    public const string FovKey = "next_3d_fov";
    public const string TiltKey = "next_3d_tilt";
    public const string DrawDistanceKey = "next_3d_draw_distance_km";
    public const string HighresKey = "next_3d_highres_km";
    public const string TiltServoKey = "next_3d_tilt_servo";
    public const string TiltServoFromKey = "next_3d_tilt_servo_from";
    public const string TiltServoToKey = "next_3d_tilt_servo_to";
    public const string TiltServoReverseKey = "next_3d_tilt_servo_reverse";

    /// <summary>Servo outputs offered for a servo-driven camera tilt (SERVO_OUTPUT_RAW carries 16).</summary>
    public const int MaxTiltServo = 16;

    public const double DefaultTiltServoFrom = -15;
    public const double DefaultTiltServoTo = 90;

    private double _servoTilt = double.NaN;

    /// <summary>Default horizontal field of view offered when a custom one is switched on (a typical FPV lens).</summary>
    public const double DefaultFov = 110;

    private bool _threeD;
    private bool _threeDFallback;

    /// <summary>Show the 3D view behind the HUD (instead of the video).</summary>
    public bool ThreeD
    {
        get => _threeD;
        set
        {
            if (_threeD == value)
                return;
            _threeD = value;
            _store.Set(ThreeDKey, value.ToString());
            _store.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(UsesSynthetic));
        }
    }

    /// <summary>Show the 3D view automatically whenever the video is not playing (stopped, lost, failed).</summary>
    public bool ThreeDFallback
    {
        get => _threeDFallback;
        set
        {
            if (_threeDFallback == value)
                return;
            _threeDFallback = value;
            _store.Set(ThreeDFallbackKey, value.ToString());
            _store.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(UsesSynthetic));
        }
    }

    /// <summary>
    /// FPV camera of the 3D view: horizontal field of view in degrees (30–160), or null to match the
    /// HUD's pitch ladder (about 60° on a 4:3 HUD).
    /// </summary>
    public double? FpvFov
    {
        get => double.TryParse(_store.Get(FovKey), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? Math.Clamp(v, 30, 160)
            : null;
        set
        {
            _store.Set(FovKey, value is { } v ? Math.Clamp(v, 30, 160).ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
            _store.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>FPV camera tilted up on the frame, degrees (−30–70; FPV quads often fly with 20–40).</summary>
    public double FpvTilt
    {
        get => double.TryParse(_store.Get(TiltKey), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? Math.Clamp(v, -30, 70)
            : 0;
        set
        {
            _store.Set(TiltKey, Math.Clamp(value, -30, 70).ToString(System.Globalization.CultureInfo.InvariantCulture));
            _store.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(CameraTilt));
        }
    }

    /// <summary>
    /// Servo output (1–16) that drives the camera tilt, or 0 for the fixed <see cref="FpvTilt"/>.
    /// The angle then follows that output's PWM between <see cref="TiltServoFrom"/> (1000 µs) and
    /// <see cref="TiltServoTo"/> (2000 µs).
    /// </summary>
    public int TiltServo
    {
        get => int.TryParse(_store.Get(TiltServoKey), out var v) ? Math.Clamp(v, 0, MaxTiltServo) : 0;
        set
        {
            _store.Set(TiltServoKey, Math.Clamp(value, 0, MaxTiltServo).ToString(System.Globalization.CultureInfo.InvariantCulture));
            _store.Save();
            ServoSettingChanged();
        }
    }

    /// <summary>Camera angle below the nose line at 1000 µs, degrees (negative: up).</summary>
    public double TiltServoFrom
    {
        get => ReadAngle(TiltServoFromKey, DefaultTiltServoFrom);
        set
        {
            WriteAngle(TiltServoFromKey, value);
            ServoSettingChanged();
        }
    }

    /// <summary>Camera angle below the nose line at 2000 µs, degrees (90: straight down).</summary>
    public double TiltServoTo
    {
        get => ReadAngle(TiltServoToKey, DefaultTiltServoTo);
        set
        {
            WriteAngle(TiltServoToKey, value);
            ServoSettingChanged();
        }
    }

    /// <summary>Swaps the ends: 1000 µs gives <see cref="TiltServoTo"/> and 2000 µs <see cref="TiltServoFrom"/>.</summary>
    public bool TiltServoReverse
    {
        get => bool.TryParse(_store.Get(TiltServoReverseKey), out var v) && v;
        set
        {
            _store.Set(TiltServoReverseKey, value.ToString());
            _store.Save();
            ServoSettingChanged();
        }
    }

    /// <summary>The camera's upward tilt now, degrees: the fixed one, or the tilt servo's position.</summary>
    public double CameraTilt => TiltServo == 0 ? FpvTilt
        : double.IsNaN(_servoTilt) ? -(TiltServoReverse ? TiltServoTo : TiltServoFrom) : _servoTilt;

    /// <summary>Camera angle below the nose line for a servo PWM, degrees (PWM clamped to 1000–2000 µs).</summary>
    public static double ServoTiltAngle(double pwm, double from, double to, bool reverse)
    {
        var f = Math.Clamp((pwm - 1000) / 1000, 0, 1);
        if (reverse)
            f = 1 - f;
        return from + f * (to - from);
    }

    private static readonly System.Reflection.PropertyInfo?[] ServoOuts =
        Enumerable.Range(1, MaxTiltServo).Select(n => typeof(CurrentState).GetProperty($"ch{n}out")).ToArray();

    /// <summary>
    /// Follows the tilt servo from the vehicle's servo outputs; raises <see cref="CameraTilt"/> when it moved.
    /// Called per frame by the views that draw the 3D camera. No output yet (0 µs) keeps the last angle.
    /// </summary>
    public void UpdateServoTilt(CurrentState? cs = null)
    {
        var servo = TiltServo;
        if (servo == 0)
            return;
        cs ??= Services.Session.Cs;
        if (ServoOuts[servo - 1]?.GetValue(cs) is not float pwm || pwm < 500)
            return;
        var up = -ServoTiltAngle(pwm, TiltServoFrom, TiltServoTo, TiltServoReverse);
        if (!double.IsNaN(_servoTilt) && Math.Abs(up - _servoTilt) < 0.1)
            return;
        _servoTilt = up;
        OnPropertyChanged(nameof(CameraTilt));
    }

    private void ServoSettingChanged()
    {
        _servoTilt = double.NaN;
        UpdateServoTilt();
        OnPropertyChanged(nameof(TiltServo));
        OnPropertyChanged(nameof(TiltServoFrom));
        OnPropertyChanged(nameof(TiltServoTo));
        OnPropertyChanged(nameof(TiltServoReverse));
        OnPropertyChanged(nameof(CameraTilt));
    }

    private double ReadAngle(string key, double fallback) =>
        double.TryParse(_store.Get(key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)
            ? Math.Clamp(v, -90, 90)
            : fallback;

    private void WriteAngle(string key, double value)
    {
        _store.Set(key, (double.IsFinite(value) ? Math.Clamp(value, -90, 90) : 0).ToString(System.Globalization.CultureInfo.InvariantCulture));
        _store.Save();
    }

    /// <summary>Drawing distance in kilometres, shared by all 3D views and saved between runs.</summary>
    public double DrawDistanceKm
    {
        get => double.TryParse(_store.Get(DrawDistanceKey), System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)
            ? Math.Clamp(v, SyntheticScene.MinDrawDistance / 1000, SyntheticScene.MaxDrawDistance / 1000)
            : SyntheticScene.DefaultDrawDistance / 1000;
        set
        {
            var km = double.IsFinite(value)
                ? Math.Clamp(value, SyntheticScene.MinDrawDistance / 1000, SyntheticScene.MaxDrawDistance / 1000)
                : SyntheticScene.DefaultDrawDistance / 1000;
            _store.Set(DrawDistanceKey, km.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _store.Save();
            SyntheticScene.For(Services).DrawDistance = km * 1000;
            OnPropertyChanged();
        }
    }

    /// <summary>Hi-res preload radius in kilometres: sharp map tiles preloaded around the
    /// vehicle out to this distance, saved between runs.</summary>
    public double HighresKm
    {
        get => double.TryParse(_store.Get(HighresKey), System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)
            ? Math.Clamp(v, SyntheticScene.MinHighres / 1000, SyntheticScene.MaxHighres / 1000)
            : SyntheticScene.DefaultHighres / 1000;
        set
        {
            var km = double.IsFinite(value)
                ? Math.Clamp(value, SyntheticScene.MinHighres / 1000, SyntheticScene.MaxHighres / 1000)
                : SyntheticScene.DefaultHighres / 1000;
            _store.Set(HighresKey, km.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _store.Save();
            SyntheticScene.For(Services).HighresDistance = km * 1000;
            OnPropertyChanged();
        }
    }

    /// <summary>Opens the 3D view settings (applied live).</summary>
    public Task OpenCameraSettingsAsync() => Services.Windows.ShowDialogAsync(new FpvCameraViewModel(this), 640, 700);

    /// <summary>Whether the HUD shows the 3D view now (rather than video or the plain sky and ground).</summary>
    public bool UsesSynthetic => ThreeD || (ThreeDFallback && State != VideoStreamState.Playing);

    /// <summary>The 3D view replaces the camera picture (the video stops).</summary>
    public void Start3D()
    {
        if (IsActive)
            Stop();
        ThreeD = true;
    }

    public void Stop3D() => ThreeD = false;
}
