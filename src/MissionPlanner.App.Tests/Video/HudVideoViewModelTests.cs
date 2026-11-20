using MissionPlanner.App.Core.Video;
using MissionPlanner.App.Core.ViewModels.Video;

namespace MissionPlanner.App.Tests.Video;

[TestClass]
public sealed class HudVideoViewModelTests
{
    private static (HudVideoViewModel Vm, FakeVideoStream Stream, MemoryVideoStore Store, VideoTestUi Ui) Create(Action<MemoryVideoStore>? seed = null)
    {
        var store = new MemoryVideoStore();
        seed?.Invoke(store);
        var stream = new FakeVideoStream();
        var ui = new VideoTestUi();
        var vm = new HudVideoViewModel(ui.CreateServices(), store, stream);
        return (vm, stream, store, ui);
    }

    [TestMethod]
    public void StartRunsTheSelectedSourcesPipelineAndStatusFollowsTheStream()
    {
        var (vm, stream, _, _) = Create();
        Assert.AreEqual(VideoStreamState.Stopped, vm.State);
        Assert.IsFalse(vm.IsActive);

        Assert.IsTrue(vm.Start());
        Assert.HasCount(1, stream.Started);
        Assert.AreEqual(VideoPipelineBuilder.Build(vm.Selected!), stream.Started[0]);
        Assert.AreEqual("udp5600", vm.Playing!.Id);
        Assert.IsTrue(vm.IsActive);
        Assert.AreEqual("Connecting video…", vm.StatusText);

        stream.Set(VideoStreamState.Waiting);
        Assert.AreEqual("No video", vm.StatusText);
        Assert.IsTrue(vm.ShowsFrame);
        stream.Set(VideoStreamState.Playing);
        Assert.AreEqual("", vm.StatusText);
        stream.Set(VideoStreamState.Reconnecting, "Internal data stream error.");
        Assert.AreEqual("Video lost: Internal data stream error. Reconnecting…", vm.StatusText);
        Assert.IsFalse(vm.ShowsFrame);

        vm.Stop();
        Assert.AreEqual(VideoStreamState.Stopped, vm.State);
        Assert.AreEqual("", vm.StatusText);
        Assert.IsNull(vm.Playing);
        Assert.IsFalse(vm.IsActive);
    }

    [TestMethod]
    public void AnInvalidSourceShowsAnErrorInsteadOfStarting()
    {
        var (vm, stream, _, _) = Create();
        Assert.IsFalse(vm.Start(new VideoSourceConfig { Type = VideoSourceType.Rtsp, Url = "" }));
        Assert.IsEmpty(stream.Started);
        Assert.AreEqual(VideoStreamState.Failed, vm.State);
        StringAssert.StartsWith(vm.StatusText, "Video error: Enter an RTSP address");
        Assert.IsTrue(vm.IsActive, "Stop clears the error");

        // a late notification from the stopped stream does not hide the error
        stream.Set(VideoStreamState.Stopped);
        Assert.AreEqual(VideoStreamState.Failed, vm.State);

        vm.Stop();
        Assert.AreEqual("", vm.StatusText);
        Assert.AreEqual(VideoStreamState.Stopped, vm.State);
    }

    [TestMethod]
    public void StreamFailuresShowTheirReason()
    {
        var (vm, stream, _, _) = Create();
        vm.Start();
        stream.Set(VideoStreamState.Failed, "GStreamer was not found. Install GStreamer 1.x.");
        Assert.AreEqual("Video error: GStreamer was not found. Install GStreamer 1.x", vm.StatusText);
    }

    [TestMethod]
    public void SelectingASourceSavesAndStartsIt()
    {
        var (vm, stream, store, _) = Create();
        vm.Select("test");
        Assert.AreEqual("test", vm.Selected!.Id);
        Assert.AreEqual("test", store.Values[VideoSettings.SelectedKey]);
        StringAssert.StartsWith(stream.Started.Single(), "videotestsrc");

        vm.Select("nope");
        Assert.AreEqual("test", vm.Selected!.Id);
        Assert.HasCount(1, stream.Started);

        vm.Select("udp5601", start: false);
        Assert.HasCount(1, stream.Started);
    }

    [TestMethod]
    public void OptionsPersist()
    {
        var (vm, _, store, _) = Create();
        var saves = store.Saves;
        vm.AutoStart = true;
        vm.Aspect = VideoAspect.Fit;
        Assert.AreEqual("True", store.Values[VideoSettings.AutoStartKey]);
        Assert.AreEqual("Fit", store.Values[VideoSettings.AspectKey]);
        Assert.AreEqual(saves + 2, store.Saves);

        var again = new HudVideoViewModel(new VideoTestUi().CreateServices(), store, new FakeVideoStream());
        Assert.IsTrue(again.AutoStart);
        Assert.AreEqual(VideoAspect.Fit, again.Aspect);
    }

