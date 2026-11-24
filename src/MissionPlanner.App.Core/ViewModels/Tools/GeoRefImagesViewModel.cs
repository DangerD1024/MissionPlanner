using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.ViewModels.Logs;
using MissionPlanner.GeoRef;

namespace MissionPlanner.App.Core.ViewModels.Tools;

/// <summary>A photo matched to a position by the geo-referencing.</summary>
public sealed record GeoRefPhoto(string Path, double Lat, double Lng, double AltAmsl, double RelAlt)
{
    public string Name => System.IO.Path.GetFileName(Path);

    public string Text => string.Format(CultureInfo.InvariantCulture, "{0}  {1:0.0000000}, {2:0.0000000}  {3:0.0} m AMSL, {4:0.0} m rel",
        Name, Lat, Lng, AltAmsl, RelAlt);
}

/// <summary>How photos are matched to the log.</summary>
public enum GeoRefMode
{
    /// <summary>CAM messages (the autopilot's camera feedback), one per photo in order.</summary>
    CamMessage,

    /// <summary>TRIG messages (the autopilot triggered the camera), one per photo in order.</summary>
    Trigger,

    /// <summary>The photos' EXIF time plus a fixed offset to the log's GPS time.</summary>
    TimeOffset,
}

/// <summary>
/// TOOLS > Geo-reference images (Ctrl+F "Geo ref images", Mission Planner's GeoRef window): matches
/// survey photos to the positions in a flight log, writes the KML/text reports and copies the photos
/// with GPS EXIF tags into a "geotagged" folder.
/// </summary>
public sealed partial class GeoRefImagesViewModel(AppServices services)
    : ToolPageViewModel("Geo-reference images",
        "Matches the photos of a survey to the positions in its flight log (CAM or TRIG messages, or a time offset), writes location reports, and saves copies of the photos with their GPS position in a \"geotagged\" folder.",
        services)
{
    private readonly GeoRefImageBase _georef = new() { useAMSLAlt = true };

    public override bool NeedsVehicle => false;

    public sealed record ModeOption(GeoRefMode Mode, string Text);

    /// <summary>The processing modes, labelled as in Mission Planner.</summary>
    public IReadOnlyList<ModeOption> Modes { get; } =
    [
        new(GeoRefMode.CamMessage, "CAM Message"),
        new(GeoRefMode.Trigger, "Trigger Message"),
        new(GeoRefMode.TimeOffset, "Time offset"),
    ];

    public ModeOption SelectedMode
    {
        get => Modes.First(m => m.Mode == Mode);
        set
        {
            if (value != null)
                Mode = value.Mode;
        }
    }

    [ObservableProperty]
    private string _logFile = "";

    [ObservableProperty]
    private string _imageFolder = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTimeOffset), nameof(IsCam), nameof(IsTrigger), nameof(UsesShutterLag), nameof(SelectedMode))]
    private GeoRefMode _mode = GeoRefMode.CamMessage;

    public bool IsTimeOffset => Mode == GeoRefMode.TimeOffset;
    public bool IsCam => Mode == GeoRefMode.CamMessage;
    public bool IsTrigger => Mode == GeoRefMode.Trigger;
    public bool UsesShutterLag => Mode == GeoRefMode.CamMessage;

    /// <summary>Seconds from the photos' EXIF time to GPS time (time offset mode).</summary>
    [ObservableProperty]
    private string _offsetSeconds = "0";

    /// <summary>Use CAM messages for the positions in time offset mode.</summary>
    [ObservableProperty]
    private bool _useCamMessages;

    [ObservableProperty]
    private decimal _shutterLagMs;

    [ObservableProperty]
    private bool _useGps2;

    [ObservableProperty]
    private bool _useAmslAlt = true;

    /// <summary>Added to the relative altitude ("Rel Alt base").</summary>
    [ObservableProperty]
    private string _baseAlt = "0";

    [ObservableProperty]
    private bool _camUseGpsAlt;

    [ObservableProperty]
    private bool _trigUseGpsAlt;

    [ObservableProperty]
    private decimal _dropStart;

    [ObservableProperty]
    private decimal _dropEnd;

    [ObservableProperty]
    private decimal _minShutter = 0.5m;

    [ObservableProperty]
    private decimal _cameraRotation = 90;

    [ObservableProperty]
    private decimal _crossFov = 200;

    [ObservableProperty]
    private decimal _dirFov = 130;

    [ObservableProperty]
    private string _output = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGeoTag))]
    private bool _processed;

    public bool CanGeoTag => Processed && !IsBusy;

    /// <summary>The vehicle's path from the log, for the map.</summary>
    [ObservableProperty]
    private IReadOnlyList<LogTrackPoint> _track = [];

    /// <summary>Where the camera fired (CAM/TRIG messages), for the map.</summary>
    [ObservableProperty]
    private IReadOnlyList<LogTrackPoint> _shots = [];

    public ObservableCollection<GeoRefPhoto> Photos { get; } = new();

    [ObservableProperty]
    private GeoRefPhoto? _selectedPhoto;

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(IsBusy))
            OnPropertyChanged(nameof(CanGeoTag));
    }

    partial void OnLogFileChanged(string value)
    {
        _georef.vehicleLocations?.Clear();
        _georef.picturesInfo?.Clear();
        Processed = false;
    }

    partial void OnUseCamMessagesChanged(bool value) => _georef.vehicleLocations?.Clear();

    partial void OnUseGps2Changed(bool value) => _georef.vehicleLocations?.Clear();

    partial void OnModeChanged(GeoRefMode value) => _georef.vehicleLocations?.Clear();

    private void AppendText(string text) =>
        Services.Dispatcher.Post(() => Output += text.Replace("\n", Environment.NewLine));

    [RelayCommand]
    private async Task BrowseLogAsync()
    {
        var file = await Services.Dialogs.OpenFileAsync("Flight log", new FileFilter("Logs", "log", "tlog", "bin", "BIN"));
        if (file == null)
            return;
        LogFile = file;
        ImageFolder = Path.GetDirectoryName(file) ?? "";
    }

    [RelayCommand]
    private async Task BrowseImagesAsync()
    {
        var folder = await Services.Dialogs.PickFolderAsync("Folder of photos");
        if (folder == null)
            return;
        SetImageFolder(folder);
    }

    /// <summary>Picks the photo folder; a location.txt there may give the time offset.</summary>
    internal void SetImageFolder(string folder)
    {
        ImageFolder = folder;
        var file = Path.Combine(folder, "location.txt");
        if (!File.Exists(file))
            return;
        try
        {
            var match = Regex.Match(File.ReadAllText(file), "seconds_offset: ([0-9]+)");
            if (match.Success)
                OffsetSeconds = match.Groups[1].Value;
        }
        catch (IOException)
        {
        }
    }

    private string Gps => UseGps2 ? "GPS2" : "GPS";

    private bool CheckInputs()
    {
        if (!File.Exists(LogFile))
        {
            Report("Choose the flight log first.", true);
            return false;
        }

        if (!Directory.Exists(ImageFolder))
        {
            Report("Choose the folder of photos first.", true);
            return false;
        }

        return true;
    }

    [RelayCommand]
    private async Task EstimateOffsetAsync()
    {
        if (!CheckInputs() || IsBusy)
            return;
        IsBusy = true;
        Output = "";
        try
        {
            var (log, dir, gps, cam) = (LogFile, ImageFolder, Gps, UseCamMessages);
            var offset = await Task.Run(() => _georef.EstimateOffset(log, dir, gps, cam, AppendText));
            AppendText("Offset around :  " + offset.ToString(CultureInfo.InvariantCulture) + "\n\n");
            Report("Offset around " + offset.ToString("0.##", CultureInfo.InvariantCulture) + " s.");
        }
        catch (Exception ex)
        {
            Report("Could not estimate the offset: " + ex.Message, true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Matches the photos to the log and writes the report files ("Pre-process").</summary>
    [RelayCommand]
    internal async Task ProcessAsync()
    {
        if (!CheckInputs() || IsBusy)
            return;

        float seconds = 0;
        if (Mode == GeoRefMode.TimeOffset &&
            !float.TryParse(OffsetSeconds, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
        {
            Output = "";
            AppendText("Offset number not in correct format. Use . as decimal separator\n");
            Report("The offset is not a number (use . as the decimal separator).", true);
            return;
        }

        IsBusy = true;
        Processed = false;
        Output = "";
        Photos.Clear();
        Track = [];
        Shots = [];

        _georef.useAMSLAlt = UseAmslAlt;
        _georef.millisShutterLag = (int)ShutterLagMs;
        _georef.minshutter = (double)MinShutter;

        var (log, dir, gps, mode, cam) = (LogFile, ImageFolder, Gps, Mode, UseCamMessages);
        var (rotation, hfov, vfov) = ((double)CameraRotation, (double)CrossFov, (double)DirFov);
        var (dropStart, dropEnd) = ((int)DropStart, (int)DropEnd);
        var (camGpsAlt, trigGpsAlt) = (CamUseGpsAlt, TrigUseGpsAlt);
        try
        {
            await Task.Run(() =>
            {
                try
                {
                    switch (mode)
                    {
                        case GeoRefMode.TimeOffset:
                            _georef.picturesInfo = _georef.doworkGPSOFFSET(log, dir, seconds, gps, cam, AppendText);
                            if (_georef.picturesInfo != null)
                                _georef.CreateReportFiles(_georef.picturesInfo, dir, seconds, rotation, hfov, vfov, AppendText);
                            break;
                        case GeoRefMode.CamMessage:
                            _georef.picturesInfo = _georef.doworkCAM(log, dir, gps, AppendText, dropStart, dropEnd);
                            if (_georef.picturesInfo != null)
                                _georef.CreateReportFiles(_georef.picturesInfo, dir, seconds, rotation, hfov, vfov, AppendText, null, camGpsAlt);
                            break;
                        case GeoRefMode.Trigger:
                            _georef.picturesInfo = _georef.doworkTRIG(log, dir, gps, AppendText, dropStart, dropEnd);
                            if (_georef.picturesInfo != null)
                                _georef.CreateReportFiles(_georef.picturesInfo, dir, seconds, rotation, hfov, vfov, AppendText, null, trigGpsAlt);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    AppendText("Error " + ex + "\n");
                }
            });

            Track = _georef.vehicleLocations?.Values.Select(v => new LogTrackPoint(0, v.Lat, v.Lon, v.AltAMSL)).ToList() ?? [];
            Shots = _georef.camLocations?.Values.Select(v => new LogTrackPoint(0, v.Lat, v.Lon, v.AltAMSL)).ToList() ?? [];
            foreach (var p in _georef.picturesInfo?.Values ?? Enumerable.Empty<PictureInformation>())
                Photos.Add(new GeoRefPhoto(p.Path, p.Lat, p.Lon, p.AltAMSL, p.RelAlt));

            Processed = true;
            Report(Photos.Count == 0
                ? "No photos matched; see the log below."
                : $"{Photos.Count} photos located. Reports written to {dir}; Geotag images to save tagged copies.",
                Photos.Count == 0);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Saves the photos with GPS EXIF tags into &lt;folder&gt;/geotagged.</summary>
    [RelayCommand]
    internal async Task GeoTagAsync()
    {
        if (IsBusy || !Directory.Exists(ImageFolder))
            return;
        if (_georef.picturesInfo == null || _georef.picturesInfo.Count == 0)
        {
            AppendText("no valid matchs");
            Report("Nothing to geotag: process the photos first.", true);
            return;
        }

        double baseAlt = 0;
        // Mission Planner adds the base to the AMSL altitude
        if (!double.TryParse(BaseAlt, NumberStyles.Float, CultureInfo.InvariantCulture, out baseAlt))
        {
            Report("The base altitude is not a number.", true);
            return;
        }

        IsBusy = true;
        var (root, mode, amsl) = (ImageFolder, Mode, UseAmslAlt);
        var (camGpsAlt, trigGpsAlt) = (CamUseGpsAlt, TrigUseGpsAlt);
        try
        {
            var count = await Task.Run(() =>
            {
                var geoTagFolder = Path.Combine(root, "geotagged");
                // start from an empty folder
                if (Directory.Exists(geoTagFolder))
                    Directory.Delete(geoTagFolder, true);
                Directory.CreateDirectory(geoTagFolder);

                var n = 0;
                foreach (var pic in _georef.picturesInfo.Values)
                {
                    double alt;
                    if (camGpsAlt && mode == GeoRefMode.CamMessage)
                        alt = pic.GPSAlt;
                    else if (trigGpsAlt && mode == GeoRefMode.Trigger)
                        alt = pic.GPSAlt;
                    else if (amsl)
                        alt = baseAlt + pic.AltAMSL;
                    else
                        alt = pic.RelAlt;
                    _georef.WriteCoordinatesToImage(pic.Path, pic.Lat, pic.Lon, alt, root, AppendText);
                    n++;
                }

                return n;
            });
            AppendText("GeoTagging FINISHED \n\n");
            Report($"Saved {count} geotagged photos in {Path.Combine(root, "geotagged")}.");
        }
        catch (Exception ex)
        {
            Report("Could not geotag the photos: " + ex.Message, true);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
