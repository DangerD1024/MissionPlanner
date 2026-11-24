using System.Globalization;
using System.Text.RegularExpressions;

namespace MissionPlanner.App.Core.ViewModels.Video;

/// <summary>How the HUD picture leaves the app.</summary>
public enum StreamMode
{
    /// <summary>An RTSP server other apps connect to: rtsp://this-pc:8554/hud.</summary>
    RtspServer,

    /// <summary>RTP/H.264 sent to one address (QGroundControl, Mission Planner, a UDP H.264 source).</summary>
    UdpPush,

    Both,

    /// <summary>Raw BGRA frames in /dev/shm for a tracker on the same machine: no encoder,
    /// no decoder, no network — one frame of latency. Not usable across machines.</summary>
    Shm,

    /// <summary>Only the frame source runs (recording the HUD to a file without streaming); not offered in the settings.</summary>
    RecordOnly,
}

/// <summary>Streaming options, kept under <c>next_stream_*</c> keys.</summary>
public sealed partial class StreamSettings
{
    public const string ModeKey = "next_stream_mode";
    public const string RtspPortKey = "next_stream_rtsp_port";
    public const string RtspPathKey = "next_stream_rtsp_path";
    public const string UdpHostKey = "next_stream_udp_host";
    public const string UdpPortKey = "next_stream_udp_port";
    public const string HeightKey = "next_stream_height";
    public const string FpsKey = "next_stream_fps";
    public const string BitrateKey = "next_stream_kbps";
    public const string EncoderKey = "next_stream_encoder";
    public const string ShowOsdKey = "next_stream_osd";

    /// <summary>"Auto" picks the first installed encoder from <see cref="StreamPipelines.EncoderOrder"/>.</summary>
    public const string AutoEncoder = "auto";

    public static readonly int[] Heights = [480, 720, 1080];
    public static readonly int[] FrameRates = [15, 25, 30];

    public StreamMode Mode { get; set; } = StreamMode.RtspServer;
    public int RtspPort { get; set; } = 8554;
    public string RtspPath { get; set; } = "hud";
    public string UdpHost { get; set; } = "127.0.0.1";
    public int UdpPort { get; set; } = 5600;

    /// <summary>Picture height; the width follows at 16:9 (rounded to even).</summary>
    public int Height { get; set; } = 720;

    public int Fps { get; set; } = 30;
    public int BitrateKbps { get; set; } = 2500;
    public string Encoder { get; set; } = AutoEncoder;

    /// <summary>Draw the HUD (OSD) over the picture in the stream.</summary>
    public bool ShowOsd { get; set; } = true;

    public int Width => (int)Math.Round(Height * 16 / 9.0 / 2) * 2;

    public bool UsesRtsp => Mode is StreamMode.RtspServer or StreamMode.Both;
    public bool UsesUdp => Mode is StreamMode.UdpPush or StreamMode.Both;
    public bool UsesShm => Mode is StreamMode.Shm;

    /// <summary>Where <see cref="StreamMode.Shm"/> publishes raw frames (seqlock header + 3 slots).</summary>
    public const string ShmPath = "/dev/shm/mp-hud";

    public StreamSettings Clone() => (StreamSettings)MemberwiseClone();

    [GeneratedRegex(@"^[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*$")]
    private static partial Regex PathRegex();

    /// <summary>Why these settings cannot stream, or null.</summary>
    public string? Validate()
    {
        if (UsesRtsp)
        {
            if (RtspPort is < 1 or > 65535)
                return "RTSP port must be 1 to 65535.";
            if (!PathRegex().IsMatch(RtspPath.Trim('/')))
                return "RTSP path may use letters, digits, '.', '_', '-' and '/'.";
        }

        if (UsesUdp)
        {
            if (string.IsNullOrWhiteSpace(UdpHost) || UdpHost.Any(char.IsWhiteSpace) || UdpHost.Contains('"'))
                return "Enter the address to send the UDP stream to.";
            if (UdpPort is < 1 or > 65535)
                return "UDP port must be 1 to 65535.";
        }

        if (!Heights.Contains(Height))
            return "Choose a resolution.";
        if (Fps is < 1 or > 60)
            return "Frame rate must be 1 to 60.";
        if (BitrateKbps is < 100 or > 50000)
            return "Bitrate must be 100 to 50000 kbit/s.";
        return null;
    }

    public static StreamSettings Load(IVideoSettingsStore store)
    {
        var s = new StreamSettings();
        if (Enum.TryParse<StreamMode>(store.Get(ModeKey), out var mode))
            s.Mode = mode;
        s.RtspPort = Int(store.Get(RtspPortKey), s.RtspPort);
        if (store.Get(RtspPathKey) is { Length: > 0 } path)
            s.RtspPath = path;
        if (store.Get(UdpHostKey) is { Length: > 0 } host)
            s.UdpHost = host;
        s.UdpPort = Int(store.Get(UdpPortKey), s.UdpPort);
        var height = Int(store.Get(HeightKey), s.Height);
        s.Height = Heights.Contains(height) ? height : s.Height;
        s.Fps = Math.Clamp(Int(store.Get(FpsKey), s.Fps), 1, 60);
        s.BitrateKbps = Math.Clamp(Int(store.Get(BitrateKey), s.BitrateKbps), 100, 50000);
        if (store.Get(EncoderKey) is { Length: > 0 } encoder)
            s.Encoder = encoder;
        if (bool.TryParse(store.Get(ShowOsdKey), out var osd))
            s.ShowOsd = osd;
        return s;
    }

