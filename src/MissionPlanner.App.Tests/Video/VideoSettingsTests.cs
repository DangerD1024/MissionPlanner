using MissionPlanner.App.Core.Video;
using MissionPlanner.App.Core.ViewModels.Video;

namespace MissionPlanner.App.Tests.Video;

[TestClass]
public sealed class VideoSettingsTests
{
    [TestMethod]
    public void FirstRunOffersTheUsualSources()
    {
        var s = VideoSettings.Load(new MemoryVideoStore());
        CollectionAssert.AreEqual(new[] { "udp5600", "udp5601", "test" }, s.Sources.Select(x => x.Id).ToArray());
        Assert.AreEqual(5600, s.Sources[0].Port);
        Assert.AreEqual(VideoSourceType.UdpH265, s.Sources[1].Type);
        Assert.AreEqual(5601, s.Sources[1].Port);
        Assert.AreEqual("udp5600", s.SelectedId);
        Assert.IsFalse(s.AutoStart);
        Assert.AreEqual(VideoAspect.Fill, s.Aspect);
        Assert.IsNull(s.LastPipeline);
    }

    [TestMethod]
    public void EverythingRoundTripsUnderNextVideoKeys()
    {
        var store = new MemoryVideoStore();
        var s = VideoSettings.Load(store);
        s.Sources.Add(new VideoSourceConfig
        {
            Id = "cam", Name = "Gimbal \"main\" <rtsp>", Type = VideoSourceType.Rtsp, Url = "rtsp://10.0.0.5:8554/main?a=1&b=2",
            LowLatency = false, Transport = RtspTransport.Tcp, Decoder = VideoDecoder.Software,
        });
        s.Sources.Add(new VideoSourceConfig { Id = "ts", Type = VideoSourceType.TcpMpegTs, Host = "drone", Port = 5000 });
        s.Sources.Add(new VideoSourceConfig { Id = "raw", Type = VideoSourceType.Custom, Pipeline = "videotestsrc ! appsink name=outsink" });
        s.SelectedId = "cam";
        s.AutoStart = true;
        s.Aspect = VideoAspect.Fit;
        s.LastPipeline = "videotestsrc";
        s.Save(store);

        Assert.AreEqual(1, store.Saves);
        Assert.IsTrue(store.Values.Keys.All(k => k.StartsWith("next_video_", StringComparison.Ordinal)), string.Join(",", store.Values.Keys));
        // keys must be valid XML element names for the settings file
        Assert.IsTrue(store.Values.Keys.All(k => k.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')));
        StringAssert.Contains(store.Values[VideoSettings.SourcesKey], "\"Type\":\"Rtsp\"");
        Assert.DoesNotContain("Summary", store.Values[VideoSettings.SourcesKey]);

        var back = VideoSettings.Load(store);
        Assert.HasCount(6, back.Sources);
        var cam = back.Selected!;
        Assert.AreEqual("cam", cam.Id);
        Assert.AreEqual("Gimbal \"main\" <rtsp>", cam.Name);
        Assert.AreEqual(VideoSourceType.Rtsp, cam.Type);
        Assert.AreEqual("rtsp://10.0.0.5:8554/main?a=1&b=2", cam.Url);
        Assert.IsFalse(cam.LowLatency);
        Assert.AreEqual(RtspTransport.Tcp, cam.Transport);
        Assert.AreEqual(VideoDecoder.Software, cam.Decoder);
        Assert.AreEqual("drone", back.Sources[4].Host);
        Assert.AreEqual(5000, back.Sources[4].Port);
        Assert.AreEqual("videotestsrc ! appsink name=outsink", back.Sources[5].Pipeline);
        Assert.IsTrue(back.AutoStart);
        Assert.AreEqual(VideoAspect.Fit, back.Aspect);
        Assert.AreEqual("videotestsrc", back.LastPipeline);
    }

    [TestMethod]
    public void AnEmptyListStaysEmpty()
    {
        var store = new MemoryVideoStore();
        var s = VideoSettings.Load(store);
        s.Sources.Clear();
        s.SelectedId = null;
        s.Save(store);

        var back = VideoSettings.Load(store);
        Assert.IsEmpty(back.Sources);
        Assert.IsNull(back.Selected);
    }

    [TestMethod]
    public void DamagedSettingsFallBackSafely()
    {
        var store = new MemoryVideoStore();
        store.Values[VideoSettings.SourcesKey] = "{not json";
        store.Values[VideoSettings.AutoStartKey] = "maybe";
        store.Values[VideoSettings.AspectKey] = "Stretch";
        store.Values[VideoSettings.SelectedKey] = "gone";
        var s = VideoSettings.Load(store);
        Assert.HasCount(3, s.Sources);
        Assert.AreEqual("udp5600", s.SelectedId);
        Assert.IsFalse(s.AutoStart);
        Assert.AreEqual(VideoAspect.Fill, s.Aspect);

        // missing and repeated ids are replaced so selection stays unambiguous
        store.Values[VideoSettings.SourcesKey] = "[{\"Name\":\"a\",\"Type\":\"Rtsp\",\"Url\":\"rtsp://x\"},{\"Id\":\"x\",\"Name\":\"b\"},{\"Id\":\"x\",\"Name\":\"c\"}, null]";
        s = VideoSettings.Load(store);
        Assert.HasCount(3, s.Sources);
        Assert.AreEqual(3, s.Sources.Select(x => x.Id).Distinct().Count());
        Assert.IsTrue(s.Sources.All(x => !string.IsNullOrEmpty(x.Id)));
        Assert.AreEqual("x", s.Sources[1].Id);
        Assert.AreEqual(s.Sources[0].Id, s.SelectedId);
        // fields left out of the JSON take their defaults
        Assert.AreEqual(VideoSourceType.UdpH264, s.Sources[1].Type);
        Assert.IsTrue(s.Sources[1].LowLatency);
    }
}

[TestClass]
public sealed class VideoLayoutTests
{
    private static void AreClose(VideoRect expected, VideoRect actual)
    {
        Assert.AreEqual(expected.X, actual.X, 1e-9, $"{expected} vs {actual}");
        Assert.AreEqual(expected.Y, actual.Y, 1e-9, $"{expected} vs {actual}");
        Assert.AreEqual(expected.Width, actual.Width, 1e-9, $"{expected} vs {actual}");
        Assert.AreEqual(expected.Height, actual.Height, 1e-9, $"{expected} vs {actual}");
    }

    [TestMethod]
    public void FillCropsTheOverhangCentred()
    {
        // 16:9 frame in a 4:3 HUD: the sides are cut
        var (src, dst) = VideoLayout.Place(1920, 1080, 480, 360, VideoAspect.Fill);
        AreClose(new VideoRect(240, 0, 1440, 1080), src);
        AreClose(new VideoRect(0, 0, 480, 360), dst);

        // 4:3 frame in a wide area: top and bottom are cut
        (src, dst) = VideoLayout.Place(640, 480, 800, 300, VideoAspect.Fill);
        AreClose(new VideoRect(0, 120, 640, 240), src);
        AreClose(new VideoRect(0, 0, 800, 300), dst);
    }

    [TestMethod]
    public void FitLetterboxesCentred()
    {
        var (src, dst) = VideoLayout.Place(1920, 1080, 480, 360, VideoAspect.Fit);
        AreClose(new VideoRect(0, 0, 1920, 1080), src);
        AreClose(new VideoRect(0, 45, 480, 270), dst);

        (src, dst) = VideoLayout.Place(640, 480, 800, 300, VideoAspect.Fit);
        AreClose(new VideoRect(0, 0, 640, 480), src);
        AreClose(new VideoRect(200, 0, 400, 300), dst);
    }

    [TestMethod]
    public void SameShapeFillsExactlyEitherWay()
    {
        foreach (var aspect in new[] { VideoAspect.Fill, VideoAspect.Fit })
        {
            var (src, dst) = VideoLayout.Place(640, 480, 480, 360, aspect);
            AreClose(new VideoRect(0, 0, 640, 480), src);
            AreClose(new VideoRect(0, 0, 480, 360), dst);
        }
    }

    [TestMethod]
    public void DegenerateSizesDoNotThrow()
    {
        var (_, dst) = VideoLayout.Place(0, 0, 480, 360, VideoAspect.Fill);
        AreClose(new VideoRect(0, 0, 480, 360), dst);
        (_, dst) = VideoLayout.Place(640, 480, 0, -5, VideoAspect.Fit);
        AreClose(new VideoRect(0, 0, 0, 0), dst);
    }

    [TestMethod]
    public void StatusLines()
    {
        Assert.AreEqual("", VideoLayout.StatusText(VideoStreamState.Stopped, null));
        Assert.AreEqual("", VideoLayout.StatusText(VideoStreamState.Playing, null));
        Assert.AreEqual("Connecting video…", VideoLayout.StatusText(VideoStreamState.Connecting, null));
        Assert.AreEqual("No video", VideoLayout.StatusText(VideoStreamState.Waiting, null));
        Assert.AreEqual("Video lost, reconnecting…", VideoLayout.StatusText(VideoStreamState.Reconnecting, null));
        Assert.AreEqual("Video lost: Could not open resource for reading and writing. Reconnecting…",
            VideoLayout.StatusText(VideoStreamState.Reconnecting, "Could not open resource for reading and writing."));
        Assert.AreEqual("Video error: GStreamer was not found", VideoLayout.StatusText(VideoStreamState.Failed, "GStreamer was not found."));
        Assert.AreEqual("Video error: unknown", VideoLayout.StatusText(VideoStreamState.Failed, ""));

        Assert.IsTrue(VideoLayout.ShowsFrame(VideoStreamState.Playing));
        Assert.IsTrue(VideoLayout.ShowsFrame(VideoStreamState.Waiting));
        Assert.IsFalse(VideoLayout.ShowsFrame(VideoStreamState.Reconnecting));
        Assert.IsFalse(VideoLayout.ShowsFrame(VideoStreamState.Failed));
        Assert.IsFalse(VideoLayout.ShowsFrame(VideoStreamState.Stopped));
    }
}

[TestClass]
public sealed class VideoFrameBufferTests
{
    [TestMethod]
    public void ReaderGetsEachNewFrameOnceAndNothingAfterClear()
    {
        var buffer = new VideoFrameBuffer();
        long seq = 0;
        Assert.IsFalse(buffer.TryRead(ref seq, (_, _, _, _) => Assert.Fail("no frame yet")));

        var frame = new byte[4 * 2 * 3];
        frame[0] = 7;
        buffer.Write(frame, 2, 3, 8);
        byte first = 0;
        var size = (0, 0, 0);
        Assert.IsTrue(buffer.TryRead(ref seq, (px, w, h, stride) =>
        {
            first = px[0];
            size = (w, h, stride);
        }));
        Assert.AreEqual(7, first);
        Assert.AreEqual((2, 3, 8), size);
        Assert.IsFalse(buffer.TryRead(ref seq, (_, _, _, _) => Assert.Fail("same frame twice")));

        // frames the reader missed are skipped: it sees the newest
        frame[0] = 8;
        buffer.Write(frame, 2, 3, 8);
        frame[0] = 9;
        buffer.Write(frame, 2, 3, 8);
        Assert.IsTrue(buffer.TryRead(ref seq, (px, _, _, _) => first = px[0]));
        Assert.AreEqual(9, first);

        buffer.Write(frame, 2, 3, 8);
        buffer.Clear();
        Assert.IsFalse(buffer.TryRead(ref seq, (_, _, _, _) => Assert.Fail("cleared")));
        Assert.AreEqual(0, buffer.Width);
    }

    [TestMethod]
    public void WriterAndReaderOnDifferentThreadsNeverTear()
    {
        var buffer = new VideoFrameBuffer();
        const int w = 64, h = 48, stride = w * 4;
        using var stop = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            var frame = new byte[stride * h];
            for (byte n = 0; !stop.IsCancellationRequested; n++)
            {
                Array.Fill(frame, n);
                buffer.Write(frame, w, h, stride);
            }
        });

        long seq = 0;
        var reads = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 300)
        {
            buffer.TryRead(ref seq, (px, _, _, _) =>
            {
                reads++;
                var v = px[0];
                for (var i = 0; i < stride * h; i += 97)
                    Assert.AreEqual(v, px[i], "torn frame");
            });
        }

        stop.Cancel();
        writer.Wait();
        Assert.IsGreaterThan(10, reads);
    }
}
