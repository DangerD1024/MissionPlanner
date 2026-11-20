using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.Video;

/// <summary>
/// The 3D view's FPV camera (HUD menu > 3D view > FPV camera…): field of view and uptilt, applied
/// to the HUD as they are moved; Cancel puts back what was there.
/// </summary>
public sealed partial class FpvCameraViewModel : PageViewModel, IDialogPage
{
    private readonly HudVideoViewModel _owner;
    private readonly double? _originalFov;
    private readonly double _originalTilt;

    public FpvCameraViewModel(HudVideoViewModel owner)
        : base("FPV camera")
    {
        _owner = owner;
        _originalFov = owner.FpvFov;
        _originalTilt = owner.FpvTilt;
        _customFov = _originalFov != null;
        _fov = _originalFov ?? HudVideoViewModel.DefaultFov;
        _tilt = _originalTilt;
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

    public string FovText => CustomFov ? $"{Fov:0}°" : "";

    public string TiltText => $"{Tilt:0}°";

    partial void OnCustomFovChanged(bool value) => Apply();

    partial void OnFovChanged(double value) => Apply();

    partial void OnTiltChanged(double value) => Apply();

    private void Apply()
    {
        _owner.FpvFov = CustomFov ? Math.Round(Fov) : null;
        _owner.FpvTilt = Math.Round(Tilt);
        OnPropertyChanged(nameof(FovText));
        OnPropertyChanged(nameof(TiltText));
    }

    [RelayCommand]
    private void Reset()
    {
        CustomFov = false;
        Fov = HudVideoViewModel.DefaultFov;
        Tilt = 0;
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, true);

    [RelayCommand]
    private void Cancel()
    {
        _owner.FpvFov = _originalFov;
        _owner.FpvTilt = _originalTilt;
        CloseRequested?.Invoke(this, false);
    }
}
