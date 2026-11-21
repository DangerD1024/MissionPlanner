using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.Video;

namespace MissionPlanner.App.Core.ViewModels.Video;

public sealed record VideoTypeOption(VideoSourceType Type, string Name, string Help)
{
    public override string ToString() => Name;
}

public sealed record VideoChoice<T>(T Value, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// "Video settings" dialog, after QGroundControl's: saved sources on the left, the selected one's
/// settings on the right with the exact GStreamer pipeline it will run, and the HUD video options.
/// Works on copies; nothing changes until Save.
/// </summary>
public sealed partial class VideoSettingsViewModel : PageViewModel, IDialogPage
{
    public static readonly IReadOnlyList<VideoTypeOption> Types =
    [
        new(VideoSourceType.UdpH264, "UDP h.264 video stream",
            "RTP/H.264 sent to a UDP port on this computer. ArduPilot companion computers and many radios use port 5600."),
        new(VideoSourceType.UdpH265, "UDP h.265 video stream", "RTP/H.265 sent to a UDP port on this computer (usually 5601)."),
        new(VideoSourceType.Rtsp, "RTSP video stream", "A camera or companion computer serving RTSP, e.g. rtsp://192.168.43.1:8554/fpv_stream (Herelink)."),
        new(VideoSourceType.TcpMpegTs, "TCP-MPEG2 video stream", "MPEG transport stream read from a TCP server (host and port)."),
        new(VideoSourceType.UdpMpegTs, "MPEG-TS video stream (UDP)", "MPEG transport stream sent to a UDP port on this computer."),
        new(VideoSourceType.MjpegHttp, "MJPEG over HTTP", "Motion JPEG from an IP camera's web address (multipart/x-mixed-replace)."),
        new(VideoSourceType.TestPattern, "Test pattern", "GStreamer's test pattern: checks that video works on this computer."),
        new(VideoSourceType.CaptureDevice, "Capture device (EasyCap, UVC webcam)",
            "A video capture device plugged into this computer: an EasyCap or other analog video grabber (5.8 GHz FPV receiver output), " +
            "a UVC webcam or an HDMI capture stick. Pick it from the list; empty uses the first one."),
        new(VideoSourceType.Custom, "Custom GStreamer pipeline",
            "Any gst-launch pipeline. It must end with an appsink named outsink that receives BGRA frames:\n" +
            "  ... ! " + VideoPipelineBuilder.SinkTail + "\n" +
            "If the pipeline has no appsink named outsink, that ending is added for you."),
    ];

    public static readonly IReadOnlyList<VideoChoice<RtspTransport>> Transports =
    [
        new(RtspTransport.Auto, "Automatic"),
        new(RtspTransport.Tcp, "TCP (through firewalls, a little more delay)"),
        new(RtspTransport.Udp, "UDP"),
    ];

    public static readonly IReadOnlyList<VideoChoice<VideoDecoder>> Decoders =
    [
        new(VideoDecoder.Auto, "Automatic (hardware when available)"),
        new(VideoDecoder.Software, "Software (libav)"),
    ];

    public static readonly IReadOnlyList<VideoChoice<VideoAspect>> Aspects =
    [
        new(VideoAspect.Fill, "Fill the HUD (crop edges)"),
        new(VideoAspect.Fit, "Fit the whole frame (bars)"),
    ];

    private readonly HudVideoViewModel _owner;

    public VideoSettingsViewModel(HudVideoViewModel owner)
        : base("Video Settings")
    {
        _owner = owner;
        foreach (var s in owner.Sources)
            Sources.Add(new VideoSourceItem(s.Clone()));
        _selected = Sources.FirstOrDefault(s => s.Id == owner.Selected?.Id) ?? Sources.FirstOrDefault();
        _autoStart = owner.AutoStart;
        _aspect = Aspects.First(a => a.Value == owner.Aspect);
        Sources.CollectionChanged += (_, _) => NotifyCommands();
        CheckGStreamer();
    }

    public ObservableCollection<VideoSourceItem> Sources { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand), nameof(DuplicateCommand), nameof(SaveAndPlayCommand))]
    private VideoSourceItem? _selected;

    public bool HasSelection => Selected != null;

    [ObservableProperty]
    private bool _autoStart;

    [ObservableProperty]
    private VideoChoice<VideoAspect> _aspect;

    /// <summary>"GStreamer 1.28.2" or why video cannot work here.</summary>
    [ObservableProperty]
    private string _gStreamerStatus = "Looking for GStreamer…";

    [ObservableProperty]
    private bool _gStreamerMissing;

    public event EventHandler<bool>? CloseRequested;

    [RelayCommand]
    private void Add()
    {
        var item = new VideoSourceItem(new VideoSourceConfig { Name = UniqueName("New source") });
        Sources.Add(item);
        Selected = item;
    }

    private bool HasSelected() => Selected != null;

    [RelayCommand(CanExecute = nameof(HasSelected))]
    private void Duplicate()
    {
        if (Selected == null)
            return;
        var copy = Selected.ToConfig();
        copy.Id = VideoSourceConfig.NewId();
        copy.Name = UniqueName(Selected.Name.Length > 0 ? Selected.Name + " copy" : "Copy");
        var item = new VideoSourceItem(copy);
        Sources.Insert(Sources.IndexOf(Selected) + 1, item);
        Selected = item;
    }

    [RelayCommand(CanExecute = nameof(HasSelected))]
    private void Delete()
    {
        if (Selected == null)
            return;
        var index = Sources.IndexOf(Selected);
        Sources.Remove(Selected);
        Selected = Sources.Count == 0 ? null : Sources[Math.Min(index, Sources.Count - 1)];
    }

    /// <summary>Saves everything; a running stream restarts if its source was edited.</summary>
    [RelayCommand]
    private void Save()
    {
        if (!CanClose())
            return;
        Apply(play: false);
        CloseRequested?.Invoke(this, true);
    }

    private bool CanPlay() => Selected != null;

    /// <summary>Saves and starts the selected source.</summary>
    [RelayCommand(CanExecute = nameof(CanPlay))]
    private void SaveAndPlay()
    {
        if (!CanClose() || Selected?.ValidationError != null)
            return;
        Apply(play: true);
        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    /// <summary>Invalid sources may be saved (to finish later), but not left without being seen.</summary>
    private bool CanClose()
    {
        var bad = Sources.FirstOrDefault(s => s.ValidationError != null && s != Selected);
        if (bad == null)
            return true;
        Selected = bad;
        return false;
    }

    private void Apply(bool play) =>
        _owner.Apply(Sources.Select(s => s.ToConfig()), Selected?.Id, AutoStart, Aspect.Value, play);

    private void NotifyCommands()
    {
        DeleteCommand.NotifyCanExecuteChanged();
        DuplicateCommand.NotifyCanExecuteChanged();
        SaveAndPlayCommand.NotifyCanExecuteChanged();
    }

    private string UniqueName(string name)
    {
        var candidate = name;
        for (var i = 2; Sources.Any(s => s.Name == candidate); i++)
            candidate = $"{name} {i}";
        return candidate;
    }

    private void CheckGStreamer()
    {
        // loading GStreamer the first time scans its plugins: keep it off the UI thread
        var dispatcher = _owner.Services.Dispatcher;
        _ = Task.Run(() =>
        {
            var ok = VideoStream.IsAvailable(out var message);
            var devices = ok ? CaptureDevices.List() : [];
            dispatcher.Post(() =>
            {
                GStreamerMissing = !ok;
                GStreamerStatus = ok ? message + " found" : message;
                ShowDevices(devices);
            });
        });
    }

    /// <summary>Capture devices connected now (EasyCap, UVC), for the Device list.</summary>
    public ObservableCollection<string> CaptureDeviceIds { get; } = new();

    [ObservableProperty]
    private string _captureDevicesText = "";

    [RelayCommand]
    private void RefreshDevices()
    {
        CaptureDevicesText = "Looking for capture devices…";
        var dispatcher = _owner.Services.Dispatcher;
        _ = Task.Run(() =>
        {
            var devices = CaptureDevices.List();
            dispatcher.Post(() => ShowDevices(devices));
        });
    }

    private void ShowDevices(IReadOnlyList<CaptureDevice> devices)
    {
        CaptureDeviceIds.Clear();
        foreach (var d in devices)
            CaptureDeviceIds.Add(d.Id);
        CaptureDevicesText = devices.Count == 0
            ? "No capture devices found. Plug the EasyCap / camera in and press Refresh."
            : string.Join(" · ", devices.Select(d => d.ToString()));
    }
}

