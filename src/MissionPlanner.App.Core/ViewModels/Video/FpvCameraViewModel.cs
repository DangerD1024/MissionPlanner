using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.Video;

/// <summary>
/// The 3D view's drawing distance and FPV camera, applied live; Cancel restores the original values.
/// </summary>
public sealed partial class FpvCameraViewModel : PageViewModel, IDialogPage
{
    private readonly HudVideoViewModel _owner;
    private readonly double? _originalFov;
    private readonly double _originalTilt;
    private readonly double _originalDrawDistance;
    private readonly double _originalHighres;
    private readonly int _originalServo;
    private readonly double _originalServoFrom;
    private readonly double _originalServoTo;
    private readonly bool _originalServoReverse;

    public FpvCameraViewModel(HudVideoViewModel owner)
        : base("3D view settings")
    {
        _owner = owner;
        _originalFov = owner.FpvFov;
        _originalTilt = owner.FpvTilt;
        _customFov = _originalFov != null;
        _fov = _originalFov ?? HudVideoViewModel.DefaultFov;
        _tilt = _originalTilt;
        _drawDistanceKm = _originalDrawDistance = owner.DrawDistanceKm;
        _highresKm = _originalHighres = owner.HighresKm;
        _originalServo = owner.TiltServo;
        _servoTilt = _originalServo > 0;
        _selectedServo = _originalServo > 0 ? _originalServo : 5;
        _originalServoFrom = owner.TiltServoFrom;
        _originalServoTo = owner.TiltServoTo;
        _servoFrom = (decimal)_originalServoFrom;
        _servoTo = (decimal)_originalServoTo;
        _servoReverse = _originalServoReverse = owner.TiltServoReverse;
    }

    /// <summary>Servo outputs the tilt can follow (1–16).</summary>
    public IReadOnlyList<int> ServoNumbers { get; } = Enumerable.Range(1, HudVideoViewModel.MaxTiltServo).ToArray();

    /// <summary>The rates offered for <see cref="ServoRate"/>: those of SETTINGS › Telemetry rates.</summary>
    public IReadOnlyList<ConfigScreens.RateChoice> ServoRateChoices => ConfigScreens.PlannerSettingsViewModel.RateChoices;

    private int? _originalServoRate;

    /// <summary>
    /// How often the vehicle sends its servo outputs: the RC telemetry rate (SERVO_OUTPUT_RAW is in the
    /// RC_CHANNELS stream), the same setting as SETTINGS › App Settings › Telemetry rates › RC, stored and
    /// applied to the vehicle the same way.
    /// </summary>
    public ConfigScreens.RateChoice ServoRate
    {
        get => ConfigScreens.PlannerSettings.Choice(ConfigScreens.PlannerSettings.CurrentRate(_owner.Services.Session, ConfigScreens.PlannerSettings.StreamGroup.Rc));
        set
        {
            if (value == null || value.Hz == ServoRate.Hz)
                return;
            _originalServoRate ??= ServoRate.Hz;
            ConfigScreens.PlannerSettings.SetRate(_owner.Services, ConfigScreens.PlannerSettings.StreamGroup.Rc, value.Hz);
            OnPropertyChanged();
        }
    }

    public override void OnShown() => _owner.Services.StreamRateChanged += OnStreamRateChanged;

    public override void OnHidden() => _owner.Services.StreamRateChanged -= OnStreamRateChanged;

    private void OnStreamRateChanged(object? sender, ConfigScreens.PlannerSettings.StreamGroup group)
    {
        if (group == ConfigScreens.PlannerSettings.StreamGroup.Rc)
            OnPropertyChanged(nameof(ServoRate));
    }

    /// <summary>Off: a fixed tilt (<see cref="Tilt"/>). On: the tilt follows a servo output.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ManualTilt))]
    private bool _servoTilt;

    /// <summary>The fixed tilt is used (the other radio button).</summary>
    public bool ManualTilt
    {
        get => !ServoTilt;
        set => ServoTilt = !value;
    }

    [ObservableProperty]
    private int _selectedServo;

    /// <summary>Angle below the nose line at 1000 µs (negative: up).</summary>
    [ObservableProperty]
    private decimal _servoFrom;

    /// <summary>Angle below the nose line at 2000 µs (90: straight down).</summary>
    [ObservableProperty]
    private decimal _servoTo;

    [ObservableProperty]
    private bool _servoReverse;

    /// <summary>The servo's output now and the angle it gives, refreshed by <see cref="Tick"/>.</summary>
    [ObservableProperty]
    private string _servoNowText = "";

    partial void OnServoTiltChanged(bool value) => Apply();

    partial void OnSelectedServoChanged(int value) => Apply();

    partial void OnServoFromChanged(decimal value) => Apply();

    partial void OnServoToChanged(decimal value) => Apply();

    partial void OnServoReverseChanged(bool value) => Apply();

