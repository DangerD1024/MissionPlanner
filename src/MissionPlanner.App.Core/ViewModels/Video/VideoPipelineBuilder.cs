using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MissionPlanner.App.Core.ViewModels.Video;

/// <summary>
/// Turns a <see cref="VideoSourceConfig"/> into GStreamer pipeline text for the video engine.
/// Every pipeline ends in a leaky one-frame queue, BGRA conversion and an unsynchronised appsink
/// named <see cref="SinkName"/> that keeps one frame: the HUD always shows the newest frame and
/// never waits on the clock.
/// </summary>
public static partial class VideoPipelineBuilder
{
    /// <summary>The engine reads frames from the appsink with this name.</summary>
    public const string SinkName = "outsink";

    /// <summary>The ending a hand-written pipeline needs (shown to users).</summary>
    public const string SinkTail = "videoconvert ! video/x-raw,format=BGRA ! appsink name=outsink";

    /// <summary>The ending every built pipeline gets.</summary>
    public const string Tail =
        "queue max-size-buffers=1 leaky=downstream ! videoconvert ! video/x-raw,format=BGRA ! appsink name=outsink sync=false max-buffers=1 drop=true";

    /// <summary>Mission Planner's example pipeline for "Set GStreamer source".</summary>
    public const string ExamplePipeline =
        "videotestsrc ! video/x-raw, width=1280, height=720, framerate=30/1 ! videoconvert ! video/x-raw,format=BGRA ! appsink name=outsink";

    private const int UdpBufferSize = 1 << 19;

    /// <summary>What is wrong with the source, or null when a pipeline can be built.</summary>
    public static string? Validate(VideoSourceConfig c)
    {
        switch (c.Type)
        {
            case VideoSourceType.UdpH264:
            case VideoSourceType.UdpH265:
            case VideoSourceType.UdpMpegTs:
                return ValidPort(c.Port) ? null : "Enter a UDP port between 1 and 65535.";
            case VideoSourceType.TcpMpegTs:
                if (!ValidHost(c.Host))
                    return "Enter the TCP server's host name or IP address.";
                return ValidPort(c.Port) ? null : "Enter a TCP port between 1 and 65535.";
            case VideoSourceType.Rtsp:
                return ValidUrl(c.Url, "rtsp", "rtsps", "rtspt", "rtsph")
                    ? null
                    : "Enter an RTSP address, e.g. rtsp://192.168.43.1:8554/fpv_stream";
            case VideoSourceType.MjpegHttp:
                return ValidUrl(c.Url, "http", "https") ? null : "Enter an HTTP address, e.g. http://192.168.1.10:8080/video";
            case VideoSourceType.TestPattern:
                return null;
            case VideoSourceType.Custom:
                return string.IsNullOrWhiteSpace(c.Pipeline) ? "Enter a GStreamer pipeline." : null;
            case VideoSourceType.CaptureDevice:
                return c.Device.Contains('"') || c.Device.Contains('\n') ? "The device name cannot contain quotes or line breaks." : null;
            default:
                return "Unknown source type.";
        }
    }

    /// <summary>The pipeline for a valid source; throws <see cref="ArgumentException"/> for an invalid one.</summary>
    public static string Build(VideoSourceConfig c) => Build(c, CurrentCapturePlatform);

    /// <summary>Where capture devices come from: Media Foundation, Video4Linux or AVFoundation.</summary>
    public enum CapturePlatform
    {
        Windows,
        Linux,
        MacOS,
    }

    public static CapturePlatform CurrentCapturePlatform =>
        OperatingSystem.IsWindows() ? CapturePlatform.Windows : OperatingSystem.IsMacOS() ? CapturePlatform.MacOS : CapturePlatform.Linux;