/// <summary>One source being edited in the dialog.</summary>
public sealed partial class VideoSourceItem : ObservableObject
{
    public VideoSourceItem(VideoSourceConfig config)
    {
        Id = config.Id;
        _name = config.Name;
        _type = VideoSettingsViewModel.Types.FirstOrDefault(t => t.Type == config.Type) ?? VideoSettingsViewModel.Types[0];
        _url = config.Url;
        _host = config.Host;
        _port = config.Port;
        _pipeline = config.Pipeline;
        _device = config.Device;
        _lowLatency = config.LowLatency;
        _transport = VideoSettingsViewModel.Transports.First(t => t.Value == config.Transport);
        _decoder = VideoSettingsViewModel.Decoders.First(d => d.Value == config.Decoder);
    }

    public string Id { get; }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUrl), nameof(ShowHost), nameof(ShowPort), nameof(ShowPipeline), nameof(ShowLowLatency),
        nameof(ShowTransport), nameof(ShowDecoder), nameof(UrlLabel), nameof(UrlWatermark), nameof(PortLabel), nameof(ShowDevice))]
    private VideoTypeOption _type;

    [ObservableProperty]
    private string _url;

    [ObservableProperty]
    private string _host;

    [ObservableProperty]
    private decimal? _port;

    [ObservableProperty]
    private string _pipeline;

    /// <summary>Capture device name (Windows, macOS) or node (Linux).</summary>
    [ObservableProperty]
    private string _device;

    [ObservableProperty]
    private bool _lowLatency;

    [ObservableProperty]
    private VideoChoice<RtspTransport> _transport;

    [ObservableProperty]
    private VideoChoice<VideoDecoder> _decoder;

    public bool ShowUrl => Type.Type is VideoSourceType.Rtsp or VideoSourceType.MjpegHttp;

    public bool ShowHost => Type.Type == VideoSourceType.TcpMpegTs;

    public bool ShowPort => Type.Type is VideoSourceType.UdpH264 or VideoSourceType.UdpH265 or VideoSourceType.UdpMpegTs or VideoSourceType.TcpMpegTs;

    public bool ShowPipeline => Type.Type == VideoSourceType.Custom;

    public bool ShowDevice => Type.Type == VideoSourceType.CaptureDevice;

    public bool ShowLowLatency => Type.Type is VideoSourceType.UdpH264 or VideoSourceType.UdpH265 or VideoSourceType.Rtsp;

    public bool ShowTransport => Type.Type == VideoSourceType.Rtsp;

    public bool ShowDecoder => Type.Type is VideoSourceType.UdpH264 or VideoSourceType.UdpH265;

    public string UrlLabel => Type.Type == VideoSourceType.Rtsp ? "RTSP URL" : "HTTP URL";

    public string UrlWatermark => Type.Type == VideoSourceType.Rtsp ? "rtsp://192.168.43.1:8554/fpv_stream" : "http://192.168.1.10:8080/video";

    public string PortLabel => Type.Type == VideoSourceType.TcpMpegTs ? "TCP port" : "UDP port (listen)";

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? ToConfig().Summary : Name;

    public string Summary => ToConfig().Summary;

    /// <summary>What is wrong, or null.</summary>
    public string? ValidationError => VideoPipelineBuilder.Validate(ToConfig());

    /// <summary>The exact pipeline this source runs (or the problem).</summary>
    public string PipelinePreview => ValidationError is { } e ? e : VideoPipelineBuilder.Build(ToConfig());

    public VideoSourceConfig ToConfig() => new()
    {
        Id = Id,
        Name = Name.Trim(),
        Type = Type.Type,
        Url = Url.Trim(),
        Host = Host.Trim(),
        Port = Port is { } p && p >= 0 && p <= int.MaxValue ? (int)decimal.Truncate(p) : 0,
        Pipeline = Pipeline,
        Device = (Device ?? "").Trim(),
        LowLatency = LowLatency,
        Transport = Transport.Value,
        Decoder = Decoder.Value,
    };

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is not (nameof(Name) or nameof(Type) or nameof(Url) or nameof(Host) or nameof(Port) or nameof(Pipeline)
            or nameof(Device) or nameof(LowLatency) or nameof(Transport) or nameof(Decoder)))
            return;
        // everything shown about the source depends on all of its fields
        base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Summary)));
        base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(ValidationError)));
        base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(PipelinePreview)));
        base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(DisplayName)));
    }

    partial void OnTypeChanged(VideoTypeOption? oldValue, VideoTypeOption newValue)
    {
        // switching between the two UDP RTP kinds moves the port to that kind's usual default
        if (newValue.Type == VideoSourceType.UdpH265 && Port == VideoSourceConfig.DefaultH264Port)
            Port = VideoSourceConfig.DefaultH265Port;
        else if (newValue.Type == VideoSourceType.UdpH264 && Port == VideoSourceConfig.DefaultH265Port)
            Port = VideoSourceConfig.DefaultH264Port;
        else if (ShowPort && Port is null or <= 0)
            Port = newValue.Type == VideoSourceType.UdpH265 ? VideoSourceConfig.DefaultH265Port : VideoSourceConfig.DefaultH264Port;
    }

    public override string ToString() => DisplayName;
}
