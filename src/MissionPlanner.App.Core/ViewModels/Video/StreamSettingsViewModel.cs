using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.Video;

namespace MissionPlanner.App.Core.ViewModels.Video;

/// <summary>The Streaming settings dialog: edits a copy, applied by Save (a running stream restarts).</summary>
public sealed partial class StreamSettingsViewModel : PageViewModel, IDialogPage
{
    private readonly StreamingViewModel _owner;

    public StreamSettingsViewModel(StreamingViewModel owner)
        : base("Streaming settings")
    {
        _owner = owner;
        var s = owner.Settings;
        _mode = Modes.First(m => m.Value == s.Mode);
        _rtspPort = s.RtspPort;
        _rtspPath = s.RtspPath;
        _udpHost = s.UdpHost;
        _udpPort = s.UdpPort;
        _resolution = Resolutions.FirstOrDefault(r => r.Value == s.Height) ?? Resolutions[1];
        _frameRate = FrameRates.FirstOrDefault(r => r.Value == s.Fps) ?? FrameRates[^1];
        _bitrateKbps = s.BitrateKbps;
        _showOsd = s.ShowOsd;

        var installed = HudStreamer.InstalledEncoders();
        Encoders = new[] { new VideoChoice<string>(StreamSettings.AutoEncoder, "Automatic" + (installed.Count > 0 ? $" ({installed[0]})" : "")) }
            .Concat(installed.Select(e => new VideoChoice<string>(e, e)))
            .ToList();
        _encoder = Encoders.FirstOrDefault(e => e.Value == s.Encoder) ?? Encoders[0];
        IsAvailable = HudStreamer.IsAvailable(out var message);
        Availability = message;
    }

    public event EventHandler<bool>? CloseRequested;

    public IReadOnlyList<VideoChoice<StreamMode>> Modes { get; } =
    [
        new(StreamMode.RtspServer, "RTSP server (other apps connect to this computer)"),
        new(StreamMode.UdpPush, "Send RTP/H.264 over UDP to one address"),
        new(StreamMode.Both, "Both"),
    ];

    public IReadOnlyList<VideoChoice<int>> Resolutions { get; } =
        StreamSettings.Heights.Select(h => new VideoChoice<int>(h, $"{(int)Math.Round(h * 16 / 9.0 / 2) * 2} × {h}")).ToList();

    public IReadOnlyList<VideoChoice<int>> FrameRates { get; } =
        StreamSettings.FrameRates.Select(f => new VideoChoice<int>(f, $"{f} fps")).ToList();

    public IReadOnlyList<VideoChoice<string>> Encoders { get; }

    /// <summary>"GStreamer 1.26.11", or why streaming cannot work here.</summary>
    public string Availability { get; }

    public bool IsAvailable { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsRtsp), nameof(ShowsUdp), nameof(Error))]
    private VideoChoice<StreamMode> _mode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    private int _rtspPort;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    private string _rtspPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    private string _udpHost;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    private int _udpPort;

    [ObservableProperty]
    private VideoChoice<int> _resolution;

    [ObservableProperty]
    private VideoChoice<int> _frameRate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    private int _bitrateKbps;

    [ObservableProperty]
    private VideoChoice<string> _encoder;

    [ObservableProperty]
    private bool _showOsd;

    public bool ShowsRtsp => Mode.Value is StreamMode.RtspServer or StreamMode.Both;

    public bool ShowsUdp => Mode.Value is StreamMode.UdpPush or StreamMode.Both;

    /// <summary>Why Save is not possible, or null.</summary>
    public string? Error => ToSettings().Validate();

    public StreamSettings ToSettings() => new()
    {
        Mode = Mode.Value,
        RtspPort = RtspPort,
        RtspPath = (RtspPath ?? "").Trim(),
        UdpHost = (UdpHost ?? "").Trim(),
        UdpPort = UdpPort,
        Height = Resolution.Value,
        Fps = FrameRate.Value,
        BitrateKbps = BitrateKbps,
        Encoder = Encoder.Value,
        ShowOsd = ShowOsd,
    };

    [RelayCommand]
    private void Save()
    {
        if (Error != null)
            return;
        _owner.Apply(ToSettings());
        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}
