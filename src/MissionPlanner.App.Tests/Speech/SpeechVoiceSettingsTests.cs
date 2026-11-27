using MissionPlanner.App.Core.Speech;
using MissionPlanner.App.Core.ViewModels.FlightData;
using MissionPlanner.App.Tests.Logs;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Tests.Speech;

[TestClass]
public class SystemVoiceTests
{
    private static string? Has(string name, params string[] present) => present.Contains(name) ? "/usr/bin/" + name : null;

    [TestMethod]
    public void TheSystemProgramGetsTheVoiceRateAndVolume()
    {
        var o = new SystemVoiceOptions("English (America)", 1.5, 40);

        var spd = SpeechBackend.For("hello", SpeechBackend.Os.Linux, n => Has(n, "spd-say"), o)!;
        CollectionAssert.AreEqual(new[] { "--wait", "-y", "English (America)", "-r", "50", "-i", "-60", "--", "hello" }, spd.Arguments.ToArray());

        var ng = SpeechBackend.For("hello", SpeechBackend.Os.Linux, n => Has(n, "espeak-ng"), o)!;
        CollectionAssert.AreEqual(new[] { "-v", "English (America)", "-s", "263", "-a", "40", "--", "hello" }, ng.Arguments.ToArray());

        var say = SpeechBackend.For("hello", SpeechBackend.Os.MacOs, n => Has(n, "say"), o with { Voice = "Samantha" })!;
        CollectionAssert.AreEqual(new[] { "-v", "Samantha", "-r", "263", "--", "[[volm 0.40]] hello" }, say.Arguments.ToArray());

        var win = SpeechBackend.For("hello", SpeechBackend.Os.Windows, n => n == "powershell.exe" ? @"C:\ps.exe" : null, o with { Voice = "Microsoft Zira\nDesktop" })!;
        Assert.AreEqual("Microsoft Zira Desktop\nhello", win.StandardInput, "voice on the first line, the text after: nothing goes through the command line");
        var script = win.Arguments[^1];
        StringAssert.Contains(script, "$s.Rate = 4;");
        StringAssert.Contains(script, "$s.Volume = 40;");
        StringAssert.Contains(script, "SelectVoice");

        Assert.AreEqual("--tts", SpeechBackend.For("x", SpeechBackend.Os.Linux, n => Has(n, "festival"), o)!.Arguments[0], "festival takes none");
        Assert.IsFalse(SpeechBackend.HasOptions("festival"));
        Assert.IsTrue(SpeechBackend.HasOptions("spd-say"));
    }

    [TestMethod]
    public void DefaultOptionsKeepTheOriginalCommands()
    {
        var spd = SpeechBackend.For("hello", SpeechBackend.Os.Linux, n => Has(n, "spd-say"), SystemVoiceOptions.Default)!;
        CollectionAssert.AreEqual(new[] { "--wait", "--", "hello" }, spd.Arguments.ToArray());
        var win = SpeechBackend.For("hi", SpeechBackend.Os.Windows, n => n == "powershell.exe" ? @"C:\ps.exe" : null, SystemVoiceOptions.Default)!;
        Assert.AreEqual("hi", win.StandardInput);
    }

    [TestMethod]
    public void InstalledVoicesAreReadFromEachProgram()
    {
        var say = SystemVoices.ParseSay("Albert              en_US    # Hello! My name is Albert.\nEddy (English (US)) en_US    # Hello! My name is Eddy.\n");
        CollectionAssert.AreEqual(new[] { "Albert", "Eddy (English (US))" }, say.Select(v => v.Id).ToArray());

        var spd = SystemVoices.ParseSpdSay("     NAME                 LANGUAGE        VARIANT\n English (America)            en-US           none\n          Afrikaans            af            none\n");
        CollectionAssert.AreEqual(new[] { "English (America)", "Afrikaans" }, spd.Select(v => v.Id).ToArray());

        var espeak = SystemVoices.ParseEspeak("Pty Language       Age/Gender VoiceName          File                 Other Languages\n 5  en-us           --/M      English_(America)  gmw/en-US            (en 3)\n");
        Assert.AreEqual("en-us", espeak.Single().Id);
        Assert.AreEqual("English (America) (en-us)", espeak.Single().Label);

        var win = SystemVoices.ParseWindows("Microsoft Zira Desktop|en-US|Female\r\nMicrosoft David Desktop|en-US|Male\r\n");
        Assert.AreEqual("Microsoft Zira Desktop", win[0].Id);
        Assert.AreEqual("Microsoft David Desktop (en-US, male)", win[1].Label);
    }
}

[TestClass]
public class SpeechVoiceViewModelTests
{
    [TestCleanup]
    public void Cleanup()
    {
        foreach (var k in SpeechVoiceSettings.Keys)
            Settings.Instance.Remove(k);
    }

    private static (SpeechVoiceViewModel Vm, SpeechEngine Engine, RecordingPlayer Player, List<string> Ran) Create(VoiceStore? store = null)
    {
        store ??= new VoiceStore(SpeechTestSupport.NewFolder("voices"));
        var player = new RecordingPlayer();
        var neural = new NeuralSpeech(store, (_, _) => new FakeSynthesizer(), () => player, () => null);
        var ran = new List<string>();
        var engine = new SpeechEngine((text, _) => new SpeechCommand("say", [text], null), (cmd, _) =>
        {
            lock (ran)
                ran.Add(cmd.Arguments[0]);
            return true;
        }, neural, SpeechVoiceSettings.Load);
        return (new SpeechVoiceViewModel(engine, new FakeUiServices()), engine, player, ran);
    }

