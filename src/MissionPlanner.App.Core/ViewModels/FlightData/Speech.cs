using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using log4net;
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

    /// <summary>The command that speaks <paramref name="text"/>, or null if this machine has no speech program.</summary>
    public static SpeechCommand? For(string text, Os os, Func<string, string?> find)
    {
        switch (os)
        {
            case Os.Windows:
                return find("powershell.exe") is { } ps
                    ? new SpeechCommand(ps, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", WindowsScript], text)
                    : null;
            case Os.MacOs:
                return find("say") is { } say ? new SpeechCommand(say, ["--", text], null) : null;
            default:
                // spd-say returns at once unless told to wait; waiting keeps utterances in order
                if (find("spd-say") is { } spd)
                    return new SpeechCommand(spd, ["--wait", "--", text], null);
                if (find("espeak-ng") is { } ng)
                    return new SpeechCommand(ng, ["--", text], null);
                if (find("espeak") is { } es)
                    return new SpeechCommand(es, ["--", text], null);
                if (find("festival") is { } fest)
                    return new SpeechCommand(fest, ["--tts"], text);
                return null;
        }
    }

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
/// (the library calls it on the link's reader thread); one worker thread runs the speech program
/// for each utterance in turn. A backlog is capped so a burst of messages cannot pile up minutes
/// of talking.
/// </summary>
public sealed class SpeechEngine : ISpeech, IDisposable
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(SpeechEngine));

    private const int MaxQueued = 4;
    private const int MaxUtteranceMs = 30_000;

    private readonly ConcurrentQueue<string> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<string, SpeechCommand?> _resolve;
    private readonly Func<SpeechCommand, CancellationToken, bool> _run;
    private CancellationTokenSource _current = new();
    private Thread? _worker;
    private volatile bool _speaking;

    public SpeechEngine()
        : this(null, null)
    {
    }

    /// <summary>For tests: how to find the program and how to run it.</summary>
    internal SpeechEngine(Func<string, SpeechCommand?>? resolve, Func<SpeechCommand, CancellationToken, bool>? run)
    {
        _resolve = resolve ?? (text => SpeechBackend.For(text, SpeechBackend.Current, SpeechBackend.FindOnPath));
        _run = run ?? RunProcess;
    }

    /// <summary>Master switch (Mission Planner's "speechenable").</summary>
    public bool speechEnable { get; set; }

    /// <summary>Nothing queued and nothing being said (callers skip low-value speech while busy).</summary>
    public bool IsReady => !_speaking && _queue.IsEmpty;

    /// <summary>The speech program this machine would use, or null.</summary>
    public string? BackendName => _resolve("test")?.Name;

    /// <summary>Raised on the worker thread after each utterance (the text as spoken).</summary>
    public event EventHandler<string>? Spoken;

    public void SpeakAsync(string text)
    {
        if (!speechEnable || string.IsNullOrWhiteSpace(text))
            return;
        Enqueue(text);
    }

    /// <summary>Queues text even with speech off (the settings dialog's Test button).</summary>
    public void SpeakNow(string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
            Enqueue(text);
    }

    private void Enqueue(string text)
    {
        text = SpeechBackend.Normalize(text);
        if (text.Length == 0)
            return;
        while (_queue.Count >= MaxQueued && _queue.TryDequeue(out _))
        {
            // drop the oldest: the newest message matters most
        }

        _queue.Enqueue(text);
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

            if (!_queue.TryDequeue(out var text))
                continue;

            _speaking = true;
            try
            {
                var cmd = _resolve(text);
                if (cmd == null)
                {
                    Log.Info("no speech program found; not saying: " + text);
                }
                else
                {
                    _run(cmd, _current.Token);
                    Spoken?.Invoke(this, text);
                }
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
    }
}
