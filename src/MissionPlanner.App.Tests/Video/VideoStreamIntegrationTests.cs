using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MissionPlanner.App.Core.Video;
using MissionPlanner.App.Core.ViewModels.Video;

namespace MissionPlanner.App.Tests.Video;

/// <summary>
/// The engine against real GStreamer pipelines in this process (test pattern, and an H.264 RTP
/// stream over UDP loopback sent by a second pipeline). Inconclusive where GStreamer, or the
/// plugins a test needs, are not installed.
/// </summary>
[TestClass]
public sealed class VideoStreamIntegrationTests
{
    private static void RequireGStreamer()
    {
        if (!VideoStream.IsAvailable(out var message))
            Assert.Inconclusive(message);
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition())
                return true;
            Thread.Sleep(20);
        }

        return condition();
    }

    /// <summary>Skips the test when a pipeline fails for want of a plugin on this machine.</summary>
    private static void InconclusiveIfMissingElement(VideoStream stream)
    {
        // parsing happens on the stream's thread, within moments of Start
        WaitFor(() => stream.State != VideoStreamState.Connecting, TimeSpan.FromMilliseconds(500));
        if (stream.State == VideoStreamState.Failed && stream.Error is { } e && e.Contains("no element", StringComparison.OrdinalIgnoreCase))
            Assert.Inconclusive(e);
    }

    private static int FreeUdpPort()
    {
        using var s = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)s.Client.LocalEndPoint!).Port;
    }

    private static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>A 320×240 test pattern, encoded by <paramref name="encode"/> (its own appsink only keeps the engine happy).</summary>
    private static string Sender(string encode) =>
        "videotestsrc is-live=true ! video/x-raw,width=320,height=240,framerate=30/1 ! tee name=t " +
        $"t. ! queue ! {encode} t. ! queue ! " + VideoPipelineBuilder.Tail;

    /// <summary>Runs a sender pipeline and the source's receiver; the receiver must show 320×240 frames.</summary>
    private static void AssertReceives(VideoSourceConfig source, string sender, bool senderFirst = false)
    {
        RequireGStreamer();
        using var tx = new VideoStream();
        using var rx = new VideoStream { MinRetryDelay = TimeSpan.FromMilliseconds(200), MaxRetryDelay = TimeSpan.FromMilliseconds(500) };
        if (senderFirst)
        {
            tx.Start(sender);
            InconclusiveIfMissingElement(tx);
        }

        rx.Start(VideoPipelineBuilder.Build(source));
        InconclusiveIfMissingElement(rx);
        if (!senderFirst)
        {
            tx.Start(sender);
            InconclusiveIfMissingElement(tx);
        }

        Assert.IsTrue(WaitFor(() => rx.FramesReceived >= 5, TimeSpan.FromSeconds(20)),
            $"receiver {rx.State} {rx.Error}; sender {tx.State} {tx.Error}; pipeline {rx.Pipeline}");
        Assert.AreEqual(320, rx.Frames.Width);
        Assert.AreEqual(240, rx.Frames.Height);
        Assert.AreEqual(VideoStreamState.Playing, rx.State);
    }

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public void UdpPortHeldByAnotherProgramIsReported()
    {
        RequireGStreamer();
        // another listener on the port, sharing it the way ground stations usually bind
        using var other = new UdpClient();
        other.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        other.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var port = ((IPEndPoint)other.Client.LocalEndPoint!).Port;

        using var stream = new VideoStream { MinRetryDelay = TimeSpan.FromMilliseconds(200), MaxRetryDelay = TimeSpan.FromMilliseconds(400) };
        stream.Start(VideoPipelineBuilder.Build(new VideoSourceConfig { Type = VideoSourceType.UdpH264, Port = port }));
        Assert.IsTrue(WaitFor(() => stream.State == VideoStreamState.Reconnecting, TimeSpan.FromSeconds(10)), $"{stream.State} {stream.Error}");
        StringAssert.Contains(stream.Error, "already in use");
    }

    [TestMethod]
    [Timeout(40000, CooperativeCancellation = true)]
    public void UdpH264WithAutomaticDecoder()
    {
        var port = FreeUdpPort();
        AssertReceives(new VideoSourceConfig { Type = VideoSourceType.UdpH264, Port = port, Decoder = VideoDecoder.Auto },
            Sender($"x264enc tune=zerolatency bitrate=500 key-int-max=15 ! rtph264pay config-interval=1 ! udpsink host=127.0.0.1 port={port}"));
    }

    [TestMethod]
    [Timeout(40000, CooperativeCancellation = true)]
    public void UdpH265()
    {
        var port = FreeUdpPort();
        AssertReceives(new VideoSourceConfig { Type = VideoSourceType.UdpH265, Port = port, Decoder = VideoDecoder.Software, LowLatency = false },
            Sender($"x265enc tune=zerolatency bitrate=500 key-int-max=15 ! rtph265pay config-interval=1 ! udpsink host=127.0.0.1 port={port}"));
    }

    [TestMethod]
    [Timeout(40000, CooperativeCancellation = true)]
    public void UdpMpegTs()
    {
        var port = FreeUdpPort();
        AssertReceives(new VideoSourceConfig { Type = VideoSourceType.UdpMpegTs, Port = port },
            Sender($"x264enc tune=zerolatency bitrate=500 key-int-max=15 ! h264parse ! mpegtsmux alignment=7 ! udpsink host=127.0.0.1 port={port}"));
    }

    [TestMethod]
    [Timeout(40000, CooperativeCancellation = true)]
    public void TcpMpegTsWithAudioInTheStream()
    {
        var port = FreeTcpPort();
        // audio first in the stream: the receiver must still pick the video
        AssertReceives(new VideoSourceConfig { Type = VideoSourceType.TcpMpegTs, Host = "127.0.0.1", Port = port },
            $"audiotestsrc is-live=true ! avenc_aac ! aacparse ! mpegtsmux name=mux ! tcpserversink host=127.0.0.1 port={port} " +
            Sender("x264enc tune=zerolatency bitrate=500 key-int-max=15 ! h264parse ! mux."),
            senderFirst: true);
    }

    [TestMethod]
    [Timeout(40000, CooperativeCancellation = true)]
    public void MjpegOverHttp()
    {
        // GStreamer serves multipart JPEG over plain TCP; this proxy puts an HTTP response header in front
        var mjpegPort = FreeTcpPort();
        using var http = new MjpegHttpProxy(mjpegPort);
        AssertReceives(new VideoSourceConfig { Type = VideoSourceType.MjpegHttp, Url = $"http://127.0.0.1:{http.Port}/video" },
            Sender($"jpegenc quality=60 ! multipartmux boundary=frame ! tcpserversink host=127.0.0.1 port={mjpegPort}"),
            senderFirst: true);
    }

    /// <summary>A one-connection HTTP server relaying a multipart/x-mixed-replace byte stream from a TCP port.</summary>
    private sealed class MjpegHttpProxy : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();

        public MjpegHttpProxy(int upstreamPort)
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(() => ServeAsync(upstreamPort));
        }

        public int Port { get; }

        private async Task ServeAsync(int upstreamPort)
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    var stream = client.GetStream();
                    // read the request head
                    var buf = new byte[4096];
                    var head = "";
                    while (!head.Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        var n = await stream.ReadAsync(buf, _cts.Token);
                        if (n == 0)
                            break;
                        head += System.Text.Encoding.ASCII.GetString(buf, 0, n);
                    }

                    var header = "HTTP/1.0 200 OK\r\nContent-Type: multipart/x-mixed-replace; boundary=frame\r\nConnection: close\r\n\r\n"u8.ToArray();
                    await stream.WriteAsync(header, _cts.Token);
                    using var upstream = new TcpClient();
                    await upstream.ConnectAsync(IPAddress.Loopback, upstreamPort, _cts.Token);
                    await upstream.GetStream().CopyToAsync(stream, _cts.Token);
                }
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public void TestPatternDeliversBgraFramesOfTheRightSize()
    {
        RequireGStreamer();
        using var stream = new VideoStream();
        var states = new List<VideoStreamState>();
        stream.StateChanged += (_, _) =>
        {
            lock (states)
                states.Add(stream.State);
        };
        var arrived = 0;
        stream.FrameArrived += (_, _) => Interlocked.Increment(ref arrived);

        stream.Start("videotestsrc is-live=true pattern=red ! video/x-raw,width=320,height=240,framerate=30/1 ! " + VideoPipelineBuilder.Tail);
        Assert.IsTrue(WaitFor(() => stream.FramesReceived >= 5, TimeSpan.FromSeconds(15)), $"frames: {stream.FramesReceived}, state {stream.State} {stream.Error}");
        Assert.AreEqual(VideoStreamState.Playing, stream.State);
        Assert.IsGreaterThanOrEqualTo(5, Volatile.Read(ref arrived));

        long seq = 0;
        int w = 0, h = 0, stride = 0;
        byte b = 0, g = 0, r = 0, a = 0;
        Assert.IsTrue(stream.Frames.TryRead(ref seq, (px, fw, fh, fs) =>
        {
            w = fw;
            h = fh;
            stride = fs;
            var mid = fh / 2 * fs + fw / 2 * 4;
            (b, g, r, a) = (px[mid], px[mid + 1], px[mid + 2], px[mid + 3]);
        }));
        Assert.AreEqual(320, w);
        Assert.AreEqual(240, h);
        Assert.AreEqual(320 * 4, stride);
        // red in BGRA byte order
        Assert.IsGreaterThan(200, (int)r, $"BGRA {b},{g},{r},{a}");
        Assert.IsLessThan(60, (int)g);
        Assert.IsLessThan(60, (int)b);

        stream.Stop();
        Assert.AreEqual(VideoStreamState.Stopped, stream.State);
        lock (states)
        {
            Assert.AreEqual(VideoStreamState.Connecting, states[0]);
            Assert.Contains(VideoStreamState.Playing, states);
            Assert.AreEqual(VideoStreamState.Stopped, states[^1]);
        }

        // nothing stale is kept after a stop
        Assert.IsFalse(stream.Frames.TryRead(ref seq, (_, _, _, _) => { }));
    }

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public void BuiltTestPatternSourceRuns()
    {
        RequireGStreamer();
        using var stream = new VideoStream();
        stream.Start(VideoPipelineBuilder.Build(new VideoSourceConfig { Type = VideoSourceType.TestPattern }));
        Assert.IsTrue(WaitFor(() => stream.FramesReceived >= 2, TimeSpan.FromSeconds(15)), $"{stream.State} {stream.Error}");
        Assert.AreEqual(1280, stream.Frames.Width);
        Assert.AreEqual(720, stream.Frames.Height);
    }

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public void BrokenPipelinesFailWithoutRetrying()
    {
        RequireGStreamer();
        using var stream = new VideoStream();

        stream.Start("videotestsrc ! ! appsink name=outsink");
        Assert.IsTrue(WaitFor(() => stream.State == VideoStreamState.Failed, TimeSpan.FromSeconds(10)), stream.State.ToString());
        StringAssert.Contains(stream.Error, "Pipeline error");

        stream.Start("videotestsrc ! nosuchelement42 ! " + VideoPipelineBuilder.Tail);
        Assert.IsTrue(WaitFor(() => stream.State == VideoStreamState.Failed, TimeSpan.FromSeconds(10)), stream.State.ToString());
        StringAssert.Contains(stream.Error, "nosuchelement42");

        stream.Start("videotestsrc is-live=true ! fakesink");
        Assert.IsTrue(WaitFor(() => stream.State == VideoStreamState.Failed, TimeSpan.FromSeconds(10)), stream.State.ToString());
        StringAssert.Contains(stream.Error, "appsink named outsink");

        // a pipeline that hands the appsink something other than BGRA
        stream.Start("videotestsrc is-live=true ! video/x-raw,format=I420,width=64,height=48 ! appsink name=outsink");
        Assert.IsTrue(WaitFor(() => stream.State == VideoStreamState.Failed, TimeSpan.FromSeconds(10)), stream.State.ToString());
        StringAssert.Contains(stream.Error, "BGRA");

        stream.Stop();
        Assert.AreEqual(VideoStreamState.Stopped, stream.State);
        Assert.IsNull(stream.Error);
    }

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public void UnreachableRtspServerKeepsRetrying()
    {
        RequireGStreamer();
        using var stream = new VideoStream { MinRetryDelay = TimeSpan.FromMilliseconds(200), MaxRetryDelay = TimeSpan.FromMilliseconds(400) };
        // nothing listens on port 1: rtspsrc reports an error, the engine waits and tries again
        stream.Start(VideoPipelineBuilder.Build(new VideoSourceConfig
        {
            Type = VideoSourceType.Rtsp, Url = "rtsp://127.0.0.1:1/stream name", Transport = RtspTransport.Tcp,
        }));
        InconclusiveIfMissingElement(stream);
        Assert.IsTrue(WaitFor(() => stream.State == VideoStreamState.Reconnecting, TimeSpan.FromSeconds(15)), $"{stream.State} {stream.Error}");
        Assert.IsNotNull(stream.Error);
        var sw = Stopwatch.StartNew();
        stream.Stop();
        Assert.IsLessThan(3000, sw.ElapsedMilliseconds, "stop waits for the pipeline");
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public void UdpH264StreamPlaysAndReconnectsWhenTheSenderComesBack()
    {
        RequireGStreamer();
        var port = FreeUdpPort();

        // sender: a test pattern encoded to RTP/H.264 on UDP loopback (its own appsink only keeps the engine happy)
        var sendPipeline =
            "videotestsrc is-live=true ! video/x-raw,width=320,height=240,framerate=30/1 ! tee name=t " +
            $"t. ! queue ! x264enc tune=zerolatency bitrate=500 key-int-max=15 ! rtph264pay config-interval=1 ! udpsink host=127.0.0.1 port={port} " +
            "t. ! queue ! " + VideoPipelineBuilder.Tail;

        using var receiver = new VideoStream
        {
            NoFrameNotice = TimeSpan.FromMilliseconds(500),
            StallTimeout = TimeSpan.FromMilliseconds(1500),
            MinRetryDelay = TimeSpan.FromMilliseconds(200),
            MaxRetryDelay = TimeSpan.FromMilliseconds(500),
        };
        var states = new List<VideoStreamState>();
        receiver.StateChanged += (_, _) =>
        {
            lock (states)
                states.Add(receiver.State);
        };
        receiver.Start(VideoPipelineBuilder.Build(new VideoSourceConfig { Type = VideoSourceType.UdpH264, Port = port, Decoder = VideoDecoder.Software }));
        InconclusiveIfMissingElement(receiver);

        // no sender yet: up, but no video
        Assert.IsTrue(WaitFor(() => receiver.State == VideoStreamState.Waiting, TimeSpan.FromSeconds(10)), $"{receiver.State} {receiver.Error}");

        using var sender = new VideoStream();
        sender.Start(sendPipeline);
        Thread.Sleep(300);
        InconclusiveIfMissingElement(sender);

        Assert.IsTrue(WaitFor(() => receiver.FramesReceived >= 10, TimeSpan.FromSeconds(15)), $"receiver {receiver.State} {receiver.Error}, sender {sender.State} {sender.Error}");
        Assert.AreEqual(320, receiver.Frames.Width);
        Assert.AreEqual(240, receiver.Frames.Height);

        // the sender goes away: short stall, then the pipeline is rebuilt
        sender.Stop();
        Assert.IsTrue(WaitFor(() => { lock (states) return states.Contains(VideoStreamState.Reconnecting); }, TimeSpan.FromSeconds(10)),
            string.Join(",", states));

        // and it comes back
        var before = receiver.FramesReceived;
        sender.Start(sendPipeline);
        Assert.IsTrue(WaitFor(() => receiver.FramesReceived >= before + 10 && receiver.State == VideoStreamState.Playing, TimeSpan.FromSeconds(15)),
            $"receiver {receiver.State} {receiver.Error}");

        lock (states)
        {
            var i = states.IndexOf(VideoStreamState.Playing);
            Assert.IsGreaterThanOrEqualTo(0, i);
            CollectionAssert.IsSubsetOf(new[] { VideoStreamState.Waiting, VideoStreamState.Reconnecting }, states.Skip(i).ToList());
            Assert.AreEqual(VideoStreamState.Playing, states[^1]);
        }
    }
}
