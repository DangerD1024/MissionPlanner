using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using log4net;
using MissionPlanner.App.Core.Speech;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.FlightData;

/// <summary>A text-to-speech program to run for one utterance.</summary>
public sealed record SpeechCommand(string FileName, IReadOnlyList<string> Arguments, string? StandardInput)
{
    public string Name => Path.GetFileNameWithoutExtension(FileName);
}

/// <summary>
/// Picks the platform's speech program: <c>spd-say</c>, <c>espeak-ng</c>, <c>espeak</c> or
/// <c>festival</c> on Linux, <c>say</c> on macOS, Windows' System.Speech through PowerShell.
/// The text is passed as one argument (never through a shell) or on standard input.
/// </summary>
public static class SpeechBackend
{
    public enum Os
    {
        Linux,
        MacOs,
        Windows,
    }

    public static Os Current =>
        OperatingSystem.IsWindows() ? Os.Windows : OperatingSystem.IsMacOS() ? Os.MacOs : Os.Linux;

    // System.Speech ships with Windows PowerShell; the text arrives on stdin so nothing needs quoting
    private const string WindowsScript =
        "Add-Type -AssemblyName System.Speech; $s = New-Object System.Speech.Synthesis.SpeechSynthesizer; " +
        "$s.Speak([Console]::In.ReadToEnd())";

    // the same with a voice (first line of stdin; a name that is not installed is ignored), rate and volume
    private const string WindowsScriptWithOptions =
        "Add-Type -AssemblyName System.Speech; $s = New-Object System.Speech.Synthesis.SpeechSynthesizer; " +
        "$v = [Console]::In.ReadLine(); if ($v) { try { $s.SelectVoice($v) } catch { } }; $s.Rate = {rate}; $s.Volume = {volume}; " +
        "$s.Speak([Console]::In.ReadToEnd())";

    /// <summary>The command that speaks <paramref name="text"/>, or null if this machine has no speech program.</summary>
    public static SpeechCommand? For(string text, Os os, Func<string, string?> find) => For(text, os, find, null);

    /// <summary>
    /// The same with the user's voice, rate and volume where the program takes them: SAPI, say and spd-say
    /// (voice, rate, volume), espeak-ng / espeak (voice, words per minute, amplitude); festival takes none.
    /// </summary>
    public static SpeechCommand? For(string text, Os os, Func<string, string?> find, SystemVoiceOptions? options)
    {
        var o = options ?? SystemVoiceOptions.Default;
        switch (os)
        {
            case Os.Windows:
            {
                if (find("powershell.exe") is not { } ps)
                    return null;
                if (o.IsDefault)
                    return new SpeechCommand(ps, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", WindowsScript], text);
                // SAPI's rate is -10..10, about three times slower or faster at the ends
                var rate = Math.Clamp(10 * Math.Log(o.Speed) / Math.Log(3), -10, 10);
                var script = WindowsScriptWithOptions.Replace("{rate}", Int(rate)).Replace("{volume}", Int(Math.Clamp(o.Volume, 0, 100)));
                var voice = (o.Voice ?? "").Replace('\r', ' ').Replace('\n', ' ');
                return new SpeechCommand(ps, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script], voice + "\n" + text);
            }

            case Os.MacOs:
            {
                if (find("say") is not { } say)
                    return null;
                var args = new List<string>();
                if (o.HasVoice)
                    args.AddRange(["-v", o.Voice!]);
                if (!o.NormalSpeed)
                    args.AddRange(["-r", Int(175 * o.Speed)]);
                args.Add("--");
                // say has no volume option; its embedded [[volm]] command sets it for the text
                args.Add(o.FullVolume ? text : $"[[volm {(Math.Clamp(o.Volume, 0, 100) / 100.0).ToString("0.00", CultureInfo.InvariantCulture)}]] {text}");
                return new SpeechCommand(say, args, null);
            }

            default:
            {
                // spd-say returns at once unless told to wait; waiting keeps utterances in order
                if (find("spd-say") is { } spd)
                {
                    var args = new List<string> { "--wait" };
                    if (o.HasVoice)
                        args.AddRange(["-y", o.Voice!]);
                    if (!o.NormalSpeed)
                        args.AddRange(["-r", Int(Math.Clamp((o.Speed - 1) * 100, -100, 100))]);
                    if (!o.FullVolume)
                        args.AddRange(["-i", Int(Math.Clamp(o.Volume, 0, 100) - 100)]);
                    args.AddRange(["--", text]);
                    return new SpeechCommand(spd, args, null);
                }

                if ((find("espeak-ng") ?? find("espeak")) is { } espeak)
                {
                    var args = new List<string>();
                    if (o.HasVoice)
                        args.AddRange(["-v", o.Voice!]);
                    if (!o.NormalSpeed)
                        args.AddRange(["-s", Int(175 * o.Speed)]);
                    if (!o.FullVolume)
                        args.AddRange(["-a", Int(Math.Clamp(o.Volume, 0, 100))]);
                    args.AddRange(["--", text]);
                    return new SpeechCommand(espeak, args, null);
                }

                if (find("festival") is { } fest)
                    return new SpeechCommand(fest, ["--tts"], text);
                return null;
            }
        }
    }

