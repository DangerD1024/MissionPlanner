using System.Reflection;
using System.Runtime.InteropServices;
using log4net;
using MissionPlanner.App.Core.Localization;
using SherpaOnnx;

namespace MissionPlanner.App.Core.Speech;

/// <summary>A loaded neural voice model.</summary>
public interface ISpeechSynthesizer : IDisposable
{
    int SampleRate { get; }

    /// <summary>The audio of one sentence given as phonemes (<see cref="Phonemizer"/>).</summary>
    float[] Generate(string phonemes, int speaker, double speed);
}

/// <summary>sherpa-onnx's offline TTS (in-process, CPU), for Piper, Kokoro and Kitten models.</summary>
public sealed class SherpaSynthesizer : ISpeechSynthesizer
{
    private static readonly FieldInfo? HandleField = typeof(OfflineTts).GetField("_handle", BindingFlags.Instance | BindingFlags.NonPublic);
    private readonly object _gate = new();
    private OfflineTts? _tts;

    private SherpaSynthesizer(OfflineTts tts)
    {
        _tts = tts;
        SampleRate = tts.SampleRate;
    }

    public int SampleRate { get; }

    /// <summary>Half the cores, 1..4: fast enough for speech without starving the rest of the app.</summary>
    public static int Threads => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    /// <summary>True when the native engine (sherpa-onnx-c-api with onnxruntime) can be loaded on this computer.</summary>
    public static bool NativeAvailable(out string? problem)
    {
        try
        {
            if (NativeLibrary.TryLoad("sherpa-onnx-c-api", typeof(OfflineTts).Assembly, null, out _))
            {
                problem = null;
                return true;
            }

            problem = L.Tr("The neural speech engine is not included for this platform.");
        }
        catch (Exception ex)
        {
            problem = L.Tr("The neural speech engine could not be loaded:") + " " + ex.Message;
        }

        return false;
    }

    /// <summary>Loads a voice's model. Every file is checked first: sherpa-onnx gives up on a missing one without saying why.</summary>
    /// <exception cref="InvalidDataException">A file is missing or the model does not load.</exception>
    public static SherpaSynthesizer Load(VoiceInfo voice, string directory)
    {
        foreach (var file in VoiceStore.RequiredFiles(voice))
        {
            var path = Path.Combine(directory, file);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                throw new InvalidDataException(L.Format("{0} is missing from the voice folder: delete the voice and download it again.", file));
        }

        // sherpa-onnx opens files with ANSI paths on Windows: hand a non-ASCII folder over by its short name
        directory = NativePaths.ForNative(directory)
                    ?? throw new InvalidDataException(L.Format(
                        "The speech engine cannot open files in {0} (the folder name has letters it cannot read). Delete the voice and download it again: it is then kept in a folder it can open.",
                        directory));

        var config = new OfflineTtsConfig();
        config.Model.NumThreads = Threads;
        config.Model.Debug = 0;
        config.Model.Provider = "cpu";
        var model = Path.Combine(directory, voice.ModelFile);
        var tokens = Path.Combine(directory, "tokens.txt");
        switch (voice.Kind)
        {
            case VoiceModelKind.Kokoro:
                config.Model.Kokoro.Model = model;
                config.Model.Kokoro.Voices = Path.Combine(directory, "voices.bin");
                config.Model.Kokoro.Tokens = tokens;
                break;
            case VoiceModelKind.Kitten:
                config.Model.Kitten.Model = model;
                config.Model.Kitten.Voices = Path.Combine(directory, "voices.bin");
                config.Model.Kitten.Tokens = tokens;
                break;
            default:
                config.Model.Vits.Model = model;
                config.Model.Vits.Tokens = tokens;
                break;
        }

        var tts = new OfflineTts(config);
        // the wrapper does not check: a model that failed to load leaves a null handle that would crash later
        if (HandleField?.GetValue(tts) is HandleRef { Handle: var h } && h == IntPtr.Zero)
            throw new InvalidDataException(L.Tr("The voice model could not be loaded: delete the voice and download it again."));
        return new SherpaSynthesizer(tts);
    }

