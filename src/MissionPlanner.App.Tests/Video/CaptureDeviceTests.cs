using System.Diagnostics;
using MissionPlanner.App.Core.Video;
using MissionPlanner.App.Core.ViewModels.Video;
using Platform = MissionPlanner.App.Core.ViewModels.Video.VideoPipelineBuilder.CapturePlatform;

namespace MissionPlanner.App.Tests.Video;

/// <summary>Video from a capture device: EasyCap / analog grabbers, UVC webcams, HDMI capture.</summary>
[TestClass]
public sealed class CaptureDeviceTests
{
    [TestMethod]
    public void EachPlatformOpensTheDeviceItsOwnWay()
    {
        Assert.AreEqual("mfvideosrc device-name=\"USB2.0 Video Capture\" ! decodebin3 ! " + VideoPipelineBuilder.Tail,
            VideoPipelineBuilder.Capture("USB2.0 Video Capture", Platform.Windows));
        Assert.AreEqual("v4l2src device=\"/dev/video2\" ! decodebin3 ! " + VideoPipelineBuilder.Tail,
            VideoPipelineBuilder.Capture("/dev/video2", Platform.Linux));
        Assert.AreEqual("avfvideosrc device-index=1 ! decodebin3 ! " + VideoPipelineBuilder.Tail,
            VideoPipelineBuilder.Capture("1", Platform.MacOS));

        // no device named: the first one
        StringAssert.StartsWith(VideoPipelineBuilder.Capture("", Platform.Windows), "mfvideosrc ! ");
        StringAssert.StartsWith(VideoPipelineBuilder.Capture(" ", Platform.Linux), "v4l2src ! ");
        StringAssert.StartsWith(VideoPipelineBuilder.Capture("FaceTime HD Camera", Platform.MacOS), "avfvideosrc ! ");
    }

    [TestMethod]
    public void ASourceBuildsForThePlatformAndIsDescribed()
    {
        var c = new VideoSourceConfig { Type = VideoSourceType.CaptureDevice, Device = "/dev/video0" };
        Assert.IsNull(VideoPipelineBuilder.Validate(c));
        StringAssert.StartsWith(VideoPipelineBuilder.Build(c, Platform.Linux), "v4l2src device=\"/dev/video0\"");
        Assert.AreEqual("Capture: /dev/video0", c.Summary);
        Assert.AreEqual("Capture device", new VideoSourceConfig { Type = VideoSourceType.CaptureDevice }.Summary);
        Assert.IsNotNull(VideoPipelineBuilder.Validate(c.WithDevice("bad \" name")));
        Assert.IsFalse(c.SameStream(new VideoSourceConfig { Type = VideoSourceType.CaptureDevice, Device = "/dev/video1" }));
    }

    [TestMethod]
    public void TheDeviceIsSavedWithTheSource()
    {
        var store = new MemoryVideoStore();
        var settings = new VideoSettings
        {
            Sources = [new VideoSourceConfig { Id = "cap", Name = "EasyCap", Type = VideoSourceType.CaptureDevice, Device = "USB2.0 Video Capture" }],
            SelectedId = "cap",
        };
        settings.Save(store);
        var loaded = VideoSettings.Load(store);
        Assert.AreEqual(VideoSourceType.CaptureDevice, loaded.Selected!.Type);
        Assert.AreEqual("USB2.0 Video Capture", loaded.Selected.Device);
    }

    [TestMethod]
    public void TheDialogShowsTheDeviceFieldForCaptureSources()
    {
        var item = new VideoSourceItem(new VideoSourceConfig { Type = VideoSourceType.UdpH264 });
        Assert.IsFalse(item.ShowDevice);
        item.Type = VideoSettingsViewModel.Types.Single(t => t.Type == VideoSourceType.CaptureDevice);
        Assert.IsTrue(item.ShowDevice);
        Assert.IsFalse(item.ShowPort);
        Assert.IsFalse(item.ShowUrl);
        item.Device = "  /dev/video0 ";
        Assert.AreEqual("/dev/video0", item.ToConfig().Device);
        StringAssert.Contains(item.PipelinePreview, "decodebin3");
    }

    /// <summary>Real devices, when this computer has one (a webcam light may come on for a moment).</summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public void ADeviceFoundHereDeliversFrames()
    {
        if (!VideoStream.IsAvailable(out var message))
            Assert.Inconclusive(message);
        var devices = CaptureDevices.List();
        Console.WriteLine("capture devices: " + string.Join(" | ", devices));
        if (devices.Count == 0)
            Assert.Inconclusive("no capture device on this computer");

        using var stream = new VideoStream();
        stream.Start(VideoPipelineBuilder.Build(new VideoSourceConfig { Type = VideoSourceType.CaptureDevice, Device = devices[0].Id }));
        var sw = Stopwatch.StartNew();
        while (stream.FramesReceived < 5 && sw.Elapsed < TimeSpan.FromSeconds(20) && stream.State != VideoStreamState.Failed)
            Thread.Sleep(50);
        if (stream.State == VideoStreamState.Failed && stream.Error?.Contains("busy", StringComparison.OrdinalIgnoreCase) == true)
            Assert.Inconclusive("the device is in use by another program");
        Assert.IsGreaterThanOrEqualTo(5L, stream.FramesReceived, $"{stream.State} {stream.Error} {stream.Pipeline}");
        Console.WriteLine($"{devices[0]}: {stream.Frames.Width}x{stream.Frames.Height}");
    }
}

internal static class VideoSourceConfigTestExtensions
{
    public static VideoSourceConfig WithDevice(this VideoSourceConfig c, string device)
    {
        var copy = c.Clone();
        copy.Device = device;
        return copy;
    }
}