    private static string Int(double v) => Math.Round(v, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);

    /// <summary>Whether the program takes a voice, rate and volume (festival takes none).</summary>
    public static bool HasOptions(string? program) => program is not (null or "festival");

    /// <summary>The program's path when it is on the PATH (or, for PowerShell, in System32).</summary>
    public static string? FindOnPath(string name)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (OperatingSystem.IsWindows())
            paths.Add(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0"));
        else
            paths.AddRange(["/usr/bin", "/usr/local/bin", "/opt/homebrew/bin"]);
        foreach (var dir in paths.Distinct())
        {
            try
            {
                var full = Path.Combine(dir.Trim('"'), name);
                if (File.Exists(full))
                    return full;
            }
            catch (ArgumentException)
            {
                // a malformed PATH entry
            }
        }

        return null;
    }

    /// <summary>Mission Planner's pronunciation fixes ("PreArm" → "Pre Arm", "12m" → "12 meters", "QHover" → "Q Hover").</summary>
    public static string Normalize(string text)
    {
        text = Regex.Replace(text, @"\bPreArm\b", "Pre Arm", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\bdist\b", "distance", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\bNAV\b", "Navigation", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\b([0-9]+)m\b", "$1 meters", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\b([0-9]+)ft\b", "$1 feet", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\b([0-9]+)\s*baud\b", "$1 baudrate", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\bq((?!u)[a-z]+)", "q $1", RegexOptions.IgnoreCase);
        return text.Trim();
    }
}

