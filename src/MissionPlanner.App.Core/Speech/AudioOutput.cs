using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using log4net;
using MissionPlanner.App.Core.ViewModels.FlightData;

namespace MissionPlanner.App.Core.Speech;

/// <summary>16-bit PCM WAV files from float samples.</summary>
public static class Wav
{
    /// <summary>
    /// Scales by <paramref name="volume"/> (0..1, clipped to the 16-bit range) and wraps in a WAV header,
    /// with <paramref name="silence"/> samples of quiet after the audio (the pause between sentences).
    /// </summary>
    public static byte[] Pcm16(ReadOnlySpan<float> samples, int sampleRate, double volume, int silence = 0)
    {
        var gain = (float)Math.Clamp(volume, 0, 1);
        var total = samples.Length + Math.Max(0, silence);
        var bytes = new byte[44 + total * 2];
        var w = bytes.AsSpan();
        "RIFF"u8.CopyTo(w);
        BitConverter.TryWriteBytes(w[4..], 36 + total * 2);
        "WAVE"u8.CopyTo(w[8..]);
        "fmt "u8.CopyTo(w[12..]);
        BitConverter.TryWriteBytes(w[16..], 16);
        BitConverter.TryWriteBytes(w[20..], (short)1); // PCM
        BitConverter.TryWriteBytes(w[22..], (short)1); // mono
        BitConverter.TryWriteBytes(w[24..], sampleRate);
        BitConverter.TryWriteBytes(w[28..], sampleRate * 2);
        BitConverter.TryWriteBytes(w[32..], (short)2);
        BitConverter.TryWriteBytes(w[34..], (short)16);
        "data"u8.CopyTo(w[36..]);
        BitConverter.TryWriteBytes(w[40..], total * 2);
        for (var i = 0; i < samples.Length; i++)
        {
            var v = Math.Clamp(samples[i] * gain, -1f, 1f);
            BitConverter.TryWriteBytes(w[(44 + i * 2)..], (short)MathF.Round(v * 32767f));
        }

        return bytes;
    }

    /// <summary>
    /// Brings a sentence to the same loudness whatever the voice (peak at -1 dBFS; Kokoro's raw output peaks
    /// around -7), at most four times louder so near-silence is not blown up. In place; returns the array.
    /// </summary>
    public static float[] Normalize(float[] samples, float peak = 0.89f, float maxGain = 4f)
    {
        var max = 0f;
        foreach (var s in samples)
            max = Math.Max(max, Math.Abs(s));
        if (max <= 0)
            return samples;
        var gain = Math.Min(peak / max, maxGain);
        for (var i = 0; i < samples.Length; i++)
            samples[i] *= gain;
        return samples;
    }

    public static TimeSpan Duration(int samples, int sampleRate) =>
        sampleRate > 0 ? TimeSpan.FromSeconds((double)samples / sampleRate) : TimeSpan.Zero;
}

/// <summary>Something playing; <see cref="Wait"/> returns when it has finished (or stops it when cancelled).</summary>
public interface IPlayback
{
    void Wait(CancellationToken cancel);

    /// <summary>Stops at once (app exit).</summary>
    void Stop();

    /// <summary>After <see cref="Wait"/>: the player gave up (no sound server, no device), so the next one may be tried.</summary>
    bool Failed => false;
}

/// <summary>Plays WAV audio on the computer's default output.</summary>
public interface IAudioPlayer
{
    string Name { get; }

    /// <summary>Starts playing and returns at once.</summary>
    IPlayback Play(byte[] wav, TimeSpan duration);
}

/// <summary>The platform's player: winmm on Windows, afplay on macOS, pw-play / paplay / aplay on Linux.</summary>
public static class AudioPlayers
{
    /// <summary>This machine's player, or null when none is installed.</summary>
    public static IAudioPlayer? Detect(Func<string, string?>? find = null) => Detect(SpeechBackend.Current, find ?? SpeechBackend.FindOnPath);

