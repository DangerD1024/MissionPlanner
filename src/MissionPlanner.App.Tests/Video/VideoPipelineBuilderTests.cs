using MissionPlanner.App.Core.ViewModels.Video;

namespace MissionPlanner.App.Tests.Video;

[TestClass]
public sealed class VideoPipelineBuilderTests
{
    private const string Tail = VideoPipelineBuilder.Tail;

    [TestMethod]
    public void EveryPipelineEndsInTheLowLatencyBgraAppsink()
    {
        var sources = new[]
        {
            new VideoSourceConfig { Type = VideoSourceType.UdpH264 },
            new VideoSourceConfig { Type = VideoSourceType.UdpH265, Port = 5601 },
            new VideoSourceConfig { Type = VideoSourceType.UdpMpegTs, Port = 5000 },
            new VideoSourceConfig { Type = VideoSourceType.TcpMpegTs, Host = "10.0.0.2", Port = 5760 },
            new VideoSourceConfig { Type = VideoSourceType.Rtsp, Url = "rtsp://192.168.43.1:8554/fpv_stream" },
            new VideoSourceConfig { Type = VideoSourceType.MjpegHttp, Url = "http://cam.local/video" },
            new VideoSourceConfig { Type = VideoSourceType.TestPattern },
            new VideoSourceConfig { Type = VideoSourceType.Custom, Pipeline = "videotestsrc" },
        };
        foreach (var s in sources)
        {
            var p = VideoPipelineBuilder.Build(s);
            Assert.EndsWith(" ! " + Tail, p, s.Type.ToString());
            Assert.IsTrue(VideoPipelineBuilder.HasAppSink(p), s.Type.ToString());
        }

        StringAssert.Contains(Tail, "sync=false");
        StringAssert.Contains(Tail, "max-buffers=1 drop=true");
        StringAssert.Contains(Tail, "leaky=downstream");
        StringAssert.Contains(Tail, "format=BGRA");
    }

    [TestMethod]
    public void UdpH264()
    {
        var p = VideoPipelineBuilder.Build(new VideoSourceConfig { Type = VideoSourceType.UdpH264, Port = 5600 });
        Assert.AreEqual(
            "udpsrc port=5600 reuse=false buffer-size=524288 caps=\"application/x-rtp,media=(string)video,clock-rate=(int)90000,encoding-name=(string)H264\" " +
            "! rtph264depay ! h264parse ! decodebin3 ! " + Tail, p);

        var sw = VideoPipelineBuilder.Build(new VideoSourceConfig { Type = VideoSourceType.UdpH264, Port = 5602, Decoder = VideoDecoder.Software });
        StringAssert.Contains(sw, "port=5602");
        StringAssert.Contains(sw, "! h264parse ! avdec_h264 thread-type=slice ! ");
        Assert.DoesNotContain("rtpjitterbuffer", sw);

        // without low latency a jitter buffer reorders packets
        var smooth = VideoPipelineBuilder.Build(new VideoSourceConfig { Type = VideoSourceType.UdpH264, LowLatency = false, Decoder = VideoDecoder.Software });
        StringAssert.Contains(smooth, "\" ! rtpjitterbuffer latency=200 ! rtph264depay");
        StringAssert.Contains(smooth, "! avdec_h264 ! ");
    }

    [TestMethod]
    public void UdpH265()
    {
        var p = VideoPipelineBuilder.Build(new VideoSourceConfig { Type = VideoSourceType.UdpH265, Port = 5601, Decoder = VideoDecoder.Software });
        StringAssert.StartsWith(p, "udpsrc port=5601 ");
        StringAssert.Contains(p, "encoding-name=(string)H265\"");
        StringAssert.Contains(p, "! rtph265depay ! h265parse ! avdec_h265 thread-type=slice ! ");
    }

    [TestMethod]
    public void Rtsp()
    {
        var p = VideoPipelineBuilder.Build(new VideoSourceConfig { Type = VideoSourceType.Rtsp, Url = " rtsp://192.168.43.1:8554/fpv_stream " });
        Assert.AreEqual(
            "rtspsrc location=\"rtsp://192.168.43.1:8554/fpv_stream\" latency=0 udp-reconnect=true do-retransmission=false " +
            "! application/x-rtp,media=video ! decodebin3 ! " + Tail, p);

        var tcp = VideoPipelineBuilder.Build(new VideoSourceConfig
        {
            Type = VideoSourceType.Rtsp, Url = "rtsp://user:pw@cam/live", Transport = RtspTransport.Tcp, LowLatency = false,
        });
        StringAssert.Contains(tcp, "latency=200 protocols=tcp ");

        var udp = VideoPipelineBuilder.Build(new VideoSourceConfig { Type = VideoSourceType.Rtsp, Url = "rtsps://cam/live", Transport = RtspTransport.Udp });
        StringAssert.Contains(udp, " protocols=udp ");
    }

    [TestMethod]
    public void TcpAndUdpMpegTs()
    {
        Assert.AreEqual(
            "tcpclientsrc host=\"192.168.1.20\" port=5000 ! decodebin3 ! video/x-raw ! " + Tail,
            VideoPipelineBuilder.Build(new VideoSourceConfig { Type = VideoSourceType.TcpMpegTs, Host = "192.168.1.20", Port = 5000 }));
        Assert.AreEqual(
            "udpsrc port=5004 reuse=false buffer-size=524288 caps=\"video/mpegts,systemstream=(boolean)true,packetsize=(int)188\" ! decodebin3 ! video/x-raw ! " + Tail,
            VideoPipelineBuilder.Build(new VideoSourceConfig { Type = VideoSourceType.UdpMpegTs, Port = 5004 }));
    }