    [TestMethod]
    public void ApplyRestartsARunningSourceOnlyWhenItsStreamChanged()
    {
        var (vm, stream, _, _) = Create();
        vm.Start();
        var edited = vm.Sources.Select(s => s.Clone()).ToList();

        // renaming does not interrupt the video
        edited[0].Name = "Main camera";
        vm.Apply(edited, "udp5600", autoStart: false, VideoAspect.Fill, play: false);
        Assert.HasCount(1, stream.Started);
        Assert.AreEqual("Main camera", vm.Selected!.Name);

        // a new port does
        edited[0].Port = 5700;
        vm.Apply(edited, "udp5600", autoStart: false, VideoAspect.Fill, play: false);
        Assert.HasCount(2, stream.Started);
        StringAssert.Contains(stream.Started[1], "port=5700");

        // deleting it stops it
        vm.Apply(edited.Skip(1), "udp5601", autoStart: true, VideoAspect.Fit, play: false);
        Assert.AreEqual(VideoStreamState.Stopped, vm.State);
        Assert.AreEqual("udp5601", vm.Selected!.Id);
        Assert.IsTrue(vm.AutoStart);
        Assert.AreEqual(VideoAspect.Fit, vm.Aspect);

        // play starts the selected one; an unknown selection falls back to the first
        vm.Apply(edited, "missing", autoStart: true, VideoAspect.Fit, play: true);
        Assert.AreEqual("udp5600", vm.Selected!.Id);
        StringAssert.Contains(stream.Started[^1], "port=5700");
    }

    [TestMethod]
    public async Task SetGStreamerSourceKeepsTheRawPipelineAsASourceAndStartsIt()
    {
        var (vm, stream, store, ui) = Create();

        // first time: the selected source's pipeline is offered as a starting point
        ui.PromptAnswer = null;
        await vm.SetGStreamerSourceAsync();
        Assert.AreEqual(VideoPipelineBuilder.Build(vm.Selected!), ui.LastPromptInitial);
        Assert.IsEmpty(stream.Started, "cancel changes nothing");

        ui.PromptAnswer = "  videotestsrc pattern=ball  ";
        await vm.SetGStreamerSourceAsync();
        var quick = vm.Selected!;
        Assert.AreEqual(VideoSettings.QuickPipelineId, quick.Id);
        Assert.AreEqual(VideoSourceType.Custom, quick.Type);
        Assert.AreEqual("videotestsrc pattern=ball", quick.Pipeline);
        Assert.AreEqual("videotestsrc pattern=ball ! " + VideoPipelineBuilder.Tail, stream.Started.Single());
        Assert.AreEqual("videotestsrc pattern=ball", store.Values[VideoSettings.LastPipelineKey]);

        // next time the last pipeline comes back, and the same source is reused
        ui.PromptAnswer = "videotestsrc ! videoconvert ! video/x-raw,format=BGRA ! appsink name=outsink";
        await vm.SetGStreamerSourceAsync();
        Assert.AreEqual("videotestsrc pattern=ball", ui.LastPromptInitial);
        Assert.AreEqual(1, vm.Sources.Count(s => s.Id == VideoSettings.QuickPipelineId));
        Assert.AreEqual(ui.PromptAnswer, stream.Started[^1]);

        var reloaded = VideoSettings.Load(store);
        Assert.AreEqual(VideoSettings.QuickPipelineId, reloaded.SelectedId);
    }

    [TestMethod]
    public async Task SettingsOpensAsADialog()
    {
        var (vm, _, _, ui) = Create();
        await vm.OpenSettingsAsync();
        Assert.IsInstanceOfType<VideoSettingsViewModel>(ui.Dialogs.Single());
    }

    [TestMethod]
    public void NoSourcesMeansAnErrorNotACrash()
    {
        var (vm, stream, _, _) = Create(store => store.Values[VideoSettings.SourcesKey] = "[]");
        Assert.IsNull(vm.Selected);
        Assert.IsFalse(vm.Start());
        Assert.IsEmpty(stream.Started);
        StringAssert.Contains(vm.StatusText, "no video source");
    }
}

[TestClass]
public sealed class VideoSettingsViewModelTests
{
    private static (VideoSettingsViewModel Page, HudVideoViewModel Owner, FakeVideoStream Stream, MemoryVideoStore Store) Create()
    {
        var store = new MemoryVideoStore();
        var stream = new FakeVideoStream();
        var owner = new HudVideoViewModel(new VideoTestUi().CreateServices(), store, stream);
        return (new VideoSettingsViewModel(owner), owner, stream, store);
    }