    public void Save(IVideoSettingsStore store)
    {
        var inv = CultureInfo.InvariantCulture;
        store.Set(ModeKey, Mode.ToString());
        store.Set(RtspPortKey, RtspPort.ToString(inv));
        store.Set(RtspPathKey, RtspPath);
        store.Set(UdpHostKey, UdpHost);
        store.Set(UdpPortKey, UdpPort.ToString(inv));
        store.Set(HeightKey, Height.ToString(inv));
        store.Set(FpsKey, Fps.ToString(inv));
        store.Set(BitrateKey, BitrateKbps.ToString(inv));
        store.Set(EncoderKey, Encoder);
        store.Set(ShowOsdKey, ShowOsd.ToString(inv));
        store.Save();
    }

    private static int Int(string? text, int fallback) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}

/// <summary>
/// The GStreamer pipelines of a stream. The HUD frames go into one producer (appsrc → I420 →
/// intervideosink); the RTSP server's media and the UDP sender each read that channel with
/// intervideosrc, so they come and go independently of the producer.
/// </summary>
public static class StreamPipelines
{
    public const string AppSrcName = "src";
    public const string Channel = "mp3-hud";

    /// <summary>Encoders tried for "auto", most portable first (x264 is in every desktop build).</summary>
    public static readonly string[] EncoderOrder = ["x264enc", "openh264enc", "mfh264enc", "vtenc_h264"];

    public static string RawCaps(StreamSettings s) =>
        $"video/x-raw,format=I420,width={s.Width},height={s.Height},framerate={s.Fps}/1";

    /// <summary>appsrc fed with BGRA frames of the stream size, timestamped as they arrive.</summary>
    public static string Producer(StreamSettings s) =>
        $"appsrc name={AppSrcName} is-live=true do-timestamp=true format=time block=false max-buffers=3 leaky-type=downstream " +
        $"caps=\"video/x-raw,format=BGRA,width={s.Width},height={s.Height},framerate={s.Fps}/1\" " +
        $"! videoconvert ! {RawCaps(s)} ! intervideosink channel={Channel}";

    /// <summary>One encoder element with low-latency settings, keyframe every second.</summary>
    public static string Encoder(string name, StreamSettings s) => name switch
    {
        "x264enc" => $"x264enc tune=zerolatency speed-preset=ultrafast bitrate={s.BitrateKbps} key-int-max={s.Fps} byte-stream=true",
        "openh264enc" => $"openh264enc bitrate={s.BitrateKbps * 1000} gop-size={s.Fps} complexity=low rate-control=bitrate",
        "mfh264enc" => $"mfh264enc bitrate={s.BitrateKbps} gop-size={s.Fps} low-latency=true",
        "vtenc_h264" => $"vtenc_h264 realtime=true bitrate={s.BitrateKbps} max-keyframe-interval={s.Fps} allow-frame-reordering=false",
        _ => name,
    };

    /// <summary>The encoder to use: the chosen one, or the first installed one for "auto"; null if none.</summary>
    public static string? PickEncoder(StreamSettings s, Func<string, bool> installed)
    {
        if (!string.Equals(s.Encoder, StreamSettings.AutoEncoder, StringComparison.OrdinalIgnoreCase))
            return installed(s.Encoder) ? s.Encoder : null;
        return EncoderOrder.FirstOrDefault(installed);
    }

    private static string Source(StreamSettings s) =>
        $"intervideosrc channel={Channel} ! {RawCaps(s)} ! queue max-size-buffers=2 leaky=downstream";

    /// <summary>The RTSP media: a bin with the payloader named pay0, as gst-rtsp-server expects.</summary>
    public static string RtspLaunch(StreamSettings s, string encoder) =>
        $"( {Source(s)} ! {Encoder(encoder, s)} ! h264parse ! rtph264pay name=pay0 pt=96 config-interval=1 )";

    /// <summary>
    /// The HUD to a Matroska file (H.264): plays while being written and survives a crash. The path
    /// uses forward slashes: gst-launch treats a backslash as an escape.
    /// </summary>
    public static string FileRecorder(StreamSettings s, string encoder, string path) =>
        $"{Source(s)} ! {Encoder(encoder, s)} ! h264parse ! matroskamux ! filesink location=\"{path.Replace('\\', '/').Replace("\"", "")}\"";

    /// <summary>RTP/H.264 to one address, as ArduPilot companions send it (port 5600 by default).</summary>
    public static string UdpSender(StreamSettings s, string encoder) =>
        $"{Source(s)} ! {Encoder(encoder, s)} ! h264parse ! rtph264pay pt=96 config-interval=1 " +
        $"! udpsink host={s.UdpHost} port={s.UdpPort} sync=false async=false";
}
