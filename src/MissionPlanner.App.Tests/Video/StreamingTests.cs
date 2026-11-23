using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.Video;
using MissionPlanner.App.Core.ViewModels.Video;

namespace MissionPlanner.App.Tests.Video;

/// <summary>A streamer that records what it is asked to do.</summary>
internal sealed class FakeStreamer : IHudStreamer
{
    public List<StreamSettings> Started { get; } = new();
    public int Stops { get; private set; }
    public int Pushed { get; private set; }
    public string? FailWith { get; set; }

    public bool IsRunning { get; private set; }
    public string? Error { get; set; }
    public string? EncoderName => IsRunning ? "x264enc" : null;
    public int RtspPort => IsRunning ? 8554 : 0;

    public event EventHandler? StateChanged;

    public void Start(StreamSettings settings)
    {
        if (FailWith != null)
            throw new InvalidOperationException(FailWith);
        Started.Add(settings);
        IsRunning = true;
    }

    public void Push(byte[] bgra) => Pushed++;

    public void Stop()
    {
        Stops++;
        IsRunning = false;
        IsRecording = false;
    }

    public bool IsRecording { get; private set; }

    public List<string> Recordings { get; } = new();

    public void StartRecording(string path)
    {
        if (!IsRunning)
            throw new InvalidOperationException("not running");
        Recordings.Add(path);
        IsRecording = true;
    }

    public void StopRecording() => IsRecording = false;

    public void RaiseError(string error)
    {
        Error = error;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => Stop();
}

[TestClass]
public sealed class StreamSettingsTests
{
    [TestMethod]
    public void DefaultsServeRtspAt720p30WithTheOsd()
    {
        var s = StreamSettings.Load(new MemoryVideoStore());
        Assert.AreEqual(StreamMode.RtspServer, s.Mode);
        Assert.AreEqual(8554, s.RtspPort);
        Assert.AreEqual("hud", s.RtspPath);
        Assert.AreEqual(1280, s.Width);
        Assert.AreEqual(720, s.Height);
        Assert.AreEqual(30, s.Fps);
        Assert.IsTrue(s.ShowOsd);
        Assert.IsNull(s.Validate());
        Assert.AreEqual(854, new StreamSettings { Height = 480 }.Width, "16:9, even");
        Assert.AreEqual(1920, new StreamSettings { Height = 1080 }.Width);
    }

    [TestMethod]
    public void EverythingRoundTripsUnderNextStreamKeys()
    {
        var store = new MemoryVideoStore();
        new StreamSettings
        {
            Mode = StreamMode.Both, RtspPort = 9000, RtspPath = "live/hud", UdpHost = "192.168.1.20", UdpPort = 5601,
            Height = 1080, Fps = 25, BitrateKbps = 6000, Encoder = "openh264enc", ShowOsd = false,
        }.Save(store);
        Assert.IsTrue(store.Values.Keys.All(k => k.StartsWith("next_stream_", StringComparison.Ordinal)));

        var s = StreamSettings.Load(store);
        Assert.AreEqual(StreamMode.Both, s.Mode);
        Assert.AreEqual(9000, s.RtspPort);
        Assert.AreEqual("live/hud", s.RtspPath);
        Assert.AreEqual("192.168.1.20", s.UdpHost);
        Assert.AreEqual(5601, s.UdpPort);
        Assert.AreEqual(1080, s.Height);
        Assert.AreEqual(25, s.Fps);
        Assert.AreEqual(6000, s.BitrateKbps);
        Assert.AreEqual("openh264enc", s.Encoder);
        Assert.IsFalse(s.ShowOsd);
    }

    [TestMethod]
    public void DamagedValuesFallBack()
    {
        var store = new MemoryVideoStore();
        store.Set(StreamSettings.ModeKey, "Carrier pigeon");
        store.Set(StreamSettings.HeightKey, "777");
        store.Set(StreamSettings.FpsKey, "999");
        store.Set(StreamSettings.BitrateKey, "x");
        var s = StreamSettings.Load(store);
        Assert.AreEqual(StreamMode.RtspServer, s.Mode);
        Assert.AreEqual(720, s.Height);
        Assert.AreEqual(60, s.Fps);
        Assert.AreEqual(2500, s.BitrateKbps);
    }