    public override void Tick() => UpdateServoNow();

    private void UpdateServoNow()
    {
        if (!ServoTilt)
        {
            ServoNowText = "";
            return;
        }

        var from = (double)ServoFrom;
        var to = (double)ServoTo;
        var pwm = typeof(CurrentState).GetProperty($"ch{SelectedServo}out")?.GetValue(_owner.Services.Session.Cs) is float f ? f : 0;
        var ends = $"1000 µs: {HudVideoViewModel.ServoTiltAngle(1000, from, to, ServoReverse):0}°, " +
                   $"2000 µs: {HudVideoViewModel.ServoTiltAngle(2000, from, to, ServoReverse):0}°";
        ServoNowText = pwm < 500
            ? $"SERVO{SelectedServo}: no output from the vehicle yet ({ends})."
            : $"SERVO{SelectedServo} now {pwm:0} µs = {HudVideoViewModel.ServoTiltAngle(pwm, from, to, ServoReverse):0}° below the nose ({ends}).";
    }

    public event EventHandler<bool>? CloseRequested;

    /// <summary>Off: the field of view matches the HUD's pitch ladder (the default).</summary>
    [ObservableProperty]
    private bool _customFov;

    /// <summary>Horizontal field of view, degrees.</summary>
    [ObservableProperty]
    private double _fov;

    /// <summary>Camera tilted up, degrees.</summary>
    [ObservableProperty]
    private double _tilt;

    [ObservableProperty]
    private double _drawDistanceKm;

    /// <summary>Hi-res preload radius, kilometres.</summary>
    [ObservableProperty]
    private double _highresKm;

    public string FovText => CustomFov ? $"{Fov:0}°" : "";

    /// <summary>Diagonal-FOV hint for a 16:9 frame, derived from the horizontal FOV
    /// (same focal length across axes): dFOV = 2·atan(tan(hFOV/2)·√(16²+9²)/16).</summary>
    public string DiagonalFovText => CustomFov ? $"≈ {DiagonalFov:0}° diagonal (16:9)" : "";

    private double DiagonalFov =>
        2 * Math.Atan(Math.Tan(Fov * Math.PI / 360) * Math.Sqrt(337) / 16) * 180 / Math.PI;

    public string TiltText => $"{Tilt:0}°";

    public string DrawDistanceText => $"{DrawDistanceKm:0} km";

    public string HighresText => $"{HighresKm:0.#} km";

    partial void OnCustomFovChanged(bool value) => Apply();

    partial void OnFovChanged(double value) => Apply();

    partial void OnTiltChanged(double value) => Apply();

    partial void OnDrawDistanceKmChanged(double value) => Apply();

    partial void OnHighresKmChanged(double value) => Apply();

    private void Apply()
    {
        _owner.FpvFov = CustomFov ? Math.Round(Fov) : null;
        _owner.FpvTilt = Math.Round(Tilt);
        _owner.TiltServoFrom = (double)ServoFrom;
        _owner.TiltServoTo = (double)ServoTo;
        _owner.TiltServoReverse = ServoReverse;
        _owner.TiltServo = ServoTilt ? SelectedServo : 0;
        UpdateServoNow();
        _owner.DrawDistanceKm = Math.Round(DrawDistanceKm);
        _owner.HighresKm = HighresKm;
        OnPropertyChanged(nameof(FovText));
        OnPropertyChanged(nameof(DiagonalFovText));
        OnPropertyChanged(nameof(TiltText));
        OnPropertyChanged(nameof(DrawDistanceText));
        OnPropertyChanged(nameof(HighresText));
    }

    [RelayCommand]
    private void Reset()
    {
        CustomFov = false;
        Fov = HudVideoViewModel.DefaultFov;
        Tilt = 0;
        ServoTilt = false;
        ServoFrom = (decimal)HudVideoViewModel.DefaultTiltServoFrom;
        ServoTo = (decimal)HudVideoViewModel.DefaultTiltServoTo;
        ServoReverse = false;
        DrawDistanceKm = Synthetic.SyntheticScene.DefaultDrawDistance / 1000;
        HighresKm = Synthetic.SyntheticScene.DefaultHighres / 1000;
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, true);

    [RelayCommand]
    private void Cancel()
    {
        _owner.FpvFov = _originalFov;
        _owner.FpvTilt = _originalTilt;
        _owner.TiltServoFrom = _originalServoFrom;
        _owner.TiltServoTo = _originalServoTo;
        _owner.TiltServoReverse = _originalServoReverse;
        _owner.TiltServo = _originalServo;
        if (_originalServoRate is { } rate)
            ServoRate = ConfigScreens.PlannerSettings.Choice(rate);
        _owner.DrawDistanceKm = _originalDrawDistance;
        _owner.HighresKm = _originalHighres;
        CloseRequested?.Invoke(this, false);
    }
}