    /// <summary>
    /// A capture device (EasyCap, UVC webcam, HDMI grabber) on a platform; decodebin3 takes raw
    /// (YUY2, NV12…) as well as MJPEG output, whichever the device offers.
    /// </summary>
    public static string Capture(string device, CapturePlatform platform)
    {
        var d = device.Trim();
        var source = platform switch
        {
            CapturePlatform.Windows => d.Length == 0 ? "mfvideosrc" : $"mfvideosrc device-name={Quote(d)}",
            CapturePlatform.MacOS => int.TryParse(d, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                ? $"avfvideosrc device-index={index}"
                : "avfvideosrc",
            _ => d.Length == 0 ? "v4l2src" : $"v4l2src device={Quote(d)}",
        };
        return $"{source} ! decodebin3 ! {Tail}";
    }

    /// <summary>The pipeline as it would run on a given platform (capture devices differ).</summary>
    public static string Build(VideoSourceConfig c, CapturePlatform platform)
    {
        if (Validate(c) is { } error)
            throw new ArgumentException(error, nameof(c));

        var low = c.LowLatency;
        var port = c.Port.ToString(CultureInfo.InvariantCulture);
        return c.Type switch
        {
            VideoSourceType.UdpH264 => Udp(port, "H264", low) + " ! rtph264depay ! h264parse ! " + Decoder(c.Decoder, "avdec_h264", low) + " ! " + Tail,
            VideoSourceType.UdpH265 => Udp(port, "H265", low) + " ! rtph265depay ! h265parse ! " + Decoder(c.Decoder, "avdec_h265", low) + " ! " + Tail,
            VideoSourceType.UdpMpegTs =>
                $"udpsrc port={port} reuse=false buffer-size={UdpBufferSize} caps=\"video/mpegts,systemstream=(boolean)true,packetsize=(int)188\" ! decodebin3 ! video/x-raw ! {Tail}",
            VideoSourceType.TcpMpegTs =>
                $"tcpclientsrc host={Quote(c.Host.Trim())} port={port} ! decodebin3 ! video/x-raw ! {Tail}",
            VideoSourceType.Rtsp => Rtsp(c),
            VideoSourceType.MjpegHttp =>
                $"souphttpsrc location={Quote(c.Url.Trim())} is-live=true do-timestamp=true timeout=5 ! multipartdemux ! image/jpeg ! jpegdec ! {Tail}",
            VideoSourceType.TestPattern =>
                $"videotestsrc is-live=true ! video/x-raw,width=1280,height=720,framerate=30/1 ! {Tail}",
            VideoSourceType.Custom => CompleteCustom(c.Pipeline),
            VideoSourceType.CaptureDevice => Capture(c.Device, platform),
            _ => throw new ArgumentOutOfRangeException(nameof(c)),
        };
    }

    /// <summary>
    /// A hand-written pipeline as it will run: unchanged when it already has an appsink named
    /// <see cref="SinkName"/>, otherwise with the standard BGRA appsink ending appended.
    /// </summary>
    public static string CompleteCustom(string pipeline)
    {
        var text = pipeline.Trim();
        if (HasAppSink(text))
            return text;
        text = text.TrimEnd('!', ' ', '\t', '\r', '\n');
        return text + " ! " + Tail;
    }

    /// <summary>True if the text contains <c>appsink ... name=outsink</c> (in either order of properties).</summary>
    public static bool HasAppSink(string pipeline) => AppSinkPattern().IsMatch(pipeline);

    /// <summary>A gst-launch property value in double quotes, with quotes and backslashes escaped.</summary>
    public static string Quote(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var ch in value)
        {
            if (ch is '"' or '\\')
                sb.Append('\\');
            sb.Append(ch);
        }

        return sb.Append('"').ToString();
    }

    private static string Udp(string port, string encoding, bool lowLatency)
    {
        // exclusive: with a shared port the kernel hands the packets to one listener at random
        // (another ground station listening for video too), and "No video" would be all we could say
        var src = $"udpsrc port={port} reuse=false buffer-size={UdpBufferSize} caps=\"application/x-rtp,media=(string)video,clock-rate=(int)90000,encoding-name=(string){encoding}\"";
        // the jitter buffer reorders late packets at the price of delay
        return lowLatency ? src : src + " ! rtpjitterbuffer latency=200";
    }

    private static string Decoder(VideoDecoder decoder, string software, bool lowLatency) => decoder switch
    {
        // frame threading holds several frames back; slice threading does not
        VideoDecoder.Software => lowLatency ? software + " thread-type=slice" : software,
        _ => "decodebin3",
    };

    private static string Rtsp(VideoSourceConfig c)
    {
        var sb = new StringBuilder("rtspsrc location=").Append(Quote(c.Url.Trim()));
        sb.Append(" latency=").Append(c.LowLatency ? "0" : "200");
        switch (c.Transport)
        {
            case RtspTransport.Tcp:
                sb.Append(" protocols=tcp");
                break;
            case RtspTransport.Udp:
                sb.Append(" protocols=udp");
                break;
        }

        // the caps filter picks the video stream when the server also offers audio
        sb.Append(" udp-reconnect=true do-retransmission=false ! application/x-rtp,media=video ! decodebin3 ! ").Append(Tail);
        return sb.ToString();
    }

    private static bool ValidPort(int port) => port is > 0 and <= 65535;

    private static bool ValidHost(string host) =>
        !string.IsNullOrWhiteSpace(host) && Uri.CheckHostName(host.Trim()) != UriHostNameType.Unknown;

    private static bool ValidUrl(string url, params string[] schemes) =>
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && schemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase) &&
        !string.IsNullOrEmpty(uri.Host);

    [GeneratedRegex(@"\bappsink\b[^!]*\bname\s*=\s*""?outsink\b", RegexOptions.IgnoreCase)]
    private static partial Regex AppSinkPattern();
}
