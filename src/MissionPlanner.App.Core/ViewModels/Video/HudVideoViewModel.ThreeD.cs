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
        }
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

    /// <summary>Opens the 3D view settings (applied live).</summary>
    public Task OpenCameraSettingsAsync() => Services.Windows.ShowDialogAsync(new FpvCameraViewModel(this), 560, 460);

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
