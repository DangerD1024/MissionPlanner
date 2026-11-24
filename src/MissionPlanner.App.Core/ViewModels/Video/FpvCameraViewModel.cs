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
        _owner.DrawDistanceKm = _originalDrawDistance;
        _owner.HighresKm = _originalHighres;
        CloseRequested?.Invoke(this, false);
    }
}
