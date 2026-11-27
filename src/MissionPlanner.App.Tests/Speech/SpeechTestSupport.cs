using System.Text;
using ICSharpCode.SharpZipLib.BZip2;
using ICSharpCode.SharpZipLib.Tar;
using MissionPlanner.App.Core.Speech;

namespace MissionPlanner.App.Tests.Speech;

/// <summary>Voice archives built on the spot (no network in tests), and fakes for the engine and the player.</summary>
internal static class SpeechTestSupport
{
    public static string NewFolder(string name)
    {
        var dir = Path.Combine(TestEnvironment.DataRoot, name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A .tar.bz2 shaped like sherpa-onnx's: the voice's folder with its files, plus what must not be unpacked.</summary>
    public static string BuildArchive(string folder, VoiceInfo voice, bool withHostileEntries = true, IEnumerable<string>? leaveOut = null)
    {
        var path = Path.Combine(folder, voice.Archive);
        var skip = new HashSet<string>(leaveOut ?? []);
        using (var file = File.Create(path))
        using (var bz = new BZip2OutputStream(file))
        using (var tar = new TarOutputStream(bz))
        {
            Dir(tar, voice.Folder + "/");
            foreach (var f in VoiceStore.RequiredFiles(voice).Where(f => !skip.Contains(f)))
                Add(tar, voice.Folder + "/" + f, f.EndsWith(".txt") ? Lexicon() : "model data " + f);
            Add(tar, voice.Folder + "/LICENSE", "Apache License 2.0");
            Dir(tar, voice.Folder + "/espeak-ng-data/");
            Add(tar, voice.Folder + "/espeak-ng-data/phontab", "eSpeak data the 2.x engine does not read");
            Add(tar, voice.Folder + "/dict/jieba.dict.utf8", "Chinese dictionary");
            Add(tar, voice.Folder + "/date-zh.fst", "Chinese rules");
            if (withHostileEntries)
            {
                Add(tar, voice.Folder + "/../escaped.txt", "outside the voice folder");
                Add(tar, "/tmp/absolute-" + Guid.NewGuid().ToString("N") + ".txt", "absolute path");
                Add(tar, "other-folder/stray.txt", "not this voice");
                var link = TarEntry.CreateTarEntry(voice.Folder + "/link");
                link.TarHeader.TypeFlag = TarHeader.LF_SYMLINK;
                link.TarHeader.LinkName = "/etc/passwd";
                link.Size = 0;
                tar.PutNextEntry(link);
                tar.CloseEntry();
            }
        }

        return path;
    }

    private static string Lexicon() => "kokoro k ˈ O k ə ɹ O\nmode m ˈ O d\n";

    private static void Dir(TarOutputStream tar, string name)
    {
        var e = TarEntry.CreateTarEntry(name);
        e.TarHeader.TypeFlag = TarHeader.LF_DIR;
        e.Size = 0;
        tar.PutNextEntry(e);
        tar.CloseEntry();
    }

    private static void Add(TarOutputStream tar, string name, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var e = TarEntry.CreateTarEntry(name);
        e.Size = bytes.Length;
        tar.PutNextEntry(e);
        tar.Write(bytes, 0, bytes.Length);
        tar.CloseEntry();
    }

    /// <summary>A catalogue voice whose expected size is that of a local archive.</summary>
    public static VoiceInfo Sized(VoiceInfo voice, string archive) => voice with { DownloadBytes = new FileInfo(archive).Length, InstalledBytes = 1000 };

    /// <summary>"Downloads" by copying local files: the archive, and a lexicon for the shared-lexicon URL.</summary>
    public static Func<Uri, string, IProgress<double>?, CancellationToken, Task> LocalDownload(string archive, List<Uri>? fetched = null,
        string? lexiconContent = null) =>
        async (url, path, progress, cancel) =>
        {
            lock (fetched ?? new List<Uri>())
                fetched?.Add(url);
            await Task.Yield();
            cancel.ThrowIfCancellationRequested();
            progress?.Report(0.5);
            if (url == VoiceCatalog.SharedLexicon.Url)
                await File.WriteAllTextAsync(path, lexiconContent ?? "", cancel);
            else
                File.Copy(archive, path, overwrite: true);
            progress?.Report(1);
        };
}

/// <summary>A synthesizer that makes a tone per phoneme string and counts its calls.</summary>
internal sealed class FakeSynthesizer : ISpeechSynthesizer
{
    public int SampleRate => 16000;

    public List<string> Generated { get; } = new();

    public bool Disposed { get; private set; }

    public Exception? Throw { get; set; }

    public float[] Generate(string phonemes, int speaker, double speed)
    {
        if (Throw != null)
            throw Throw;
        lock (Generated)
            Generated.Add(phonemes);
        var samples = new float[100 + phonemes.Length * 10];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = 0.5f;
        return samples;
    }

    public void Dispose() => Disposed = true;
}

/// <summary>A player that never makes a sound: it records what it was given, in order.</summary>
internal sealed class RecordingPlayer : IAudioPlayer
{
    public List<byte[]> Played { get; } = new();

    /// <summary>When set, each playback waits for it (or for cancellation).</summary>
    public ManualResetEventSlim? Hold { get; set; }

    public int Stopped;

    public string Name => "test player";

    public IPlayback Play(byte[] wav, TimeSpan duration)
    {
        lock (Played)
            Played.Add(wav);
        return new Playback(this);
    }

    private sealed class Playback(RecordingPlayer owner) : IPlayback
    {
        public void Wait(CancellationToken cancel)
        {
            if (owner.Hold is { } hold)
            {
                try
                {
                    hold.Wait(cancel);
                }
                catch (OperationCanceledException)
                {
                    Stop();
                }
            }
        }

        public void Stop() => Interlocked.Increment(ref owner.Stopped);
    }
}