    [TestMethod]
    public void EditsStayInTheDialogUntilSaved()
    {
        var (page, owner, _, store) = Create();
        Assert.HasCount(3, page.Sources);
        Assert.AreEqual("udp5600", page.Selected!.Id);
        Assert.AreEqual(VideoAspect.Fill, page.Aspect.Value);

        page.Selected.Name = "Changed";
        page.AddCommand.Execute(null);
        Assert.HasCount(4, page.Sources);
        Assert.AreEqual("New source", page.Selected!.Name);
        Assert.AreEqual("Changed", page.Sources[0].Name);
        Assert.AreEqual("UDP H.264 (5600)", owner.Sources[0].Name, "not applied yet");

        var closed = (bool?)null;
        page.CloseRequested += (_, r) => closed = r;
        page.CancelCommand.Execute(null);
        Assert.IsFalse(closed);
        Assert.HasCount(3, owner.Sources);
        Assert.AreEqual(0, store.Saves);
    }

    [TestMethod]
    public void EditingASourceUpdatesItsPreviewAndValidation()
    {
        var (page, _, _, _) = Create();
        page.AddCommand.Execute(null);
        var item = page.Selected!;
        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        item.Type = VideoSettingsViewModel.Types.First(t => t.Type == VideoSourceType.Rtsp);
        Assert.IsTrue(item.ShowUrl);
        Assert.IsFalse(item.ShowPort);
        Assert.IsTrue(item.ShowTransport);
        Assert.IsFalse(item.ShowDecoder);
        Assert.AreEqual("RTSP URL", item.UrlLabel);
        StringAssert.Contains(item.ValidationError, "RTSP");
        Assert.AreEqual(item.ValidationError, item.PipelinePreview);
        CollectionAssert.Contains(changed, nameof(VideoSourceItem.ShowUrl));
        CollectionAssert.Contains(changed, nameof(VideoSourceItem.PipelinePreview));

        changed.Clear();
        item.Url = "rtsp://10.1.1.1/live";
        Assert.IsNull(item.ValidationError);
        StringAssert.StartsWith(item.PipelinePreview, "rtspsrc location=\"rtsp://10.1.1.1/live\"");
        Assert.AreEqual("rtsp://10.1.1.1/live", item.Summary);
        CollectionAssert.Contains(changed, nameof(VideoSourceItem.PipelinePreview));
        CollectionAssert.Contains(changed, nameof(VideoSourceItem.Summary));

        item.Transport = VideoSettingsViewModel.Transports.First(t => t.Value == RtspTransport.Tcp);
        StringAssert.Contains(item.PipelinePreview, "protocols=tcp");

        item.Type = VideoSettingsViewModel.Types.First(t => t.Type == VideoSourceType.Custom);
        Assert.IsTrue(item.ShowPipeline);
        Assert.IsFalse(item.ShowLowLatency);
        StringAssert.Contains(item.ValidationError, "pipeline");
    }

    [TestMethod]
    public void SwitchingBetweenH264AndH265MovesTheDefaultPort()
    {
        var (page, _, _, _) = Create();
        var item = page.Selected!;
        Assert.AreEqual(5600m, item.Port);
        item.Type = VideoSettingsViewModel.Types.First(t => t.Type == VideoSourceType.UdpH265);
        Assert.AreEqual(5601m, item.Port);
        item.Type = VideoSettingsViewModel.Types.First(t => t.Type == VideoSourceType.UdpH264);
        Assert.AreEqual(5600m, item.Port);
        item.Port = 6000;
        item.Type = VideoSettingsViewModel.Types.First(t => t.Type == VideoSourceType.UdpH265);
        Assert.AreEqual(6000m, item.Port, "a chosen port stays");
    }

    [TestMethod]
    public void SaveAndPlayAppliesPersistsAndStartsTheSelectedSource()
    {
        var (page, owner, stream, store) = Create();
        page.AddCommand.Execute(null);
        var item = page.Selected!;
        item.Name = "Siyi";
        item.Type = VideoSettingsViewModel.Types.First(t => t.Type == VideoSourceType.Rtsp);
        item.Url = "rtsp://192.168.144.25:8554/main.264";
        page.AutoStart = true;
        page.Aspect = VideoSettingsViewModel.Aspects.First(a => a.Value == VideoAspect.Fit);

        var closed = (bool?)null;
        page.CloseRequested += (_, r) => closed = r;
        page.SaveAndPlayCommand.Execute(null);

        Assert.IsTrue(closed);
        Assert.AreEqual("Siyi", owner.Selected!.Name);
        Assert.IsTrue(owner.AutoStart);
        Assert.AreEqual(VideoAspect.Fit, owner.Aspect);
        StringAssert.StartsWith(stream.Started.Single(), "rtspsrc location=\"rtsp://192.168.144.25:8554/main.264\"");

        var saved = VideoSettings.Load(store);
        Assert.AreEqual(item.Id, saved.SelectedId);
        Assert.AreEqual("rtsp://192.168.144.25:8554/main.264", saved.Selected!.Url);
    }