    [TestMethod]
    public void WithNothingDownloadedTheRecommendedVoiceIsOfferedAndTheSystemVoiceSpeaks()
    {
        var (vm, engine, _, ran) = Create();
        using var _ = engine;

        Assert.IsTrue(vm.UseNeural);
        Assert.HasCount(VoiceCatalog.All.Count, vm.Voices);
        Assert.AreEqual("kokoro-v1", vm.SelectedVoice!.Info.Id);
        Assert.IsTrue(vm.SelectedVoice.IsSelected);
        Assert.AreEqual("Heart", vm.SelectedSpeaker!.Speaker.Name);
        Assert.IsTrue(vm.Voices.All(v => v.CanDownload && !v.CanDelete));
        StringAssert.Contains(vm.Voices[0].License, "Apache-2.0");
        StringAssert.Contains(vm.Voices[0].Details, "MB download");
        StringAssert.Contains(vm.StatusText, "No neural voice downloaded");
        Assert.IsTrue(vm.StatusIsWarning);

        vm.TestVoiceCommand.Execute(null);
        NeuralSpeechTests.WaitIdle(engine);
        CollectionAssert.AreEqual(new[] { SpeechVoiceViewModel.SampleText }, ran, "the test sentence, with the system voice until a voice is downloaded");
    }

    [TestMethod]
    public void ChoosingAVoiceListsItsSpeakersAndSaveKeepsTheChoices()
    {
        var (vm, engine, _, _) = Create();
        using var _ = engine;

        vm.SelectedVoice = vm.Voices.First(v => v.Info.Kind == VoiceModelKind.Kitten);
        Assert.IsFalse(vm.Voices[0].IsSelected);
        Assert.HasCount(8, vm.Speakers);
        Assert.AreEqual("Bella", vm.SelectedSpeaker!.Speaker.Name, "the voice's default speaker");
        vm.SelectedSpeaker = vm.Speakers.First(s => s.Speaker.Name == "Leo");
        vm.Speed = 1.3;
        vm.Volume = 70;
        vm.UseSystem = true;
        Assert.IsFalse(vm.UseNeural);
        StringAssert.Contains(vm.StatusText, "system voice");
        vm.Save();

        var saved = SpeechVoiceSettings.Load();
        Assert.AreEqual(SpeechEngineKind.System, saved.Engine);
        Assert.AreEqual("kitten-nano-v0.8", saved.VoiceId);
        Assert.AreEqual(6, saved.Speaker);
        Assert.AreEqual(1.3, saved.Speed, 1e-9);
        Assert.AreEqual(70, saved.Volume);

        var (again, engine2, _, _) = Create();
        using var __ = engine2;
        Assert.AreEqual("kitten-nano-v0.8", again.SelectedVoice!.Info.Id, "the saved voice is selected again");
        Assert.AreEqual("Leo", again.SelectedSpeaker!.Speaker.Name);
        Assert.IsTrue(again.UseSystem);
    }

    [TestMethod]
    public async Task DownloadingAVoiceFromTheListSelectsItAndTheNeuralVoiceSpeaks()
    {
        var kokoro = VoiceCatalog.Find("kokoro-v1")!;
        var work = SpeechTestSupport.NewFolder("voices-src");
        var archive = SpeechTestSupport.BuildArchive(work, kokoro, withHostileEntries: false);
        var root = SpeechTestSupport.NewFolder("voices");
        // installed through the store the page uses: the page sees it on its next refresh
        Assert.IsTrue(await new VoiceStore(root, SpeechTestSupport.LocalDownload(archive)).InstallAsync(SpeechTestSupport.Sized(kokoro, archive)));
        foreach (var lexicon in new[] { "lexicon-us-en.txt", "lexicon-gb-en.txt" })
            File.WriteAllLines(Path.Combine(root, kokoro.Folder, lexicon), ["mode m ˈ O d", "armed ˈ ɑ ɹ m d"]);

        var (vm, engine, player, ran) = Create(new VoiceStore(root));
        using var _ = engine;
        vm.Refresh();
        Assert.IsTrue(vm.Voices[0].IsInstalled);
        Assert.IsTrue(vm.Voices[0].CanDelete);
        StringAssert.Contains(vm.Voices[0].StateText, "Downloaded");
        StringAssert.Contains(vm.StatusText, "Kokoro, Heart");
        Assert.IsFalse(vm.StatusIsWarning);

        vm.TestVoiceCommand.Execute(null);
        NeuralSpeechTests.WaitIdle(engine);
        Assert.IsNotEmpty(player.Played, "the neural voice spoke");
        Assert.IsEmpty(ran);

        await vm.Voices[0].DeleteCommand.ExecuteAsync(null);
        Assert.IsFalse(vm.Voices[0].IsInstalled);
        Assert.IsFalse(Directory.Exists(Path.Combine(root, kokoro.Folder)));
        StringAssert.Contains(vm.StatusText, "No neural voice downloaded");
    }
}