    public static IAudioPlayer? Detect(SpeechBackend.Os os, Func<string, string?> find)
    {
        switch (os)
        {
            case SpeechBackend.Os.Windows:
                return OperatingSystem.IsWindows() ? new WinMmAudioPlayer() : null;
            case SpeechBackend.Os.MacOs:
                return find("afplay") is { } afplay ? new ProcessAudioPlayer(afplay, []) : null;
            default:
            {
                // PipeWire, PulseAudio, then ALSA: what a desktop has, in that order; a program can be
                // installed without its sound server running, so the next one is tried when it fails
                var players = new List<IAudioPlayer>();
                if (find("pw-play") is { } pw)
                    players.Add(new ProcessAudioPlayer(pw, []));
                if (find("paplay") is { } pa)
                    players.Add(new ProcessAudioPlayer(pa, []));
                if (find("aplay") is { } alsa)
                    players.Add(new ProcessAudioPlayer(alsa, ["-q"]));
                return players.Count switch
                {
                    0 => null,
                    1 => players[0],
                    _ => new FallbackAudioPlayer(players),
                };
            }
        }
    }
}

/// <summary>Plays with the first player that works, and keeps to it once one has failed.</summary>
public sealed class FallbackAudioPlayer(IReadOnlyList<IAudioPlayer> players) : IAudioPlayer
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(FallbackAudioPlayer));
    private int _current;

    public IReadOnlyList<IAudioPlayer> Players => players;

    public string Name => players[Math.Min(_current, players.Count - 1)].Name;

    public IPlayback Play(byte[] wav, TimeSpan duration) => new Attempt(this, wav, duration);

    private sealed class Attempt : IPlayback
    {
        private readonly FallbackAudioPlayer _owner;
        private readonly byte[] _wav;
        private readonly TimeSpan _duration;
        private int _index;
        private IPlayback _playing;

        public Attempt(FallbackAudioPlayer owner, byte[] wav, TimeSpan duration)
        {
            _owner = owner;
            _wav = wav;
            _duration = duration;
            _index = Math.Min(owner._current, owner.Players.Count - 1);
            _playing = owner.Players[_index].Play(wav, duration);
        }

        public bool Failed => _playing.Failed;

        public void Wait(CancellationToken cancel)
        {
            _playing.Wait(cancel);
            while (_playing.Failed && !cancel.IsCancellationRequested && _index + 1 < _owner.Players.Count)
            {
                Log.Info(_owner.Players[_index].Name + " could not play; trying " + _owner.Players[_index + 1].Name);
                _index++;
                _owner._current = Math.Max(_owner._current, _index);
                _playing = _owner.Players[_index].Play(_wav, _duration);
                _playing.Wait(cancel);
            }
        }

        public void Stop() => _playing.Stop();
    }
}

/// <summary>
/// Plays a WAV file with a program (pw-play, paplay, aplay, afplay): the audio goes to a temporary file,
/// the program is started with its path and killed if the speech is cancelled. Starting a player takes a
/// few tens of milliseconds, which the engine hides by preparing the next sentence meanwhile.
/// </summary>
public sealed class ProcessAudioPlayer(string fileName, IReadOnlyList<string> arguments) : IAudioPlayer
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(ProcessAudioPlayer));
    private static int _counter;

    public string Name => Path.GetFileNameWithoutExtension(fileName);

    public string FileName => fileName;

    public IReadOnlyList<string> Arguments => arguments;

    private static string TempPath => Path.Combine(Path.GetTempPath(), "mp-speech-" + Environment.ProcessId);

    private static string TempFolder
    {
        get
        {
            var dir = TempPath;
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Removes this process's temporary sound files (app exit).</summary>
    public static void CleanUp()
    {
        try
        {
            if (Directory.Exists(TempPath))
                Directory.Delete(TempPath, recursive: true);
        }
        catch (Exception)
        {
            // a player still holds a file: the OS clears the temp folder
        }
    }

    public IPlayback Play(byte[] wav, TimeSpan duration)
    {
        var path = Path.Combine(TempFolder, $"say-{Interlocked.Increment(ref _counter)}.wav");
        File.WriteAllBytes(path, wav);
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in arguments)
            psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(path);
        try
        {
            var p = Process.Start(psi) ?? throw new IOException(fileName + " did not start");
            p.OutputDataReceived += (_, _) => { };
            p.ErrorDataReceived += (_, _) => { };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return new Running(p, path, duration);
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
        }
    }

    private sealed class Running(Process process, string path, TimeSpan duration) : IPlayback
    {
        private int _waited;
        private volatile bool _stopped;

        public bool Failed { get; private set; }

        public void Wait(CancellationToken cancel)
        {
            if (Interlocked.Exchange(ref _waited, 1) == 1)
                return;
            try
            {
                using var reg = cancel.Register(Stop);
                // a stuck audio server must not hold the speech queue for ever
                if (!process.WaitForExit(duration + TimeSpan.FromSeconds(10)))
                {
                    Log.Info(process.StartInfo.FileName + " did not finish; stopped");
                    Stop();
                }

                process.WaitForExit();
                Failed = !_stopped && process.ExitCode != 0;
            }
            finally
            {
                process.Dispose();
                TryDelete(path);
            }
        }

        public void Stop()
        {
            _stopped = true;
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // gone already
            }
        }
    }
}