    public float[] Generate(string phonemes, int speaker, double speed)
    {
        if (phonemes.Length == 0)
            return [];
        var codepoints = new List<int>(phonemes.Length);
        foreach (var rune in phonemes.EnumerateRunes())
            codepoints.Add(rune.Value);
        var config = new OfflineTtsGenerationConfig
        {
            Speed = (float)speed,
            Sid = speaker,
            SilenceScale = 0.2f,
            PhonemeCodepoints = [codepoints.ToArray()],
        };
        lock (_gate)
        {
            var tts = _tts ?? throw new ObjectDisposedException(nameof(SherpaSynthesizer));
            var audio = tts.GenerateWithConfig("", config, null!);
            try
            {
                return audio.Samples;
            }
            finally
            {
                audio.Dispose();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _tts?.Dispose();
            _tts = null;
        }
    }
}

/// <summary>
/// The neural voice engine behind <see cref="ViewModels.FlightData.SpeechEngine"/>: loads the chosen voice,
/// turns each sentence into phonemes and audio (cached), and plays it, preparing the next sentence while
/// the previous one plays. <see cref="Speak"/> returns false, with <see cref="Problem"/> saying why, when
/// it cannot speak (no voice downloaded, no audio player, the native engine missing on this platform, a
/// damaged voice), and the system voice speaks instead. All calls come from the speech worker thread,
/// except <see cref="Stop"/> and <see cref="Release"/>.
/// </summary>
public sealed class NeuralSpeech : IDisposable
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(NeuralSpeech));

    private readonly VoiceStore _store;
    private readonly Func<VoiceInfo, string, ISpeechSynthesizer> _load;
    private readonly Func<IAudioPlayer?> _findPlayer;
    private readonly Func<string?> _engineProblem;
    private readonly object _gate = new();
    private IAudioPlayer? _player;
    private bool _playerSearched;
    private string? _engineChecked;
    private bool _engineSearched;
    private (string Id, ISpeechSynthesizer Synth)? _loaded;
    private (string Path, Phonemizer Phonemizer)? _phonemizer;
    // read by the settings page on the UI thread: no lock, which a loading model holds for seconds
    private volatile Failure? _failed;
    private volatile IPlayback? _playing;

    /// <param name="load">Loads a voice from its folder (tests pass a fake).</param>
    /// <param name="player">How audio is played (tests pass a fake); default: the platform's player.</param>
    /// <param name="engineProblem">Why the native engine cannot run, or null (tests); default: a probe of the library.</param>
    public NeuralSpeech(VoiceStore store, Func<VoiceInfo, string, ISpeechSynthesizer>? load = null, Func<IAudioPlayer?>? player = null,
        Func<string?>? engineProblem = null)
    {
        _store = store;
        _load = load ?? ((voice, dir) => SherpaSynthesizer.Load(voice, dir));
        _findPlayer = player ?? (() => AudioPlayers.Detect());
        _engineProblem = engineProblem ?? (() => SherpaSynthesizer.NativeAvailable(out var p) ? null : p);
    }

    public VoiceStore Store => _store;

    public AudioCache Cache { get; } = new();

    /// <summary>Why the last attempt fell back to the system voice, or null.</summary>
    public string? Problem { get; private set; }

    /// <summary>The voice and speaker that spoke last ("Kokoro, Heart").</summary>
    public string? Active { get; private set; }

    /// <summary>The player in use, for the settings page.</summary>
    public string? PlayerName => Player?.Name;

    private IAudioPlayer? Player
    {
        get
        {
            if (!_playerSearched)
            {
                _player = _findPlayer();
                _playerSearched = true;
            }

            return _player;
        }
    }

    private string? EngineProblem
    {
        get
        {
            if (!_engineSearched)
            {
                _engineChecked = _engineProblem();
                _engineSearched = true;
            }

            return _engineChecked;
        }
    }

    /// <summary>What would stop the neural voice from speaking with these settings, without loading anything.</summary>
    public string? Check(SpeechVoiceSettings settings)
    {
        var voice = settings.ResolveVoice(_store.IsInstalled);
        if (voice == null || !_store.IsInstalled(voice))
            return L.Tr("No neural voice downloaded yet.");
        if (EngineProblem is { } engine)
            return engine;
        if (Player == null)
            return OperatingSystem.IsLinux()
                ? L.Tr("No audio player found: install pipewire (pw-play), pulseaudio-utils (paplay) or alsa-utils (aplay).")
                : L.Tr("No audio player found.");
        return _failed is { } f && f.Id == voice.Id ? f.Error : null;
    }

    /// <summary>
    /// Loads the voice ahead of the first sentence (the model takes a second or two) and says one word into
    /// the void: the first inference after loading is the slowest.
    /// </summary>
    public bool Prepare(SpeechVoiceSettings settings)
    {
        if (!Resolve(settings, out var voice, out var synth, out var speaker))
            return false;
        try
        {
            var phonemes = PhonemizerFor(voice!, speaker!).Phonemes("Ready.");
            if (phonemes.Length > 0)
                synth!.Generate(phonemes, speaker!.Id, 1.0);
        }
        catch (Exception ex)
        {
            Log.Info("voice warm-up", ex);
        }

        return true;
    }

    /// <summary>The pause after a sentence that is followed by another.</summary>
    public static readonly TimeSpan SentencePause = TimeSpan.FromMilliseconds(150);

    /// <summary>Says <paramref name="text"/>; false when the neural voice cannot (see <see cref="Problem"/>).</summary>
    public bool Speak(string text, SpeechVoiceSettings settings, CancellationToken cancel)
    {
        // English voices: Cyrillic, Greek, CJK... text is left to the system voice (not a problem to report)
        if (!SpeechText.IsLatinScript(text))
            return false;
        if (!Resolve(settings, out var voice, out var synth, out var speaker))
            return false;
        var player = Player!;
        Phonemizer phonemizer;
        try
        {
            phonemizer = PhonemizerFor(voice!, speaker!);
        }
        catch (Exception ex)
        {
            return Fail(voice!, L.Tr("The voice's pronunciation lexicon could not be read:") + " " + ex.Message, ex);
        }

        Active = $"{voice!.Name}, {speaker!.Name}";
        var volume = settings.Volume / 100.0;
        var speed = Math.Clamp(settings.Speed, SpeechVoiceSettings.MinSpeed, SpeechVoiceSettings.MaxSpeed);
        var played = false;
        IPlayback? playing = null;
        try
        {
            var sentences = SpeechText.Sentences(text);
            for (var i = 0; i < sentences.Count; i++)
            {
                var sentence = sentences[i];
                if (cancel.IsCancellationRequested)
                    break;
                var phonemes = phonemizer.Phonemes(SpeechText.Normalize(sentence));
                if (phonemes.Length == 0)
                    continue;
                var key = AudioCache.Key(voice.Id, speaker.Id, speed, phonemes);
                if (!Cache.TryGet(key, out var samples))
                {
                    samples = Wav.Normalize(synth!.Generate(phonemes, speaker.Id, speed));
                    Cache.Add(key, samples);
                }

                if (samples.Length == 0)
                    continue;
                var pause = i + 1 < sentences.Count ? (int)(synth!.SampleRate * SentencePause.TotalSeconds) : 0;
                var wav = Wav.Pcm16(samples, synth!.SampleRate, volume, pause);
                playing?.Wait(cancel);
                playing = null;
                if (cancel.IsCancellationRequested)
                    break;
                playing = player.Play(wav, Wav.Duration(samples.Length + pause, synth.SampleRate));
                _playing = playing;
                played = true;
            }

            playing?.Wait(cancel);
            Problem = null;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            playing?.Stop();
            Log.Warn("neural speech", ex);
            if (played)
                return true; // half said: not again with the other voice
            return Fail(voice, L.Tr("The neural voice failed:") + " " + ex.Message, ex);
        }
        finally
        {
            _playing = null;
        }
    }

    /// <summary>Stops what is playing (app exit, speech switched off).</summary>
    public void Stop() => _playing?.Stop();

    /// <summary>Closes a voice's model before it is deleted or reinstalled.</summary>
    public void Release(string voiceId)
    {
        lock (_gate)
        {
            if (_loaded is { } l && string.Equals(l.Id, voiceId, StringComparison.OrdinalIgnoreCase))
            {
                l.Synth.Dispose();
                _loaded = null;
                _phonemizer = null;
            }

        }

        if (_failed is { } f && string.Equals(f.Id, voiceId, StringComparison.OrdinalIgnoreCase))
            _failed = null;

        Cache.Clear();
    }

    /// <summary>Forget earlier failures and look for the player again (settings saved, voice installed).</summary>
    public void Reset()
    {
        _failed = null;
        _playerSearched = false;
    }

    private bool Resolve(SpeechVoiceSettings settings, out VoiceInfo? voice, out ISpeechSynthesizer? synth, out VoiceSpeaker? speaker)
    {
        synth = null;
        speaker = null;
        voice = settings.ResolveVoice(_store.IsInstalled);
        if (Check(settings) is { } problem)
        {
            Problem = problem;
            return false;
        }

        speaker = voice!.SpeakerOrDefault(settings.Speaker < 0 || VoiceCatalog.Find(settings.VoiceId)?.Id != voice.Id ? voice.DefaultSpeaker : settings.Speaker);
        lock (_gate)
        {
            if (_loaded is { } l && l.Id == voice.Id)
            {
                synth = l.Synth;
                return true;
            }

            if (_loaded is { } old)
            {
                old.Synth.Dispose();
                _loaded = null;
                Cache.Clear();
            }

            try
            {
                var started = Environment.TickCount64;
                synth = _load(voice, _store.VoiceDirectory(voice));
                _loaded = (voice.Id, synth);
                Log.Info($"voice {voice.Id} loaded in {Environment.TickCount64 - started} ms");
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                _engineChecked = L.Tr("The neural speech engine is not available on this computer:") + " " + ex.Message;
                _engineSearched = true;
                Problem = _engineChecked;
                Log.Warn("neural speech engine", ex);
                return false;
            }
            catch (Exception ex)
            {
                Fail(voice, ex is InvalidDataException ? ex.Message : L.Tr("The voice could not be loaded:") + " " + ex.Message, ex);
                return false;
            }
        }
    }

    private Phonemizer PhonemizerFor(VoiceInfo voice, VoiceSpeaker speaker)
    {
        var path = voice.Kind == VoiceModelKind.Kokoro
            ? Path.Combine(_store.VoiceDirectory(voice), speaker.British ? "lexicon-gb-en.txt" : "lexicon-us-en.txt")
            : _store.SharedLexiconPath;
        lock (_gate)
        {
            if (_phonemizer is { } p && p.Path == path)
                return p.Phonemizer;
            var loaded = Phonemizer.Load(path, british: speaker.British);
            _phonemizer = (path, loaded);
            return loaded;
        }
    }

    private bool Fail(VoiceInfo voice, string error, Exception ex)
    {
        Log.Warn("voice " + voice.Id, ex);
        _failed = new Failure(voice.Id, error);
        Problem = error;
        return false;
    }

    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            _loaded?.Synth.Dispose();
            _loaded = null;
        }
    }

    private sealed record Failure(string Id, string Error);
}
