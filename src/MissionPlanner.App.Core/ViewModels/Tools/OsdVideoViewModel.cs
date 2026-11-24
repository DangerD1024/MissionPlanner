using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using MissionPlanner.App.Core.ViewModels.FlightData;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.Tools;

/// <summary>
/// Mission Planner's OSD video ("overlay the hud into your recorded videos"): a recording and the
/// telemetry log of the flight, a time offset to line them up, and a new video with the HUD drawn
/// over every frame (H.264 MP4, written next to the recording as name-overlay.mp4).
/// </summary>
public sealed partial class OsdVideoViewModel : ToolPageViewModel
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(OsdVideoViewModel));
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private const string KeyVideo = "next_osdvideo_video";
    private const string KeyTlog = "next_osdvideo_tlog";
    private const string KeyOffset = "next_osdvideo_offset";
    private const string KeyFullRes = "next_osdvideo_fullres";
    private const string KeyBitrate = "next_osdvideo_bitrate";
    private const string KeyEncoder = "next_osdvideo_encoder";

    public const string AutoEncoder = "auto";

    private static readonly FileFilter Videos = new("Videos", "mp4", "MP4", "mkv", "avi", "AVI", "mov", "MOV", "ts", "mpg", "mpeg", "webm");
    private static readonly FileFilter Tlogs = new("Telemetry logs", "tlog");

    private TlogHudTimeline? _timeline;
    private CancellationTokenSource? _render;
    private CancellationTokenSource? _preview;
    private bool _loading;

    public OsdVideoViewModel(AppServices services)
        : base("OSD video",
            "Draws the HUD from a telemetry log over a recorded video of the flight, and writes it as a new video.",
            services)
    {
        var s = Settings.Instance;
        _loading = true;
        _videoPath = s.GetString(KeyVideo);
        _tlogPath = s.GetString(KeyTlog);
        _offsetSeconds = (decimal)Math.Clamp(s.GetDouble(KeyOffset, 0), -OsdTimeAlignment.MaxOffset, OsdTimeAlignment.MaxOffset);
        _fullResolution = s.GetBoolean(KeyFullRes, true);
        _bitrateKbps = Math.Clamp(s.GetInt32(KeyBitrate, 8000), 500, 100000);
        _encoder = s.GetString(KeyEncoder, AutoEncoder);
        _loading = false;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsBusy))
                OnPropertyChanged(nameof(CanRender));
        };
    }

    public override bool NeedsVehicle => false;

    /// <summary>Draws the HUD; the view sets it (rendering the HUD needs the UI framework).</summary>
    public IHudCompositor? Compositor { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRender))]
    private string _videoPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRender))]
    private string _tlogPath;

    /// <summary>Seconds added to the video time to find the log time (−900…900).</summary>
    [ObservableProperty]
    private decimal _offsetSeconds;

    /// <summary>The source size; off scales wide videos down to 1280 pixels.</summary>
    [ObservableProperty]
    private bool _fullResolution;

    [ObservableProperty]
    private int _bitrateKbps;

    [ObservableProperty]
    private string _encoder;

    [ObservableProperty]
    private IReadOnlyList<string> _encoders = [AutoEncoder];

    [ObservableProperty]
    private string _videoInfoText = "";

    [ObservableProperty]
    private string _logInfoText = "";

    /// <summary>Seconds into the video of the preview frame.</summary>
    [ObservableProperty]
    private double _previewSeconds;

    [ObservableProperty]
    private double _videoDuration;

    [ObservableProperty]
    private OsdFrame? _previewFrame;

    [ObservableProperty]
    private string _previewText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRender))]
    [NotifyPropertyChangedFor(nameof(ProgressIndeterminate))]
    private bool _isRendering;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressIndeterminate))]
    private double _progress;

    [ObservableProperty]
    private string _progressText = "";

    private OsdVideoInfo? _info;

    public bool ProgressIndeterminate => IsRendering && Progress < 0;

    public bool CanRender => !IsBusy && !IsRendering && File.Exists(VideoPath) && File.Exists(TlogPath);

    public double MaxOffset => OsdTimeAlignment.MaxOffset;

    partial void OnVideoPathChanged(string value)
    {
        if (_loading)
            return;
        Settings.Instance[KeyVideo] = value;
        _info = null;
        VideoInfoText = "";
    }

    partial void OnTlogPathChanged(string value)
    {
        if (_loading)
            return;
        Settings.Instance[KeyTlog] = value;
        _timeline = null;
        LogInfoText = "";
    }

    partial void OnOffsetSecondsChanged(decimal value)
    {
        if (_loading)
            return;
        Settings.Instance[KeyOffset] = value.ToString(Inv);
        UpdatePreviewText();
    }

    partial void OnFullResolutionChanged(bool value) => Settings.Instance[KeyFullRes] = value.ToString();

    partial void OnBitrateKbpsChanged(int value) => Settings.Instance[KeyBitrate] = value.ToString(Inv);

    partial void OnEncoderChanged(string value) => Settings.Instance[KeyEncoder] = value;

    public override void OnShown()
    {
        base.OnShown();
        if (Encoders.Count <= 1)
            _ = LoadEncodersAsync();
    }

    private async Task LoadEncodersAsync()
    {
        var available = await Task.Run(() => (OsdVideoPipeline.IsAvailable(out var m), m, OsdVideoPipeline.InstalledEncoders()));
        Encoders = [AutoEncoder, .. available.Item3];
        if (!Encoders.Contains(Encoder))
            Encoder = AutoEncoder;
        if (!available.Item1)
            Report(available.m, true);
    }

    [RelayCommand]
    private async Task PickVideoAsync()
    {
        var file = await Services.Dialogs.OpenFileAsync("Recorded video", Videos);
        if (file == null)
            return;
        VideoPath = file;
        // Mission Planner kept the log and offset of a video in osdvideo.txt beside it
        if (OsdVideoConfig.Load(file) is { } saved)
        {
            if (File.Exists(saved.Tlog))
                TlogPath = saved.Tlog;
            OffsetSeconds = (decimal)Math.Clamp(saved.Offset, -OsdTimeAlignment.MaxOffset, OsdTimeAlignment.MaxOffset);
        }

        await LoadVideoInfoAsync();
    }

    [RelayCommand]
    private async Task PickTlogAsync()
    {
        var file = await Services.Dialogs.OpenFileAsync("Telemetry log of the flight", Tlogs);
        if (file == null)
            return;
        TlogPath = file;
        await EnsureTimelineAsync();
    }

    private async Task<bool> LoadVideoInfoAsync()
    {
        if (_info != null)
            return true;
        if (!File.Exists(VideoPath))
            return false;
        try
        {
            var path = VideoPath;
            _info = await Task.Run(() => OsdVideoPipeline.Probe(path));
            VideoInfoText = _info.ToString();
            VideoDuration = Math.Max(0, _info.DurationSeconds);
            PreviewSeconds = Math.Min(PreviewSeconds, VideoDuration);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("osd video probe", ex);
            VideoInfoText = "";
            Report("The video could not be read: " + ex.Message, true);
            return false;
        }
    }

    private async Task<bool> EnsureTimelineAsync()
    {
        if (_timeline != null)
            return true;
        if (!File.Exists(TlogPath))
            return false;
        IsBusy = true;
        Status = "Reading the telemetry log…";
        StatusIsError = false;
        try
        {
            var path = TlogPath;
            var progress = new Progress<double>(f => ProgressText = $"Reading the log {f:P0}");
            var timeline = await Task.Run(() => TlogHudTimeline.Load(path, progress));
            if (timeline.Count == 0)
            {
                Report("The telemetry log has no packets with times.", true);
                return false;
            }

            _timeline = timeline;
            LogInfoText = string.Format(Inv, "Log {0:yyyy-MM-dd HH:mm:ss} to {1:HH:mm:ss} ({2:h\\:mm\\:ss})",
                timeline.Start, timeline.End, timeline.Duration);
            ProgressText = "";
            Report("Telemetry log read.");
            UpdatePreviewText();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("osd video tlog", ex);
            Report("The telemetry log could not be read: " + ex.Message, true);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The HUD for a time in the video with the current offset; an empty HUD before the log starts.</summary>
    public HudModel HudAt(double videoSeconds)
    {
        if (_timeline == null)
            return new HudModel();
        var t = OsdTimeAlignment.LogTimeFor(_timeline.Start, videoSeconds, (double)OffsetSeconds);
        return _timeline.At(t) ?? new HudModel();
    }

    private void UpdatePreviewText()
    {
        if (_timeline == null)
        {
            PreviewText = "";
            return;
        }

        var t = OsdTimeAlignment.LogTimeFor(_timeline.Start, PreviewSeconds, (double)OffsetSeconds);
        var hud = _timeline.At(t);
        PreviewText = hud == null
            ? string.Format(Inv, "Video {0:0.0} s → log {1:HH:mm:ss.f}: before the log starts", PreviewSeconds, t)
            : string.Format(Inv, "Video {0:0.0} s → log {1:HH:mm:ss.f}: {2}{3}, alt {4:0.0} {5}, speed {6:0.0} {7}",
                PreviewSeconds, t, hud.Mode, hud.Armed ? " armed" : "", hud.Altitude, hud.AltUnit, hud.Groundspeed, hud.SpeedUnit);
    }

    partial void OnPreviewSecondsChanged(double value) => UpdatePreviewText();

    private (int Width, int Height) OutputSize() =>
        _info == null ? (0, 0) : OsdVideoPipeline.OutputSize(_info.Width, _info.Height, FullResolution);

    [RelayCommand]
    private async Task PreviewAsync()
    {
        if (Compositor is not { } compositor)
            return;
        if (!await LoadVideoInfoAsync() || !await EnsureTimelineAsync())
        {
            if (!File.Exists(VideoPath) || !File.Exists(TlogPath))
                Report("Pick the video and the telemetry log first.", true);
            return;
        }

        _preview?.Cancel();
        _preview = new CancellationTokenSource();
        var token = _preview.Token;
        try
        {
            var (w, h) = OutputSize();
            var (frame, seconds) = await OsdVideoPipeline.PreviewAsync(VideoPath, PreviewSeconds, _info!.StartSeconds, w, h, HudAt, compositor, token);
            if (token.IsCancellationRequested)
                return;
            PreviewFrame = frame;
            UpdatePreviewText();
            Status = string.Format(Inv, "Preview at {0:0.0} s.", seconds);
            StatusIsError = false;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn("osd video preview", ex);
            Report("Preview failed: " + ex.Message, true);
        }
    }

    /// <summary>Shifts the offset so the log moves by <paramref name="seconds"/> against the video.</summary>
    [RelayCommand]
    private async Task NudgeAsync(string seconds)
    {
        if (!double.TryParse(seconds, NumberStyles.Float, Inv, out var d))
            return;
        OffsetSeconds = (decimal)Math.Clamp((double)OffsetSeconds + d, -OsdTimeAlignment.MaxOffset, OsdTimeAlignment.MaxOffset);
        if (PreviewFrame != null)
            await PreviewAsync();
    }

    /// <summary>The output: name-overlay.mp4 next to the recording (Mission Planner wrote name-overlay.avi).</summary>
    public static string OutputPathFor(string video) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(video)) ?? ".", Path.GetFileNameWithoutExtension(video) + "-overlay.mp4");

    [RelayCommand]
    private async Task RenderAsync()
    {
        if (IsRendering || Compositor is not { } compositor)
            return;
        if (!await LoadVideoInfoAsync() || !await EnsureTimelineAsync())
        {
            if (!File.Exists(VideoPath) || !File.Exists(TlogPath))
                Report("Pick the video and the telemetry log first.", true);
            return;
        }

        var output = await Services.Dialogs.SaveFileAsync("Video with the HUD", Path.GetFileName(OutputPathFor(VideoPath)),
            new FileFilter("MP4 video", "mp4"));
        if (output == null)
            return;
        if (string.Equals(Path.GetFullPath(output), Path.GetFullPath(VideoPath), StringComparison.OrdinalIgnoreCase))
        {
            Report("Choose another file name: the recording would be overwritten.", true);
            return;
        }

        var encoder = Encoder == AutoEncoder ? Encoders.FirstOrDefault(e => e != AutoEncoder) : Encoder;
        if (encoder == null)
        {
            Report("No H.264 encoder is installed (x264enc from gst-plugins-ugly, or openh264enc).", true);
            return;
        }

        OsdVideoConfig.Save(VideoPath, TlogPath, (double)OffsetSeconds);
        var (w, h) = OutputSize();
        var options = new OsdRenderOptions(w, h, encoder, BitrateKbps);
        _render = new CancellationTokenSource();
        var token = _render.Token;
        IsRendering = true;
        Progress = 0;
        Status = "Writing " + Path.GetFileName(output) + "…";
        StatusIsError = false;
        var progress = new Progress<OsdRenderProgress>(p =>
        {
            Progress = p.Fraction;
            ProgressText = p.DurationSeconds > 0
                ? string.Format(Inv, "{0} frames, {1:0.0} of {2:0.0} s", p.Frames, p.VideoSeconds, p.DurationSeconds)
                : string.Format(Inv, "{0} frames, {1:0.0} s", p.Frames, p.VideoSeconds);
        });
        try
        {
            var frames = await OsdVideoPipeline.RenderAsync(VideoPath, output, options, HudAt, compositor, progress, token);
            Report(token.IsCancellationRequested
                ? $"Stopped: {frames} frames written to {output}."
                : $"Written {output} ({frames} frames, {w}×{h}, {encoder}).");
        }
        catch (OperationCanceledException)
        {
            Report("Cancelled.");
        }
        catch (Exception ex)
        {
            Log.Warn("osd video render", ex);
            Report("Writing the video failed: " + ex.Message, true);
        }
        finally
        {
            _render.Dispose();
            _render = null;
            IsRendering = false;
            ProgressText = "";
        }
    }

    [RelayCommand]
    private void Cancel() => _render?.Cancel();

    public override void OnShutdown()
    {
        base.OnShutdown();
        _render?.Cancel();
        _preview?.Cancel();
    }
}