    [TestMethod]
    public void InvalidSettingsAreExplained()
    {
        Assert.IsNotNull(new StreamSettings { RtspPort = 0 }.Validate());
        Assert.IsNotNull(new StreamSettings { RtspPath = "a b" }.Validate());
        Assert.IsNotNull(new StreamSettings { Mode = StreamMode.UdpPush, UdpHost = " " }.Validate());
        Assert.IsNotNull(new StreamSettings { Mode = StreamMode.UdpPush, UdpPort = 70000 }.Validate());
        Assert.IsNull(new StreamSettings { Mode = StreamMode.UdpPush, RtspPort = 0 }.Validate(), "RTSP fields do not matter for UDP only");
        Assert.IsNotNull(new StreamSettings { BitrateKbps = 10 }.Validate());
    }

    [TestMethod]
    public void PipelinesShareOneChannelAndPayloadAsPay0()
    {
        var s = new StreamSettings { Height = 720, Fps = 30, BitrateKbps = 3000, UdpHost = "10.0.0.5", UdpPort = 5600 };
        var producer = StreamPipelines.Producer(s);
        StringAssert.StartsWith(producer, "appsrc name=src is-live=true do-timestamp=true");
        StringAssert.Contains(producer, "format=BGRA,width=1280,height=720,framerate=30/1");
        StringAssert.EndsWith(producer, "intervideosink channel=mp3-hud");

        var rtsp = StreamPipelines.RtspLaunch(s, "x264enc");
        StringAssert.StartsWith(rtsp, "( intervideosrc channel=mp3-hud");
        StringAssert.Contains(rtsp, "x264enc tune=zerolatency speed-preset=ultrafast bitrate=3000 key-int-max=30");
        StringAssert.EndsWith(rtsp, "rtph264pay name=pay0 pt=96 config-interval=1 )");

        var udp = StreamPipelines.UdpSender(s, "openh264enc");
        StringAssert.Contains(udp, "openh264enc bitrate=3000000 gop-size=30");
        StringAssert.EndsWith(udp, "udpsink host=10.0.0.5 port=5600 sync=false async=false");
    }

    [TestMethod]
    public void AutoPicksTheFirstInstalledEncoder()
    {
        var auto = new StreamSettings();
        Assert.AreEqual("x264enc", StreamPipelines.PickEncoder(auto, _ => true));
        Assert.AreEqual("openh264enc", StreamPipelines.PickEncoder(auto, e => e != "x264enc"));
        Assert.AreEqual("vtenc_h264", StreamPipelines.PickEncoder(auto, e => e == "vtenc_h264"));
        Assert.IsNull(StreamPipelines.PickEncoder(auto, _ => false));
        Assert.AreEqual("mfh264enc", StreamPipelines.PickEncoder(new StreamSettings { Encoder = "mfh264enc" }, _ => true));
        Assert.IsNull(StreamPipelines.PickEncoder(new StreamSettings { Encoder = "mfh264enc" }, e => e == "x264enc"), "a chosen encoder is not swapped");
    }

    [TestMethod]
    public void AddressesListRtspForEachInterfaceThenUdp()
    {
        var urls = StreamingViewModel.BuildUrls(new StreamSettings { Mode = StreamMode.Both, RtspPath = "/hud/" }, 8554, ["192.168.1.5"]);
        CollectionAssert.AreEqual(new[]
        {
            "rtsp://192.168.1.5:8554/hud",
            "rtsp://127.0.0.1:8554/hud",
            "udp://127.0.0.1:5600 (RTP H.264)",
        }, urls.ToArray());
    }
}

[TestClass]
public sealed class StreamingViewModelTests
{
    private static (StreamingViewModel Vm, FakeStreamer Streamer, MemoryVideoStore Store, List<Notification> Toasts) Create()
    {
        var services = new VideoTestUi().CreateServices();
        var store = new MemoryVideoStore();
        var streamer = new FakeStreamer();
        var toasts = new List<Notification>();
        services.Notified += (_, n) => toasts.Add(n);
        return (new StreamingViewModel(services, store, streamer), streamer, store, toasts);
    }

