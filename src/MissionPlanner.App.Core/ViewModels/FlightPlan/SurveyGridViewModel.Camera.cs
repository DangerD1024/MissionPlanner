using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.FlightPlan;

/// <summary>
/// The camera part of the Survey (Grid) dialog, as Mission Planner's: pick a camera and the overlaps,
/// and the lane spacing and trigger distance follow from the altitude; GSD and photo footprints shown.
/// </summary>
public sealed partial class SurveyGridViewModel
{
    private const string CameraKey = "next_survey_camera";
    private const string OverlapKey = "next_survey_overlap";
    private const string SidelapKey = "next_survey_sidelap";
    private const string PortraitKey = "next_survey_portrait";
    private const string FootprintsKey = "next_survey_footprints";

    /// <summary>"Manual" (lane spacing and trigger distance typed in), then the camera database.</summary>
    public IReadOnlyList<CameraChoice> Cameras { get; } =
        new[] { new CameraChoice(null) }.Concat(CameraDatabase.All.Select(c => new CameraChoice(c))).ToList();

    public sealed record CameraChoice(CameraSpec? Camera)
    {
        public override string ToString() => Camera?.Name ?? "Manual (no camera)";
    }

    private bool _cameraLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCamera))]
    private CameraChoice? _selectedCamera;

    public bool HasCamera => SelectedCamera?.Camera != null;

    [ObservableProperty]
    private decimal _overlap = 70;

    [ObservableProperty]
    private decimal _sidelap = 60;

    [ObservableProperty]
    private bool _portrait;

    [ObservableProperty]
    private bool _showFootprints = true;

    [ObservableProperty]
    private string _cameraText = "";

    /// <summary>Photo footprint in metres (0 without a camera), for the preview.</summary>
    public double FootprintAcross { get; private set; }

    public double FootprintAlong { get; private set; }

    /// <summary>Restores the camera choice (called once when the dialog opens).</summary>
    private void LoadCameraSettings()
    {
        if (_cameraLoaded)
            return;
        _cameraLoaded = true;
        var name = Settings.Instance[CameraKey];
        _selectedCamera = Cameras.FirstOrDefault(c => c.Camera?.Name == name) ?? Cameras[0];
        _overlap = (decimal)Settings.Instance.GetDouble(OverlapKey, 70);
        _sidelap = (decimal)Settings.Instance.GetDouble(SidelapKey, 60);
        _portrait = Settings.Instance.GetBoolean(PortraitKey, false);
        _showFootprints = Settings.Instance.GetBoolean(FootprintsKey, true);
        OnPropertyChanged(nameof(SelectedCamera));
        OnPropertyChanged(nameof(HasCamera));
        OnPropertyChanged(nameof(Overlap));
        OnPropertyChanged(nameof(Sidelap));
        OnPropertyChanged(nameof(Portrait));
        OnPropertyChanged(nameof(ShowFootprints));
        ApplyCamera();
    }

    partial void OnSelectedCameraChanged(CameraChoice? value) => CameraChanged();

    partial void OnOverlapChanged(decimal value) => CameraChanged();

    partial void OnSidelapChanged(decimal value) => CameraChanged();

    partial void OnPortraitChanged(bool value) => CameraChanged();

    partial void OnShowFootprintsChanged(bool value)
    {
        Settings.Instance[FootprintsKey] = value.ToString();
        PreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CameraChanged()
    {
        if (!_cameraLoaded)
            return;
        Settings.Instance[CameraKey] = SelectedCamera?.Camera?.Name ?? "";
        Settings.Instance[OverlapKey] = Overlap.ToString(CultureInfo.InvariantCulture);
        Settings.Instance[SidelapKey] = Sidelap.ToString(CultureInfo.InvariantCulture);
        Settings.Instance[PortraitKey] = Portrait.ToString();
        ApplyCamera();
    }

    /// <summary>With a camera: lane spacing and trigger distance from the altitude and overlaps (they regenerate the grid).</summary>
    private void ApplyCamera()
    {
        var cam = SelectedCamera?.Camera;
        var alt = (double)(Altitude ?? 0);
        if (cam == null || alt <= 0)
        {
            FootprintAcross = FootprintAlong = 0;
            CameraText = cam == null ? "" : "Set the altitude";
            return;
        }

        var (across, along) = SurveyCameraMath.Footprint(cam, alt, Portrait);
        FootprintAcross = across;
        FootprintAlong = along;
        LaneSpacing = (decimal)Math.Round(SurveyCameraMath.LaneSpacing(cam, alt, (double)Sidelap, Portrait), 2);
        TriggerDistance = (decimal)Math.Round(SurveyCameraMath.TriggerDistance(cam, alt, (double)Overlap, Portrait), 2);
        CameraText = string.Create(CultureInfo.InvariantCulture,
            $"GSD {SurveyCameraMath.GsdCm(cam, alt):0.00} cm/px · photo {across:0.#} × {along:0.#} m");
    }

    partial void OnAltitudeChanged(decimal? value)
    {
        if (_cameraLoaded && HasCamera)
            ApplyCamera();
    }
}
