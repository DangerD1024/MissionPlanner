using MissionPlanner.App.Core.Video;
using MissionPlanner.App.Core.ViewModels.FlightData;
using MissionPlanner.App.Core.ViewModels.Tools;
using MissionPlanner.App.Tests.FlightData;

namespace MissionPlanner.App.Tests.Tools;

[TestClass]
public class OsdVideoTests
{
    [TestMethod]
    public void VideoTimePlusOffsetIsLogTime()
    {
        var start = new DateTime(2024, 5, 1, 12, 0, 0);
        Assert.AreEqual(start.AddSeconds(12.5), OsdTimeAlignment.LogTimeFor(start, 10, 2.5));
        Assert.AreEqual(start.AddSeconds(-5), OsdTimeAlignment.LogTimeFor(start, 5, -10));
        Assert.AreEqual(2.5, OsdTimeAlignment.OffsetFor(start, start.AddSeconds(12.5), 10), 1e-9);
        Assert.AreEqual(900, OsdTimeAlignment.OffsetFor(start, start.AddHours(1), 0), "clamped to the slider");
    }

    [TestMethod]
    public void TimelineGivesTheLastSnapshotAtOrBefore()
    {
        var t0 = new DateTime(2024, 5, 1, 12, 0, 0);
        var tl = new TlogHudTimeline([(t0, new HudModel { Roll = 1 }), (t0.AddMilliseconds(100), new HudModel { Roll = 2 })]);
        Assert.IsNull(tl.At(t0.AddMilliseconds(-1)));
        Assert.AreEqual(1, tl.At(t0)!.Roll);
        Assert.AreEqual(1, tl.At(t0.AddMilliseconds(99))!.Roll);
        Assert.AreEqual(2, tl.At(t0.AddSeconds(30))!.Roll);
        Assert.AreEqual(t0.AddMilliseconds(100), TlogHudTimeline.Bucket(t0.AddMilliseconds(150)));
    }