    [TestMethod]
    public void MjpegOverHttp()
    {
        Assert.AreEqual(
            "souphttpsrc location=\"http://192.168.1.10:8080/video?x=1&y=2\" is-live=true do-timestamp=true timeout=5 " +
            "! multipartdemux ! image/jpeg ! jpegdec ! " + Tail,
            VideoPipelineBuilder.Build(new VideoSourceConfig { Type = VideoSourceType.MjpegHttp, Url = "http://192.168.1.10:8080/video?x=1&y=2" }));
    }

    [TestMethod]
    public void CustomPipelinesGetTheAppsinkOnlyWhenTheyLackIt()
    {
        Assert.AreEqual("videotestsrc ! " + Tail,
            VideoPipelineBuilder.Build(new VideoSourceConfig { Type = VideoSourceType.Custom, Pipeline = "  videotestsrc ! \n" }));

        var own = "videotestsrc ! videoconvert ! video/x-raw,format=BGRA ! appsink name=outsink";
        Assert.AreEqual(own, VideoPipelineBuilder.Build(new VideoSourceConfig { Type = VideoSourceType.Custom, Pipeline = own }));
        Assert.AreEqual(VideoPipelineBuilder.ExamplePipeline, VideoPipelineBuilder.CompleteCustom(VideoPipelineBuilder.ExamplePipeline));

        Assert.IsTrue(VideoPipelineBuilder.HasAppSink("x ! appsink sync=false name=outsink max-buffers=1"));
        Assert.IsTrue(VideoPipelineBuilder.HasAppSink("x ! appsink name=\"outsink\""));
        Assert.IsTrue(VideoPipelineBuilder.HasAppSink("x ! appsink name = outsink"));
        Assert.IsFalse(VideoPipelineBuilder.HasAppSink("x ! appsink name=other"));
        Assert.IsFalse(VideoPipelineBuilder.HasAppSink("x ! appsink ! fakesink name=outsink"));
        Assert.IsFalse(VideoPipelineBuilder.HasAppSink("x ! appsink name=outsink2"));
    }

    [TestMethod]
    public void QuotesEscapeQuotesAndBackslashes()
    {
        Assert.AreEqual("\"plain\"", VideoPipelineBuilder.Quote("plain"));
        Assert.AreEqual("\"a \\\"b\\\" c\\\\d\"", VideoPipelineBuilder.Quote("a \"b\" c\\d"));
        var p = VideoPipelineBuilder.Build(new VideoSourceConfig { Type = VideoSourceType.Rtsp, Url = "rtsp://cam/a\"b" });
        StringAssert.Contains(p, "location=\"rtsp://cam/a\\\"b\"");
    }

    [TestMethod]
    public void InvalidSourcesAreExplainedAndNotBuilt()
    {
        var bad = new (VideoSourceConfig Source, string Message)[]
        {
            (new() { Type = VideoSourceType.UdpH264, Port = 0 }, "UDP port"),
            (new() { Type = VideoSourceType.UdpH265, Port = 70000 }, "UDP port"),
            (new() { Type = VideoSourceType.UdpMpegTs, Port = -1 }, "UDP port"),
            (new() { Type = VideoSourceType.TcpMpegTs, Host = "", Port = 5000 }, "host"),
            (new() { Type = VideoSourceType.TcpMpegTs, Host = "bad host!", Port = 5000 }, "host"),
            (new() { Type = VideoSourceType.TcpMpegTs, Host = "10.0.0.1", Port = 0 }, "TCP port"),
            (new() { Type = VideoSourceType.Rtsp, Url = "" }, "RTSP"),
            (new() { Type = VideoSourceType.Rtsp, Url = "http://cam/live" }, "RTSP"),
            (new() { Type = VideoSourceType.Rtsp, Url = "rtsp://" }, "RTSP"),
            (new() { Type = VideoSourceType.MjpegHttp, Url = "rtsp://cam" }, "HTTP"),
            (new() { Type = VideoSourceType.Custom, Pipeline = "   " }, "pipeline"),
        };
        foreach (var (source, message) in bad)
        {
            var error = VideoPipelineBuilder.Validate(source);
            Assert.IsNotNull(error, $"{source.Type} {source.Url}{source.Host}:{source.Port}");
            StringAssert.Contains(error, message);
            Assert.ThrowsExactly<ArgumentException>(() => VideoPipelineBuilder.Build(source));
        }

        Assert.IsNull(VideoPipelineBuilder.Validate(new VideoSourceConfig { Type = VideoSourceType.TcpMpegTs, Host = "drone.local", Port = 1 }));
        Assert.IsNull(VideoPipelineBuilder.Validate(new VideoSourceConfig { Type = VideoSourceType.Rtsp, Url = "RTSP://10.0.0.1/x" }));
        Assert.IsNull(VideoPipelineBuilder.Validate(new VideoSourceConfig { Type = VideoSourceType.UdpH264, Port = 65535 }));
    }

    [TestMethod]
    public void SummariesNameTheStream()
    {
        Assert.AreEqual("UDP H.264 :5600", new VideoSourceConfig { Type = VideoSourceType.UdpH264, Port = 5600 }.Summary);
        Assert.AreEqual("TCP MPEG-TS cam:5000", new VideoSourceConfig { Type = VideoSourceType.TcpMpegTs, Host = "cam", Port = 5000 }.Summary);
        Assert.AreEqual("rtsp://x/y", new VideoSourceConfig { Type = VideoSourceType.Rtsp, Url = "rtsp://x/y" }.DisplayName);
        Assert.AreEqual("Mine", new VideoSourceConfig { Type = VideoSourceType.Rtsp, Url = "rtsp://x/y", Name = "Mine" }.DisplayName);
    }
}