    [TestMethod]
    public void StartingShowsWhereToConnect()
    {
        var (vm, streamer, _, toasts) = Create();
        Assert.IsTrue(vm.Start());
        Assert.IsTrue(vm.IsStreaming);
        Assert.IsNotNull(vm.Active);
        Assert.HasCount(1, streamer.Started);
        Assert.IsTrue(vm.Urls.Any(u => u == "rtsp://127.0.0.1:8554/hud"));
        Assert.IsTrue(toasts.Single().Text.Contains("rtsp://"), toasts.Single().Text);
        StringAssert.Contains(vm.StatusText, "1280×720");

        vm.Stop();
        Assert.IsFalse(vm.IsStreaming);
        Assert.IsNull(vm.Active);
        Assert.IsEmpty(vm.Urls);
    }

    [TestMethod]
    public void RecordingWithoutStreamingRunsItsOwnFrameSource()
    {
        var (vm, streamer, _, _) = Create();
        var path = Path.Combine(TestEnvironment.DataRoot, "hud.mkv");
        Assert.IsTrue(vm.StartRecording(path));
        Assert.IsTrue(vm.IsRecording);
        Assert.IsTrue(vm.IsProducing, "the pump renders frames");
        Assert.IsFalse(vm.IsStreaming);
        Assert.AreEqual(StreamMode.RecordOnly, streamer.Started.Single().Mode);
        CollectionAssert.AreEqual(new[] { path }, streamer.Recordings);

        // starting the stream saves that file and carries on in a new one on the real stream
        Assert.IsTrue(vm.Start());
        Assert.IsTrue(vm.IsRecording);
        Assert.AreEqual(StreamMode.RtspServer, streamer.Started.Last().Mode);
        Assert.HasCount(2, streamer.Recordings);

        // stopping the stream ends the recording with it
        vm.Stop();
        Assert.IsFalse(vm.IsRecording);
        Assert.IsFalse(vm.IsProducing);
        Assert.IsFalse(streamer.IsRunning);
    }

    [TestMethod]
    public void RecorderPipelineWritesMatroskaWithForwardSlashes()
    {
        var p = StreamPipelines.FileRecorder(new StreamSettings(), "x264enc", @"C:\logs\HUD 1.mkv");
        StringAssert.Contains(p, "intervideosrc channel=mp3-hud");
        StringAssert.Contains(p, "! h264parse ! matroskamux ! filesink location=\"C:/logs/HUD 1.mkv\"");
    }

    [TestMethod]
    public void AFailedStartIsReportedAsAnErrorToast()
    {
        var (vm, streamer, _, toasts) = Create();
        streamer.FailWith = "No H.264 encoder is installed";
        Assert.IsFalse(vm.Start());
        Assert.IsFalse(vm.IsStreaming);
        Assert.AreEqual(MessageKind.Error, toasts.Single().Kind);
        StringAssert.Contains(vm.StatusText, "No H.264 encoder");
    }

    [TestMethod]
    public void PipelineErrorsShowInTheStatus()
    {
        var (vm, streamer, _, _) = Create();
        vm.Start();
        streamer.RaiseError("Internal data stream error.");
        StringAssert.Contains(vm.StatusText, "Internal data stream error.");
    }

    [TestMethod]
    public void TheOsdToggleIsSavedAndAppliesToTheRunningStream()
    {
        var (vm, streamer, store, _) = Create();
        vm.Start();
        vm.ShowOsd = false;
        Assert.IsFalse(vm.Active!.ShowOsd, "takes effect at once, no restart");
        Assert.HasCount(1, streamer.Started);
        Assert.AreEqual("False", store.Get(StreamSettings.ShowOsdKey));
        Assert.IsFalse(StreamSettings.Load(store).ShowOsd);
    }