/// <summary>
/// Windows: the WAV is played from memory by winmm's PlaySound, inside the app: no process to start and no
/// file to write, so a sentence starts within milliseconds. PlaySound plays one sound at a time per
/// process, which suits the one speech queue.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WinMmAudioPlayer : IAudioPlayer
{
    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;
    private const uint SndMemory = 0x0004;

    public string Name => "Windows audio";

    public IPlayback Play(byte[] wav, TimeSpan duration)
    {
        var pin = GCHandle.Alloc(wav, GCHandleType.Pinned);
        if (!PlaySound(pin.AddrOfPinnedObject(), IntPtr.Zero, SndMemory | SndAsync | SndNoDefault))
        {
            pin.Free();
            throw new IOException("Windows could not play the sound (PlaySound failed).");
        }

        return new Playing(pin, duration);
    }

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", SetLastError = true)]
    private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);

    private sealed class Playing(GCHandle pin, TimeSpan duration) : IPlayback
    {
        private int _done;

        public void Wait(CancellationToken cancel)
        {
            // PlaySound reports no end: wait for the audio's length, then make sure it has stopped before
            // the buffer is released
            cancel.WaitHandle.WaitOne(duration + TimeSpan.FromMilliseconds(120));
            Stop();
        }

        public void Stop()
        {
            if (Interlocked.Exchange(ref _done, 1) == 1)
                return;
            PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
            pin.Free();
        }
    }
}

/// <summary>
/// Recently generated audio, so a phrase said again ("Mode Loiter", "Waypoint 3") plays at once. Keyed by
/// voice, speaker, speed and the phonemes; bounded by total samples (least recently used goes first).
/// </summary>
public sealed class AudioCache(long maxSamples = 24_000L * 60 * 4)
{
    private readonly Dictionary<string, LinkedListNode<(string Key, float[] Samples)>> _map = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, float[] Samples)> _order = new();
    private long _samples;

    public int Count
    {
        get
        {
            lock (_map)
                return _map.Count;
        }
    }

    public long Samples
    {
        get
        {
            lock (_map)
                return _samples;
        }
    }

    public static string Key(string voice, int speaker, double speed, string phonemes) =>
        $"{voice}\u001f{speaker}\u001f{speed:0.00}\u001f{phonemes}";

    public bool TryGet(string key, out float[] samples)
    {
        lock (_map)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                samples = node.Value.Samples;
                return true;
            }
        }

        samples = [];
        return false;
    }

    public void Add(string key, float[] samples)
    {
        if (samples.Length == 0 || samples.Length > maxSamples)
            return;
        lock (_map)
        {
            if (_map.Remove(key, out var old))
            {
                _order.Remove(old);
                _samples -= old.Value.Samples.Length;
            }

            _map[key] = _order.AddFirst((key, samples));
            _samples += samples.Length;
            while (_samples > maxSamples && _order.Last is { } last)
            {
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
                _samples -= last.Value.Samples.Length;
            }
        }
    }

    public void Clear()
    {
        lock (_map)
        {
            _map.Clear();
            _order.Clear();
            _samples = 0;
        }
    }
}