    [TestMethod]
    public void AnInvalidSourceIsShownBeforeTheDialogCloses()
    {
        var (page, _, stream, _) = Create();
        page.AddCommand.Execute(null);
        var bad = page.Selected!;
        bad.Type = VideoSettingsViewModel.Types.First(t => t.Type == VideoSourceType.MjpegHttp);
        page.Selected = page.Sources[0];

        var closed = (bool?)null;
        page.CloseRequested += (_, r) => closed = r;
        page.SaveCommand.Execute(null);
        Assert.IsNull(closed, "kept open");
        Assert.AreSame(bad, page.Selected);

        // with it selected (seen), it may be saved to finish later, but not played
        page.SaveAndPlayCommand.Execute(null);
        Assert.IsNull(closed);
        Assert.IsEmpty(stream.Started);
        page.SaveCommand.Execute(null);
        Assert.IsTrue(closed);
    }

    [TestMethod]
    public void DuplicateAndDelete()
    {
        var (page, _, _, _) = Create();
        page.Selected = page.Sources[1];
        page.DuplicateCommand.Execute(null);
        Assert.HasCount(4, page.Sources);
        Assert.AreSame(page.Sources[2], page.Selected);
        Assert.AreEqual("UDP H.265 (5601) copy", page.Selected!.Name);
        Assert.AreNotEqual(page.Sources[1].Id, page.Selected.Id);
        Assert.AreEqual(5601m, page.Selected.Port);

        page.DeleteCommand.Execute(null);
        Assert.HasCount(3, page.Sources);
        Assert.AreSame(page.Sources[2], page.Selected);

        page.DeleteCommand.Execute(null);
        page.DeleteCommand.Execute(null);
        page.DeleteCommand.Execute(null);
        Assert.IsEmpty(page.Sources);
        Assert.IsNull(page.Selected);
        Assert.IsFalse(page.DeleteCommand.CanExecute(null));
        Assert.IsFalse(page.SaveAndPlayCommand.CanExecute(null));
    }
}

[TestClass]
public sealed class FpvCameraTests
{
    private static HudVideoViewModel Create(out MemoryVideoStore store)
    {
        store = new MemoryVideoStore();
        return new HudVideoViewModel(new VideoTestUi().CreateServices(), store, new FakeVideoStream());
    }

    [TestMethod]
    public void DefaultsMatchTheHudAndChangesApplyAndPersist()
    {
        var vm = Create(out var store);
        Assert.IsNull(vm.FpvFov, "default: the HUD's own field of view");
        Assert.AreEqual(0, vm.FpvTilt);

        var dialog = new FpvCameraViewModel(vm);
        dialog.CustomFov = true;
        Assert.AreEqual(HudVideoViewModel.DefaultFov, vm.FpvFov!.Value, "applied as soon as it is switched on");
        dialog.Fov = 135;
        dialog.Tilt = 30;
        Assert.AreEqual(135, vm.FpvFov!.Value);
        Assert.AreEqual(30, vm.FpvTilt);
        Assert.AreEqual("135", store.Get(HudVideoViewModel.FovKey));
        Assert.AreEqual("30", store.Get(HudVideoViewModel.TiltKey));

        dialog.CustomFov = false;
        Assert.IsNull(vm.FpvFov);
        Assert.IsNull(store.Get(HudVideoViewModel.FovKey));
    }

    [TestMethod]
    public void CancelPutsBackWhatWasThereAndResetGoesToTheDefaults()
    {
        var vm = Create(out _);
        vm.FpvFov = 90;
        vm.FpvTilt = 10;
        var dialog = new FpvCameraViewModel(vm);
        var closed = (bool?)null;
        dialog.CloseRequested += (_, ok) => closed = ok;
        dialog.Fov = 150;
        dialog.Tilt = 45;
        dialog.CancelCommand.Execute(null);
        Assert.IsFalse(closed);
        Assert.AreEqual(90, vm.FpvFov!.Value);
        Assert.AreEqual(10, vm.FpvTilt);

        var again = new FpvCameraViewModel(vm);
        again.ResetCommand.Execute(null);
        Assert.IsNull(vm.FpvFov);
        Assert.AreEqual(0, vm.FpvTilt);
    }

    [TestMethod]
    public void OutOfRangeValuesAreClamped()
    {
        var vm = Create(out _);
        vm.FpvFov = 400;
        vm.FpvTilt = -90;
        Assert.AreEqual(160, vm.FpvFov!.Value);
        Assert.AreEqual(-30, vm.FpvTilt);
    }
}