    [TestMethod]
    public void NewSettingsRestartARunningStream()
    {
        var (vm, streamer, store, _) = Create();
        vm.Apply(new StreamSettings { Height = 480 });
        Assert.IsEmpty(streamer.Started, "not streaming: only saved");
        Assert.AreEqual("480", store.Get(StreamSettings.HeightKey));

        vm.Start();
        vm.Apply(new StreamSettings { Height = 1080, Fps = 25 });
        Assert.HasCount(2, streamer.Started);
        Assert.AreEqual(1080, streamer.Started[1].Height);
        Assert.AreEqual(25, vm.Active!.Fps);
    }

    [TestMethod]
    public void TheSettingsDialogEditsACopyAndSaves()
    {
        var (vm, _, store, _) = Create();
        var dialog = new StreamSettingsViewModel(vm);
        var closed = (bool?)null;
        dialog.CloseRequested += (_, ok) => closed = ok;

        dialog.Mode = dialog.Modes.Single(m => m.Value == StreamMode.UdpPush);
        Assert.IsTrue(dialog.ShowsUdp);
        Assert.IsFalse(dialog.ShowsRtsp);
        dialog.UdpHost = "";
        Assert.IsNotNull(dialog.Error);
        dialog.SaveCommand.Execute(null);
        Assert.IsNull(closed, "invalid: not saved");

        dialog.UdpHost = "192.168.4.2";
        dialog.Resolution = dialog.Resolutions.Single(r => r.Value == 480);
        dialog.ShowOsd = false;
        Assert.AreEqual(StreamMode.RtspServer, vm.Settings.Mode, "nothing applied before Save");
        dialog.SaveCommand.Execute(null);
        Assert.IsTrue(closed);
        Assert.AreEqual(StreamMode.UdpPush, vm.Settings.Mode);
        Assert.AreEqual("192.168.4.2", StreamSettings.Load(store).UdpHost);
        Assert.IsFalse(vm.ShowOsd);
        Assert.AreEqual("Automatic", dialog.Encoders[0].Name.Split(' ')[0]);
    }
}

/// <summary>
/// The real streamer: frames pushed in come back out of our own video engine over RTSP and UDP.
/// Inconclusive where GStreamer, the inter elements, an encoder or the RTSP server are missing.
/// </summary>
[TestClass]
public sealed class HudStreamerIntegrationTests
{
    private static void RequireStreaming()
    {
        if (!HudStreamer.IsAvailable(out var message))
            Assert.Inconclusive(message);
    }

