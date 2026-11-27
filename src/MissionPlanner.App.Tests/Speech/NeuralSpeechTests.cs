using MissionPlanner.App.Core.Speech;
using MissionPlanner.App.Core.ViewModels.FlightData;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Tests.Speech;

/// <summary>The neural engine with a fake model and a recording player: never a real sound.</summary>
[TestClass]
public class NeuralSpeechTests
{
    private static VoiceInfo Kokoro => VoiceCatalog.Find("kokoro-v1")!;

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var k in SpeechVoiceSettings.Keys)
            Settings.Instance.Remove(k);
    }

    /// <summary>A store with Kokoro installed from a local archive whose lexicons know a few words.</summary>
    private static async Task<VoiceStore> InstalledStore()
    {
        var work = SpeechTestSupport.NewFolder("voices-src");
        var archive = SpeechTestSupport.BuildArchive(work, Kokoro, withHostileEntries: false);
        var store = new VoiceStore(SpeechTestSupport.NewFolder("voices"), SpeechTestSupport.LocalDownload(archive));
        // the real catalogue entry, so the engine finds it; its size is that of the test archive
        Assert.IsTrue(await new VoiceStore(store.Root, SpeechTestSupport.LocalDownload(archive)).InstallAsync(SpeechTestSupport.Sized(Kokoro, archive)));
        foreach (var lexicon in new[] { "lexicon-us-en.txt", "lexicon-gb-en.txt" })
        {
            File.WriteAllLines(Path.Combine(store.VoiceDirectory(Kokoro), lexicon),
            [
                "mode m ˈ O d", "changed ʧ ˈ A n ʤ d", "to t u", "loiter l ˈ Y T ɚ", "battery b ˈ æ T ə ɹ i", "fifty f ˈ ɪ f t i",
                "percent p ə ɹ s ˈ ɛ n t", "armed ˈ ɑ ɹ m d",
            ]);
        }

        return store;
    }

    private static SpeechVoiceSettings KokoroHeart(int volume = 100) => new(SpeechEngineKind.Neural, "kokoro-v1", 3, 1.0, volume);

    [TestMethod]
    public async Task SpeaksSentenceBySentenceAndCachesRepeatedPhrases()
    {
        var store = await InstalledStore();
        var synth = new FakeSynthesizer();
        var player = new RecordingPlayer();
        using var neural = new NeuralSpeech(store, (_, _) => synth, () => player, () => null);

        Assert.IsTrue(neural.Speak("Mode changed to Loiter. Battery 50 percent.", KokoroHeart(), CancellationToken.None));
        Assert.IsNull(neural.Problem);
        Assert.AreEqual("Kokoro, Heart", neural.Active);
        CollectionAssert.AreEqual(new[] { "mˈOd ʧˈAnʤd tu lˈYTɚ.", "bˈæTəɹi fˈɪfti pəɹsˈɛnt." }, synth.Generated);
        Assert.HasCount(2, player.Played, "one sound per sentence, in order");

        // "Mode changed to Loiter" again: from the cache, no new synthesis
        Assert.IsTrue(neural.Speak("Mode changed to Loiter.", KokoroHeart(), CancellationToken.None));
        Assert.HasCount(2, synth.Generated);
        Assert.HasCount(3, player.Played);
        Assert.AreEqual(2, neural.Cache.Count);

        // another speed is another sound
        neural.Speak("Mode changed to Loiter.", KokoroHeart() with { Speed = 1.5 }, CancellationToken.None);
        Assert.HasCount(3, synth.Generated);

        // a message in another alphabet goes to the system voice, which is no fault of the neural one
        Assert.IsFalse(neural.Speak("Режим изменён", KokoroHeart(), CancellationToken.None));
        Assert.IsNull(neural.Problem);
        Assert.HasCount(3, synth.Generated);
    }

    [TestMethod]
    public async Task VolumeScalesTheSamplesAndSentencesArePaused()
    {
        var store = await InstalledStore();
        var player = new RecordingPlayer();
        using var neural = new NeuralSpeech(store, (_, _) => new FakeSynthesizer(), () => player, () => null);

        neural.Speak("Armed. Armed.", KokoroHeart(volume: 50), CancellationToken.None);

        var first = player.Played[0];
        var last = player.Played[1];
        Assert.AreEqual((short)MathF.Round(0.89f * 0.5f * 32767f), BitConverter.ToInt16(first, 44), "brought to -1 dBFS, then half volume");
        Assert.AreEqual(first.Length - last.Length, (int)(16000 * NeuralSpeech.SentencePause.TotalSeconds) * 2, "a pause after all but the last sentence");
        Assert.AreEqual(0, BitConverter.ToInt16(first, first.Length - 2), "the pause is silent");
    }

    [TestMethod]
    public void WavIsMono16BitPcmAndClips()
    {
        var wav = Wav.Pcm16([0f, 1f, -1f, 2f, -0.5f], 24000, 1.0, silence: 3);
        Assert.AreEqual("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.AreEqual("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
        Assert.AreEqual(1, BitConverter.ToInt16(wav, 20), "PCM");
        Assert.AreEqual(1, BitConverter.ToInt16(wav, 22), "mono");
        Assert.AreEqual(24000, BitConverter.ToInt32(wav, 24));
        Assert.AreEqual(48000, BitConverter.ToInt32(wav, 28), "byte rate");
        Assert.AreEqual(16, BitConverter.ToInt16(wav, 34));
        Assert.AreEqual(16, BitConverter.ToInt32(wav, 40), "8 samples of 2 bytes");
        Assert.AreEqual(wav.Length - 8, BitConverter.ToInt32(wav, 4));
        short S(int i) => BitConverter.ToInt16(wav, 44 + i * 2);
        Assert.AreEqual(0, S(0));
        Assert.AreEqual(32767, S(1));
        Assert.AreEqual(-32767, S(2));
        Assert.AreEqual(32767, S(3), "clipped");
        Assert.AreEqual(-16384, S(4));
        Assert.AreEqual(0, S(7));
        Assert.AreEqual(0, BitConverter.ToInt16(Wav.Pcm16([0.8f], 8000, 0), 44), "volume 0 is silence");
    }

    [TestMethod]
    public void EverySentenceIsBroughtToTheSameLoudness()
    {
        var n = Wav.Normalize([0.4f, -0.2f]);
        Assert.AreEqual(0.89f, n[0], 1e-6f, "peak at -1 dBFS");
        Assert.AreEqual(-0.445f, n[1], 1e-6f, "the rest in proportion");
        Assert.AreEqual(0.04f, Wav.Normalize([0.01f])[0], 1e-7f, "at most four times louder");
        CollectionAssert.AreEqual(new[] { 0f, 0f }, Wav.Normalize([0f, 0f]));
    }

    [TestMethod]
    public void EachPlatformHasItsPlayerAndABrokenOneGivesWayToTheNext()
    {
        string? Has(string name, params string[] present) => present.Contains(name) ? "/usr/bin/" + name : null;

        var all = (FallbackAudioPlayer)AudioPlayers.Detect(SpeechBackend.Os.Linux, n => Has(n, "pw-play", "paplay", "aplay"))!;
        CollectionAssert.AreEqual(new[] { "pw-play", "paplay", "aplay" }, all.Players.Select(p => p.Name).ToArray(), "PipeWire, PulseAudio, ALSA");
        var alsa = (ProcessAudioPlayer)AudioPlayers.Detect(SpeechBackend.Os.Linux, n => Has(n, "aplay"))!;
        CollectionAssert.AreEqual(new[] { "-q" }, alsa.Arguments.ToArray());
        Assert.IsNull(AudioPlayers.Detect(SpeechBackend.Os.Linux, _ => null));
        Assert.AreEqual("afplay", AudioPlayers.Detect(SpeechBackend.Os.MacOs, n => Has(n, "afplay"))!.Name);

        // pw-play installed but no PipeWire running: the same sound goes to the next player, which is kept
        var broken = new ScriptedPlayer("pw-play", fails: true);
        var working = new ScriptedPlayer("paplay", fails: false);
        var chain = new FallbackAudioPlayer([broken, working]);
        var p = chain.Play([1, 2, 3], TimeSpan.FromSeconds(1));
        p.Wait(CancellationToken.None);
        Assert.IsFalse(p.Failed);
        Assert.AreEqual(1, broken.Count);
        Assert.AreEqual(1, working.Count);
        Assert.AreEqual("paplay", chain.Name);
        chain.Play([4], TimeSpan.FromSeconds(1)).Wait(CancellationToken.None);
        Assert.AreEqual(1, broken.Count, "not tried again");
        Assert.AreEqual(2, working.Count);
    }

    private sealed class ScriptedPlayer(string name, bool fails) : IAudioPlayer
    {
        public int Count;

        public string Name => name;

        public IPlayback Play(byte[] wav, TimeSpan duration)
        {
            Count++;
            return new Result(fails);
        }

        private sealed class Result(bool failed) : IPlayback
        {
            public bool Failed => failed;

            public void Wait(CancellationToken cancel)
            {
            }

            public void Stop()
            {
            }
        }
    }

    [TestMethod]
    public void TheCacheDropsTheLeastRecentlyUsed()
    {
        var cache = new AudioCache(maxSamples: 30);
        cache.Add("a", new float[10]);
        cache.Add("b", new float[10]);
        cache.Add("c", new float[10]);
        Assert.IsTrue(cache.TryGet("a", out _), "touch a: b is now the oldest");
        cache.Add("d", new float[10]);
        Assert.IsFalse(cache.TryGet("b", out _));
        Assert.IsTrue(cache.TryGet("a", out _));
        Assert.IsTrue(cache.TryGet("d", out _));
        Assert.AreEqual(30, cache.Samples);
        cache.Add("huge", new float[31]);
        Assert.IsFalse(cache.TryGet("huge", out _), "longer than the whole cache: not kept");
        Assert.AreNotEqual(AudioCache.Key("v", 1, 1.0, "x"), AudioCache.Key("v", 2, 1.0, "x"));
    }

    [TestMethod]
    public async Task WithoutTheNativeLibraryTheSystemVoiceSpeaks()
    {
        var store = await InstalledStore();
        var neural = new NeuralSpeech(store, (_, _) => throw new DllNotFoundException("sherpa-onnx-c-api"), () => new RecordingPlayer(), () => null);
        Assert.IsFalse(neural.Speak("Armed.", KokoroHeart(), CancellationToken.None));
        StringAssert.Contains(neural.Problem, "not available");
        StringAssert.Contains(neural.Check(KokoroHeart()), "not available", "remembered: no retry for every message");

        var probe = new NeuralSpeech(store, (_, _) => new FakeSynthesizer(), () => new RecordingPlayer(), () => "The neural speech engine is not included for this platform.");
        Assert.IsFalse(probe.Speak("Armed.", KokoroHeart(), CancellationToken.None));
        StringAssert.Contains(probe.Problem, "not included");

        // the queue: neural first, the program when it cannot
        var ran = new List<string>();
        using var engine = new SpeechEngine((text, _) => new SpeechCommand("say", [text], null), (cmd, _) =>
        {
            lock (ran)
                ran.Add(cmd.Arguments[0]);
            return true;
        }, probe, () => KokoroHeart());
        engine.SpeakNow("Mode changed to Loiter");
        WaitIdle(engine);
        CollectionAssert.AreEqual(new[] { "Mode changed to Loiter" }, ran);
        Assert.IsFalse(engine.LastSpokenByNeural);
    }

    [TestMethod]
    public async Task ADamagedOrMissingVoiceFallsBackAndSaysWhy()
    {
        var empty = new VoiceStore(SpeechTestSupport.NewFolder("voices"));
        var neural = new NeuralSpeech(empty, (_, _) => new FakeSynthesizer(), () => new RecordingPlayer(), () => null);
        Assert.IsFalse(neural.Speak("Armed.", new SpeechVoiceSettings(), CancellationToken.None));
        StringAssert.Contains(neural.Problem, "No neural voice downloaded");

        var store = await InstalledStore();
        var damaged = new NeuralSpeech(store, (_, _) => throw new InvalidDataException("model.onnx is missing from the voice folder"), () => new RecordingPlayer(), () => null);
        Assert.IsFalse(damaged.Speak("Armed.", KokoroHeart(), CancellationToken.None));
        StringAssert.Contains(damaged.Problem, "model.onnx");
        damaged.Reset();
        Assert.IsNull(damaged.Check(KokoroHeart()), "a reset (settings saved, voice reinstalled) tries again");

        var silent = new NeuralSpeech(store, (_, _) => new FakeSynthesizer(), () => null, () => null);
        Assert.IsFalse(silent.Speak("Armed.", KokoroHeart(), CancellationToken.None));
        StringAssert.Contains(silent.Problem, "audio player");

        // the real loader checks the files before sherpa-onnx sees them
        var dir = SpeechTestSupport.NewFolder("broken-voice");
        Assert.ThrowsExactly<InvalidDataException>(() => SherpaSynthesizer.Load(Kokoro, dir));
    }

    [TestMethod]
    public void TheEngineShipsWithTheTestsAndLoads()
    {
        // the tests import the app's sherpa-onnx.targets, so the native library of this platform is next to them
        Assert.IsTrue(SherpaSynthesizer.NativeAvailable(out var problem), problem);
        Assert.IsTrue(File.Exists(Path.Combine(AppContext.BaseDirectory, "Tools", "sherpa-onnx", "LICENSE.sherpa-onnx.txt")));
        Assert.IsTrue(File.Exists(Path.Combine(AppContext.BaseDirectory, "Tools", "sherpa-onnx", "LICENSE.onnxruntime.txt")));
    }

    [TestMethod]
    public async Task TheQueueKeepsUtterancesInOrderAndCancelStopsPlayback()
    {
        var store = await InstalledStore();
        var synth = new FakeSynthesizer();
        var player = new RecordingPlayer { Hold = new ManualResetEventSlim(false) };
        var neural = new NeuralSpeech(store, (_, _) => synth, () => player, () => null);
        using var engine = new SpeechEngine((_, _) => null, (_, _) => true, neural, () => KokoroHeart()) { speechEnable = true };

        engine.SpeakAsync("Armed.");
        engine.SpeakAsync("Mode changed to Loiter.");
        engine.SpeakAsync("Battery 50 percent.");
        player.Hold.Set();
        WaitIdle(engine);
        CollectionAssert.AreEqual(new[] { "ˈɑɹmd.", "mˈOd ʧˈAnʤd tu lˈYTɚ.", "bˈæTəɹi fˈɪfti pəɹsˈɛnt." }, synth.Generated);
        Assert.IsTrue(engine.LastSpokenByNeural);

        // a long announcement is cut off when speech is switched off
        player.Hold.Reset();
        engine.SpeakAsync("Mode changed to Loiter. Battery 50 percent.");
        var until = DateTime.UtcNow.AddSeconds(5);
        while (player.Played.Count < 4 && DateTime.UtcNow < until)
            Thread.Sleep(10);
        engine.SpeakAsyncCancelAll();
        WaitIdle(engine);
        Assert.HasCount(4, player.Played, "the second sentence never started");
        Assert.IsGreaterThanOrEqualTo(1, player.Stopped);
    }

    [TestMethod]
    public void ChoicesAreSavedUnderNewKeysAndTheVoiceResolvesToAnInstalledOne()
    {
        var s = new SpeechVoiceSettings(SpeechEngineKind.System, "kitten-nano-v0.8", 5, 1.25, 80, "Samantha", 0.8, 60);
        s.Save();
        Assert.AreEqual("system", Settings.Instance[SpeechVoiceSettings.EngineKey]);
        Assert.AreEqual("1.25", Settings.Instance[SpeechVoiceSettings.RateKey]);
        Assert.AreEqual(s, SpeechVoiceSettings.Load());
        Assert.IsTrue(SpeechVoiceSettings.Keys.All(k => k.StartsWith("next_speech_", StringComparison.Ordinal)));

        Settings.Instance[SpeechVoiceSettings.RateKey] = "9";
        Assert.AreEqual(SpeechVoiceSettings.MaxSpeed, SpeechVoiceSettings.Load().Speed, "clamped");

        foreach (var k in SpeechVoiceSettings.Keys)
            Settings.Instance.Remove(k);
        var fresh = SpeechVoiceSettings.Load();
        Assert.AreEqual(SpeechEngineKind.Neural, fresh.Engine, "neural by default: it speaks once a voice is downloaded");
        Assert.IsNull(fresh.ResolveVoice(_ => false), "nothing downloaded: no neural voice");
        Assert.AreEqual("kitten-nano-v0.8", fresh.ResolveVoice(v => v.Kind == VoiceModelKind.Kitten)!.Id, "the one downloaded");
        Assert.AreEqual("kokoro-v1", fresh.ResolveVoice(_ => true)!.Id, "the recommended one first");
        Assert.AreEqual("kokoro-v1", s.ResolveVoice(v => v.Id == "kokoro-v1")!.Id, "the chosen one was deleted: another installed one speaks");
    }

    /// <summary>
    /// Opt in with MP_TEST_VOICES=&lt;a voices folder with downloaded voices&gt; (as the app's data folder has):
    /// each installed voice says the test sentence through the real engine; the audio is checked, never played.
    /// </summary>
    [TestMethod]
    [TestCategory("Voice")]
    public void DownloadedVoicesSpeakThroughTheRealEngine()
    {
        var dir = Environment.GetEnvironmentVariable("MP_TEST_VOICES");
        if (string.IsNullOrWhiteSpace(dir))
            Assert.Inconclusive("set MP_TEST_VOICES to a folder of downloaded voices to run the real engine");
        var store = new VoiceStore(dir);
        var voices = store.Installed.ToList();
        if (voices.Count == 0)
            Assert.Inconclusive("no voice is installed in " + dir);
        foreach (var voice in voices)
        {
            var player = new RecordingPlayer();
            using var neural = new NeuralSpeech(store, player: () => player);
            Assert.IsTrue(neural.Speak(SpeechVoiceViewModel.SampleText, new SpeechVoiceSettings(SpeechEngineKind.Neural, voice.Id), CancellationToken.None),
                voice.Id + ": " + neural.Problem);
            Assert.HasCount(2, player.Played, voice.Id + ": two sentences");
            foreach (var wav in player.Played)
            {
                var rate = BitConverter.ToInt32(wav, 24);
                var seconds = (wav.Length - 44) / 2.0 / rate;
                Assert.IsTrue(seconds is > 0.5 and < 8, $"{voice.Id}: {seconds:0.00} s");
                var peak = 0;
                for (var i = 44; i + 1 < wav.Length; i += 2)
                    peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(wav, i)));
                Assert.IsGreaterThan(10000, peak, voice.Id + ": not silent");
            }
        }
    }

    internal static void WaitIdle(SpeechEngine engine)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        Thread.Sleep(50);
        while (!engine.IsReady && DateTime.UtcNow < until)
            Thread.Sleep(20);
        Assert.IsTrue(engine.IsReady, "the speech queue emptied");
    }
}