/// <summary>
/// Mission Planner's <see cref="ISpeech"/> for every platform. <see cref="SpeakAsync"/> only queues
/// (the library calls it on the link's reader thread); one worker thread says each utterance in turn,
/// with the downloaded neural voice when one is chosen and ready (<see cref="NeuralSpeech"/>), else with
/// the speech program. A backlog is capped so a burst of messages cannot pile up minutes of talking.
/// </summary>
public sealed class SpeechEngine : ISpeech, IDisposable
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(SpeechEngine));

    private const int MaxQueued = 4;
    private const int MaxUtteranceMs = 30_000;

    private readonly ConcurrentQueue<Utterance> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<string, SpeechVoiceSettings, SpeechCommand?> _resolve;
    private readonly Func<SpeechCommand, CancellationToken, bool> _run;
    private readonly NeuralSpeech? _neural;
    private readonly Func<SpeechVoiceSettings> _settings;
    private CancellationTokenSource _current = new();
    private Thread? _worker;
    private volatile bool _speaking;
    private volatile bool _neuralSpoke;

    public SpeechEngine()
        : this(null, null, new NeuralSpeech(new VoiceStore(VoiceStore.DefaultRoot)), SpeechVoiceSettings.Load)
    {
    }

    /// <summary>For tests: how to find the program and how to run it (system voice only).</summary>
    internal SpeechEngine(Func<string, SpeechCommand?>? resolve, Func<SpeechCommand, CancellationToken, bool>? run)
        : this(resolve == null ? null : (text, _) => resolve(text), run, null, () => new SpeechVoiceSettings(SpeechEngineKind.System))
    {
    }

    /// <summary>For tests: the system program, the neural engine and where the settings come from.</summary>
    internal SpeechEngine(Func<string, SpeechVoiceSettings, SpeechCommand?>? resolve, Func<SpeechCommand, CancellationToken, bool>? run,
        NeuralSpeech? neural, Func<SpeechVoiceSettings>? settings)
    {
        _resolve = resolve ?? ((text, s) => SpeechBackend.For(text, SpeechBackend.Current, SpeechBackend.FindOnPath, s.SystemOptions));
        _run = run ?? RunProcess;
        _neural = neural;
        _settings = settings ?? SpeechVoiceSettings.Load;
    }

    private volatile bool _enabled;

    /// <summary>Master switch (Mission Planner's "speechenable"). Switching it on loads the neural voice in the background.</summary>
    public bool speechEnable
    {
        get => _enabled;
        set
        {
            var was = _enabled;
            _enabled = value;
            if (value && !was)
                Prepare();
        }
    }

    /// <summary>Nothing queued and nothing being said (callers skip low-value speech while busy).</summary>
    public bool IsReady => !_speaking && _queue.IsEmpty;

    /// <summary>The speech program this machine would use, or null.</summary>
    public string? BackendName => _resolve("test", SpeechVoiceSettings.Load() with { Engine = SpeechEngineKind.System })?.Name;

    /// <summary>The neural voice engine (null in tests that only use the program).</summary>
    public NeuralSpeech? Neural => _neural;

    /// <summary>The downloaded voices.</summary>
    public VoiceStore? Voices => _neural?.Store;

    /// <summary>The neural voice spoke the last utterance.</summary>
    public bool LastSpokenByNeural => _neuralSpoke;

    /// <summary>Raised on the worker thread after each utterance (the text as spoken).</summary>
    public event EventHandler<string>? Spoken;

    public void SpeakAsync(string text)
    {
        if (!speechEnable || string.IsNullOrWhiteSpace(text))
            return;
        Enqueue(text, null);
    }

    /// <summary>Queues text even with speech off (the settings dialog's Test button).</summary>
    public void SpeakNow(string text) => SpeakNow(text, null);

    /// <summary>Queues text even with speech off, with these voice settings instead of the saved ones (Test voice).</summary>
    public void SpeakNow(string text, SpeechVoiceSettings? voice)
    {
        if (!string.IsNullOrWhiteSpace(text))
            Enqueue(text, voice);
    }

    /// <summary>Loads the neural voice in the background so the first announcement is not late.</summary>
    public void Prepare()
    {
        if (_neural == null)
            return;
        _queue.Enqueue(new Utterance(null, null));
        EnsureWorker();
        _signal.Release();
    }

    private void Enqueue(string text, SpeechVoiceSettings? voice)
    {
        text = SpeechBackend.Normalize(text);
        if (text.Length == 0)
            return;
        while (_queue.Count >= MaxQueued && _queue.TryDequeue(out _))
        {
            // drop the oldest: the newest message matters most
        }

        _queue.Enqueue(new Utterance(text, voice));
        EnsureWorker();
        _signal.Release();
    }

    public void SpeakAsyncCancelAll()
    {
        while (_queue.TryDequeue(out _))
        {
        }

        // not disposed: the worker may still be reading its token (a plain source holds no resources)
        var current = Interlocked.Exchange(ref _current, new CancellationTokenSource());
        current.Cancel();
        _neural?.Stop();
    }

    private void EnsureWorker()
    {
        if (_worker != null)
            return;
        lock (_queue)
        {
            if (_worker != null)
                return;
            _worker = new Thread(Work) { IsBackground = true, Name = "Speech" };
            _worker.Start();
        }
    }

    private void Work()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                _signal.Wait(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!_queue.TryDequeue(out var item))
                continue;

            var settings = item.Voice ?? Settings();
            if (item.Text == null)
            {
                // warm-up: load the chosen voice
                if (settings.Engine == SpeechEngineKind.Neural)
                    Try(() => _neural?.Prepare(settings));
                continue;
            }

            _speaking = true;
            try
            {
                var token = _current.Token;
                var said = settings.Engine == SpeechEngineKind.Neural && _neural != null && _neural.Speak(item.Text, settings, token);
                _neuralSpoke = said;
                if (!said && !token.IsCancellationRequested)
                {
                    var cmd = _resolve(item.Text, settings);
                    if (cmd == null)
                        Log.Info("no speech program found; not saying: " + item.Text);
                    else
                        _run(cmd, token);
                }

                Spoken?.Invoke(this, item.Text);
            }
            catch (Exception ex)
            {
                Log.Warn("speech", ex);
            }
            finally
            {
                _speaking = false;
            }
        }
    }

    private SpeechVoiceSettings Settings()
    {
        try
        {
            return _settings();
        }
        catch (Exception ex)
        {
            Log.Info("speech settings", ex);
            return new SpeechVoiceSettings();
        }
    }

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Info("speech", ex);
        }
    }

    private static bool RunProcess(SpeechCommand cmd, CancellationToken cancel)
    {
        var psi = new ProcessStartInfo(cmd.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = cmd.StandardInput != null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in cmd.Arguments)
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi);
        if (p == null)
            return false;
        p.OutputDataReceived += (_, _) => { };
        p.ErrorDataReceived += (_, _) => { };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (cmd.StandardInput != null)
        {
            p.StandardInput.Write(cmd.StandardInput);
            p.StandardInput.Close();
        }

        using var reg = cancel.Register(() =>
        {
            try
            {
                if (!p.HasExited)
                    p.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // already gone
            }
        });
        if (!p.WaitForExit(MaxUtteranceMs))
        {
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
            }

            return false;
        }

        return p.ExitCode == 0;
    }

    public void Dispose()
    {
        _stop.Cancel();
        SpeakAsyncCancelAll();
        _neural?.Dispose();
    }

    /// <param name="Text">What to say; null: load the neural voice only.</param>
    /// <param name="Voice">Voice settings for this utterance (Test voice); null: the saved ones.</param>
    private sealed record Utterance(string? Text, SpeechVoiceSettings? Voice);
}