    private static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static int FreeUdpPort()
    {
        using var s = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)s.Client.LocalEndPoint!).Port;
    }

    /// <summary>Pushes a moving pattern at the stream rate until <paramref name="until"/> or the timeout.</summary>
    private static bool PushUntil(IHudStreamer streamer, StreamSettings s, Func<bool> until, TimeSpan timeout)
    {
        var frame = new byte[s.Width * s.Height * 4];
        var sw = Stopwatch.StartNew();
        var n = 0;
        while (sw.Elapsed < timeout)
        {
            // a bar that moves, so the encoder has something to send
            Array.Fill(frame, (byte)40);
            var x0 = n++ * 16 % s.Width;
            for (var y = 0; y < s.Height; y++)
            {
                var row = y * s.Width * 4;
                for (var x = x0; x < Math.Min(s.Width, x0 + 64); x++)
                {
                    frame[row + x * 4] = 255;
                    frame[row + x * 4 + 1] = 255;
                    frame[row + x * 4 + 2] = 255;
                    frame[row + x * 4 + 3] = 255;
                }
            }

            streamer.Push(frame);
            if (until())
                return true;
            Thread.Sleep(1000 / s.Fps);
        }

        return until();
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public void TheHudIsRecordedToAPlayableMatroskaFile()
    {
        RequireStreaming();
        var s = new StreamSettings { Mode = StreamMode.RecordOnly, Height = 480, Fps = 15 };
        var path = Path.Combine(TestEnvironment.DataRoot, "rec-" + Guid.NewGuid().ToString("N") + ".mkv");
        using var streamer = new HudStreamer();
        streamer.Start(s);
        streamer.StartRecording(path);
        PushUntil(streamer, s, () => false, TimeSpan.FromSeconds(3));
        streamer.StopRecording();
        streamer.Stop();

        var bytes = File.ReadAllBytes(path);
        Assert.IsGreaterThan(5000, bytes.Length, "frames were written");
        CollectionAssert.AreEqual(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }, bytes.Take(4).ToArray(), "an EBML (Matroska) header");
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public void RtspServerIsReceivedByOurOwnVideoEngine()
    {
        RequireStreaming();
        if (!RtspServerNativeAvailable(out var why))
            Assert.Inconclusive(why);
        var port = FreeTcpPort();
        var s = new StreamSettings { Mode = StreamMode.RtspServer, RtspPort = port, RtspPath = "hud", Height = 480, Fps = 25, BitrateKbps = 1500 };
        using var streamer = new HudStreamer();
        streamer.Start(s);
        Assert.AreEqual(port, streamer.RtspPort);

        using var rx = new VideoStream { MinRetryDelay = TimeSpan.FromMilliseconds(300), MaxRetryDelay = TimeSpan.FromMilliseconds(600) };
        rx.Start(VideoPipelineBuilder.Build(new VideoSourceConfig { Type = VideoSourceType.Rtsp, Url = $"rtsp://127.0.0.1:{port}/hud", Transport = RtspTransport.Tcp }));
        Assert.IsTrue(PushUntil(streamer, s, () => rx.FramesReceived >= 10, TimeSpan.FromSeconds(30)),
            $"receiver {rx.State} {rx.Error}; streamer error {streamer.Error}");
        Assert.AreEqual(854, rx.Frames.Width);
        Assert.AreEqual(480, rx.Frames.Height);
        Assert.IsNull(streamer.Error);
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public void UdpPushIsReceivedAsAUdpH264Source()
    {
        RequireStreaming();
        var port = FreeUdpPort();
        var s = new StreamSettings { Mode = StreamMode.UdpPush, UdpHost = "127.0.0.1", UdpPort = port, Height = 480, Fps = 25, BitrateKbps = 1500 };
        using var rx = new VideoStream { MinRetryDelay = TimeSpan.FromMilliseconds(300), MaxRetryDelay = TimeSpan.FromMilliseconds(600) };
        rx.Start(VideoPipelineBuilder.Build(new VideoSourceConfig { Type = VideoSourceType.UdpH264, Port = port }));
        using var streamer = new HudStreamer();
        streamer.Start(s);
        Assert.AreEqual(0, streamer.RtspPort);
        Assert.IsTrue(PushUntil(streamer, s, () => rx.FramesReceived >= 10, TimeSpan.FromSeconds(30)),
            $"receiver {rx.State} {rx.Error}; streamer error {streamer.Error}");
        Assert.AreEqual(854, rx.Frames.Width);
        Assert.AreEqual(480, rx.Frames.Height);
    }

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public void ABusyRtspPortIsReportedAndTheStreamerCanStartAgain()
    {
        RequireStreaming();
        if (!RtspServerNativeAvailable(out var why))
            Assert.Inconclusive(why);
        var blocker = new TcpListener(IPAddress.Any, 0);
        blocker.Start();
        try
        {
            var port = ((IPEndPoint)blocker.LocalEndpoint).Port;
            using var streamer = new HudStreamer();
            var ex = Assert.ThrowsExactly<InvalidOperationException>(() => streamer.Start(new StreamSettings { RtspPort = port, Height = 480 }));
            StringAssert.Contains(ex.Message, port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.IsFalse(streamer.IsRunning);

            // and a free port works with the same streamer
            var free = FreeTcpPort();
            streamer.Start(new StreamSettings { RtspPort = free, Height = 480 });
            Assert.IsTrue(streamer.IsRunning);
            streamer.Stop();
            Assert.IsFalse(streamer.IsRunning);

            // stopped means the port is free again
            var again = new TcpListener(IPAddress.Any, free);
            again.Start();
            again.Stop();
        }
        finally
        {
            blocker.Stop();
        }
    }

    private static bool RtspServerNativeAvailable(out string why)
    {
        var ok = RtspServerNative.IsAvailable(out var error);
        why = error ?? "";
        return ok;
    }
}
