using System.Text.Json.Serialization;

namespace MissionPlanner.App.Core.ViewModels.Video;

/// <summary>Kinds of video source, after QGroundControl's Video Settings.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<VideoSourceType>))]
public enum VideoSourceType
{
    /// <summary>RTP/H.264 sent to a UDP port on this computer (ArduPilot companions, Herelink AirUnit forwarders...).</summary>
    UdpH264,

    /// <summary>RTP/H.265 sent to a UDP port.</summary>
    UdpH265,

    /// <summary>An RTSP server (camera, Herelink, SIYI, companion computer).</summary>
    Rtsp,

    /// <summary>MPEG-TS read from a TCP server.</summary>
    TcpMpegTs,

    /// <summary>MPEG-TS sent to a UDP port.</summary>
    UdpMpegTs,

    /// <summary>Motion JPEG over HTTP (multipart/x-mixed-replace), as IP cameras serve it.</summary>
    MjpegHttp,

    /// <summary>GStreamer's test pattern, to check that video works at all.</summary>
    TestPattern,

    /// <summary>Any GStreamer pipeline text.</summary>
    Custom,
}

[JsonConverter(typeof(JsonStringEnumConverter<RtspTransport>))]
public enum RtspTransport
{
    Auto,
    Tcp,
    Udp,
}

[JsonConverter(typeof(JsonStringEnumConverter<VideoDecoder>))]
public enum VideoDecoder
{
    /// <summary>GStreamer picks the decoder; a hardware one when the system has it.</summary>
    Auto,

    /// <summary>Always libav (FFmpeg) in software.</summary>
    Software,
}

/// <summary>How the frame fills the HUD.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<VideoAspect>))]
public enum VideoAspect
{
    /// <summary>Cover the whole HUD, cropping the edges that do not fit.</summary>
    Fill,

    /// <summary>Show the whole frame, with bars where the shapes differ.</summary>
    Fit,
}

/// <summary>One saved video source. Plain data: stored as JSON in the settings file.</summary>
public sealed class VideoSourceConfig
{
    public const int DefaultH264Port = 5600;
    public const int DefaultH265Port = 5601;

    /// <summary>Stable identity, so a source stays selected when it is renamed.</summary>
    public string Id { get; set; } = NewId();

    public string Name { get; set; } = "";

    public VideoSourceType Type { get; set; } = VideoSourceType.UdpH264;

    /// <summary>RTSP or HTTP address.</summary>
    public string Url { get; set; } = "";

    /// <summary>TCP server address for MPEG-TS over TCP.</summary>
    public string Host { get; set; } = "";

    /// <summary>UDP port to listen on, or the TCP server's port.</summary>
    public int Port { get; set; } = DefaultH264Port;

    /// <summary>Pipeline text for <see cref="VideoSourceType.Custom"/>.</summary>
    public string Pipeline { get; set; } = "";

    /// <summary>No jitter buffering: lowest delay, may show glitches on a poor link.</summary>
    public bool LowLatency { get; set; } = true;

    public RtspTransport Transport { get; set; } = RtspTransport.Auto;

    public VideoDecoder Decoder { get; set; } = VideoDecoder.Auto;

    public VideoSourceConfig Clone() => (VideoSourceConfig)MemberwiseClone();

    /// <summary>True when both describe the same stream (identity and name aside).</summary>
    public bool SameStream(VideoSourceConfig other) =>
        Type == other.Type && Url == other.Url && Host == other.Host && Port == other.Port && Pipeline == other.Pipeline &&
        LowLatency == other.LowLatency && Transport == other.Transport && Decoder == other.Decoder;

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>Short description for menus and lists, e.g. "UDP H.264 :5600".</summary>
    [JsonIgnore]
    public string Summary => Type switch
    {
        VideoSourceType.UdpH264 => $"UDP H.264 :{Port}",
        VideoSourceType.UdpH265 => $"UDP H.265 :{Port}",
        VideoSourceType.UdpMpegTs => $"UDP MPEG-TS :{Port}",
        VideoSourceType.TcpMpegTs => $"TCP MPEG-TS {Host}:{Port}",
        VideoSourceType.Rtsp => Url,
        VideoSourceType.MjpegHttp => Url,
        VideoSourceType.TestPattern => "Test pattern",
        _ => "GStreamer pipeline",
    };

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Summary : Name;

    public override string ToString() => DisplayName;
}