    [TestMethod]
    public void TlogBecomesAHudTimelineAlignedByTheOffset()
    {
        var path = TlogWriter.CreateFlight(TlogWriter.TempPath("osd"), 50);
        try
        {
            var tl = TlogHudTimeline.Load(path);
            Assert.IsGreaterThanOrEqualTo(40, tl.Count);
            Assert.AreEqual(5.0, tl.Duration.TotalSeconds, 0.11);

            // roll is k degrees from T0 + k × 100 ms: video 1.0 s with a 2.0 s offset is log + 3.0 s
            var hud = tl.At(OsdTimeAlignment.LogTimeFor(tl.Start, 1.0, 2.0))!;
            Assert.AreEqual(30, hud.Roll, 0.6);
            Assert.IsTrue(hud.Connected);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void ConfigBesideTheVideoRoundTrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "osdcfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var video = Path.Combine(dir, "flight.mp4");
            OsdVideoConfig.Save(video, @"C:\logs\flight.tlog", -12.5);
            var saved = OsdVideoConfig.Load(video);
            Assert.IsNotNull(saved);
            Assert.AreEqual(@"C:\logs\flight.tlog", saved.Value.Tlog);
            Assert.AreEqual(-12.5, saved.Value.Offset);
            Assert.IsNull(OsdVideoConfig.Load(Path.Combine(dir, "other.mp4")), "saved for another video");
            Assert.AreEqual(Path.Combine(dir, "flight-overlay.mp4"), OsdVideoViewModel.OutputPathFor(video));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void OutputSizeIsEvenAndOptionallyScaled()
    {
        Assert.AreEqual((1920, 1080), OsdVideoPipeline.OutputSize(1920, 1080, true));
        Assert.AreEqual((1280, 720), OsdVideoPipeline.OutputSize(1920, 1080, false));
        Assert.AreEqual((640, 480), OsdVideoPipeline.OutputSize(640, 480, false));
        Assert.AreEqual((320, 240), OsdVideoPipeline.OutputSize(321, 241, true));
        StringAssert.Contains(OsdVideoPipeline.EncodePipeline(new OsdRenderOptions(320, 240, "x264enc", 2000), 15, 1, @"C:\v\o.mp4"),
            "location=\"C:/v/o.mp4\"");
    }

    // ---- against GStreamer --------------------------------------------------------------------

    /// <summary>Paints a white 16×16 square top left and records the frame times.</summary>
    private sealed class SquareCompositor : IHudCompositor
    {
        public int Calls;

        public Task ComposeAsync(byte[] bgra, int width, int height, HudModel hud, CancellationToken cancel)
        {
            Calls++;
            for (var y = 0; y < 16; y++)
            for (var x = 0; x < 16; x++)
            {
                var i = 4 * (y * width + x);
                bgra[i] = bgra[i + 1] = bgra[i + 2] = 255;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class NoCompositor : IHudCompositor
    {
        public Task ComposeAsync(byte[] bgra, int width, int height, HudModel hud, CancellationToken cancel) => Task.CompletedTask;
    }

    private static void RequireGStreamer()
    {
        if (!OsdVideoPipeline.IsAvailable(out var message))
            Assert.Inconclusive(message);
        if (!GstNative.HasElement("videotestsrc") || !GstNative.HasElement("x264enc"))
            Assert.Inconclusive("videotestsrc or x264enc is not installed.");
    }

    /// <summary>A black 320×240 test clip of 30 frames at 15 fps (2 s).</summary>
    private static string MakeClip(string dir)
    {
        var path = Path.Combine(dir, "clip.mp4");
        var p = GstNative.ParseLaunch(
            "videotestsrc num-buffers=30 pattern=black ! video/x-raw,width=320,height=240,framerate=15/1 ! videoconvert ! x264enc ! h264parse ! mp4mux " +
            $"! filesink location=\"{path.Replace('\\', '/')}\"", out var error);
        if (error != IntPtr.Zero)
            Assert.Inconclusive(GstNative.TakeErrorMessage(error));
        try
        {
            GstNative.ElementSetState(p, GstNative.StatePlaying);
            var bus = GstNative.ElementGetBus(p);
            var msg = GstNative.BusTimedPopFiltered(bus, 20_000_000_000UL, GstNative.MessageEos | GstNative.MessageError);
            Assert.AreNotEqual(IntPtr.Zero, msg, "the test clip was not written in time");
            GstNative.MiniObjectUnref(msg);
            GstNative.ObjectUnref(bus);
        }
        finally
        {
            GstNative.ElementSetState(p, GstNative.StateNull);
            GstNative.ObjectUnref(p);
        }

        return path;
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task RendersEveryFrameWithItsTimeAndTheOverlay()
    {
        RequireGStreamer();
        var dir = Path.Combine(Path.GetTempPath(), "osdvideo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var clip = MakeClip(dir);
            var info = OsdVideoPipeline.Probe(clip);
            Assert.AreEqual(320, info.Width);
            Assert.AreEqual(240, info.Height);
            Assert.AreEqual(15, info.Fps, 0.01);
            Assert.AreEqual(2.0, info.DurationSeconds, 0.1);

            var times = new List<double>();
            var compositor = new SquareCompositor();
            var output = Path.Combine(dir, "clip-overlay.mp4");
            var reports = 0;
            var frames = await OsdVideoPipeline.RenderAsync(clip, output, new OsdRenderOptions(320, 240, "x264enc", 1000),
                t =>
                {
                    times.Add(t);
                    return new HudModel();
                }, compositor, new SyncProgress(() => reports++), CancellationToken.None);

            Assert.AreEqual(30, frames);
            Assert.AreEqual(30, compositor.Calls);
            Assert.AreEqual(30, reports);
            Assert.AreEqual(0, times[0], 1e-6);
            Assert.AreEqual(29 / 15.0, times[^1], 0.01);
            for (var i = 1; i < times.Count; i++)
                Assert.IsGreaterThan(times[i - 1], times[i]);
            var outInfo = OsdVideoPipeline.Probe(output);
            Assert.AreEqual(320, outInfo.Width);
            Assert.AreEqual(2.0, outInfo.DurationSeconds, 0.15);

            // the square is in the new video, the rest is still black
            var (frame, at) = await OsdVideoPipeline.PreviewAsync(output, 1.0, outInfo.StartSeconds, 0, 0, _ => new HudModel(), new NoCompositor());
            Assert.AreEqual(1.0, at, 0.1, $"at {at}, start {outInfo.StartSeconds}");
            Assert.IsGreaterThan(200, (int)frame.Pixels[4 * (8 * frame.Width + 8) + 1]);
            Assert.IsLessThan(60, (int)frame.Pixels[4 * (120 * frame.Width + 160) + 1]);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task CancelKeepsAPlayableFile()
    {
        RequireGStreamer();
        var dir = Path.Combine(Path.GetTempPath(), "osdvideo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var clip = MakeClip(dir);
            var output = Path.Combine(dir, "cut.mp4");
            using var cts = new CancellationTokenSource();
            var n = 0;
            var frames = await OsdVideoPipeline.RenderAsync(clip, output, new OsdRenderOptions(320, 240, "x264enc", 1000),
                _ =>
                {
                    if (++n == 10)
                        cts.Cancel();
                    return new HudModel();
                }, new NoCompositor(), null, cts.Token);
            Assert.AreEqual(10, frames);
            var info = OsdVideoPipeline.Probe(output);
            Assert.AreEqual(320, info.Width);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Progress that reports on the calling thread (Progress&lt;T&gt; posts, which a test can't wait for).</summary>
    private sealed class SyncProgress(Action report) : IProgress<OsdRenderProgress>
    {
        public void Report(OsdRenderProgress value) => report();
    }
}
