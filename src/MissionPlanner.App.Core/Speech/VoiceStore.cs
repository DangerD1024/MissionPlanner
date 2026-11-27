using System.Collections.Concurrent;
using System.Text.Json;
using ICSharpCode.SharpZipLib.BZip2;
using ICSharpCode.SharpZipLib.Tar;
using log4net;
using MissionPlanner.App.Core.Localization;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Firmware;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.Speech;

public enum VoiceInstallState
{
    NotInstalled,
    Downloading,
    Installing,
    Installed,
    Failed,
}

/// <summary>Where a voice is: installed, or how far its download has got.</summary>
/// <param name="Fraction">0..1 of the current step, or -1 while unknown.</param>
public readonly record struct VoiceStatus(VoiceInstallState State, double Fraction = 0, string? Error = null)
{
    public bool Busy => State is VoiceInstallState.Downloading or VoiceInstallState.Installing;
}

/// <summary>
/// The downloaded neural voices, in <c>&lt;data folder&gt;/voices/&lt;archive folder&gt;/</c> (the per-user data
/// folder, never a system-wide one). A download goes to a <c>.part</c> file, is checked against the
/// catalogue's size, unpacked into a temporary folder (only the files the engine reads: the archives also
/// carry eSpeak data and Chinese dictionaries that sherpa-onnx 2.x does not use) and moved into place in one
/// step, with a marker written last, so a voice is either complete or absent.
/// </summary>
public sealed class VoiceStore
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(VoiceStore));

    /// <summary>Written last into an installed voice's folder: what was installed.</summary>
    public const string MarkerName = ".mp-voice.json";

    private const long SpaceMargin = 50L * 1024 * 1024;

    private readonly Func<Uri, string, IProgress<double>?, CancellationToken, Task> _download;
    private readonly Func<string, long?> _freeSpace;
    private readonly ConcurrentDictionary<string, VoiceStatus> _status = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lexiconGate = new(1, 1);

    /// <param name="root">The voices folder.</param>
    /// <param name="download">How a file is fetched (tests pass a local copy); default: HTTPS with progress.</param>
    /// <param name="freeSpace">Free bytes on the drive of a folder (tests); default: the drive's.</param>
    public VoiceStore(string root, Func<Uri, string, IProgress<double>?, CancellationToken, Task>? download = null,
        Func<string, long?>? freeSpace = null)
    {
        Root = root;
        _download = download ?? ((url, path, progress, cancel) =>
            FileDownloader.DownloadAsync(FirmwareManifest.Http, url, path, progress, cancel, TimeSpan.FromSeconds(60)));
        _freeSpace = freeSpace ?? FreeSpace;
    }

    /// <summary>The voices folder in the user's data folder.</summary>
    public static string DefaultRoot => Path.Combine(Settings.GetUserDataDirectory(), "voices");

    public string Root { get; }

    /// <summary>Raised (on a worker thread) when a voice's status changes; the argument is its id.</summary>
    public event EventHandler<string>? Changed;

    public string VoiceDirectory(VoiceInfo voice) => Path.Combine(Root, voice.Folder);

    public string SharedLexiconPath => Path.Combine(Root, VoiceCatalog.SharedLexicon.Name);

    public bool SharedLexiconInstalled => File.Exists(SharedLexiconPath);

    public bool IsInstalled(VoiceInfo voice) =>
        File.Exists(Path.Combine(VoiceDirectory(voice), MarkerName)) && (!voice.SharedLexicon || SharedLexiconInstalled);

    public IEnumerable<VoiceInfo> Installed => VoiceCatalog.All.Where(IsInstalled);

    public VoiceStatus Status(VoiceInfo voice)
    {
        if (_status.TryGetValue(voice.Id, out var s) && (s.Busy || s.State == VoiceInstallState.Failed))
            return s;
        return new VoiceStatus(IsInstalled(voice) ? VoiceInstallState.Installed : VoiceInstallState.NotInstalled);
    }

    /// <summary>Bytes the voice's files take on disk (0 if not installed).</summary>
    public long InstalledSize(VoiceInfo voice)
    {
        var dir = VoiceDirectory(voice);
        if (!Directory.Exists(dir))
            return 0;
        try
        {
            return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Downloads and installs a voice (and the shared lexicon it needs). Returns when done; progress and errors
    /// are in <see cref="Status"/>. A second call while it runs returns at once.
    /// </summary>
    public async Task<bool> InstallAsync(VoiceInfo voice, CancellationToken cancel = default)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        if (!_running.TryAdd(voice.Id, cts))
        {
            cts.Dispose();
            return false;
        }

        try
        {
            await Task.Run(() => InstallCoreAsync(voice, cts.Token), cts.Token).ConfigureAwait(false);
            Set(voice, new VoiceStatus(VoiceInstallState.Installed, 1));
            return true;
        }
        catch (OperationCanceledException)
        {
            _status.TryRemove(voice.Id, out _);
            Changed?.Invoke(this, voice.Id);
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn("voice install " + voice.Id, ex);
            Set(voice, new VoiceStatus(VoiceInstallState.Failed, 0, ex is IOException or InvalidDataException or HttpRequestException or TimeoutException or TarException or BZip2Exception
                ? ex.Message
                : ex.GetType().Name + ": " + ex.Message));
            return false;
        }
        finally
        {
            _running.TryRemove(voice.Id, out _);
            cts.Dispose();
        }
    }

    /// <summary>Stops a running download or unpacking; nothing is left behind.</summary>
    public void Cancel(VoiceInfo voice)
    {
        if (_running.TryGetValue(voice.Id, out var cts))
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>Removes an installed voice (and the shared lexicon once no installed voice needs it).</summary>
    public void Delete(VoiceInfo voice)
    {
        Cancel(voice);
        var dir = VoiceDirectory(voice);
        if (Directory.Exists(dir))
        {
            // renamed first, so a voice half deleted (a file in use on Windows) never looks installed
            var trash = Path.Combine(Root, ".deleted-" + voice.Folder + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.Move(dir, trash);
            TryDeleteDirectory(trash);
        }

        if (voice.SharedLexicon && !VoiceCatalog.All.Any(v => v.SharedLexicon && v.Id != voice.Id && IsInstalled(v)))
            TryDeleteFile(SharedLexiconPath);
        _status.TryRemove(voice.Id, out _);
        Changed?.Invoke(this, voice.Id);
    }

    /// <summary>Clears what an interrupted run left (partial downloads, temporary and deleted folders).</summary>
    public void CleanUp()
    {
        if (!Directory.Exists(Root))
            return;
        foreach (var dir in Directory.GetDirectories(Root, ".*"))
        {
            var name = Path.GetFileName(dir);
            if ((name.StartsWith(".tmp-", StringComparison.Ordinal) || name.StartsWith(".deleted-", StringComparison.Ordinal)) && !_running.Any(r => name.Contains(r.Key, StringComparison.OrdinalIgnoreCase)))
                TryDeleteDirectory(dir);
        }
    }

    private async Task InstallCoreAsync(VoiceInfo voice, CancellationToken cancel)
    {
        Directory.CreateDirectory(Root);
        var needLexicon = voice.SharedLexicon && !SharedLexiconInstalled;
        var needed = voice.DownloadBytes + voice.InstalledBytes + (needLexicon ? VoiceCatalog.SharedLexicon.Bytes : 0) + SpaceMargin;
        if (_freeSpace(Root) is { } free && free < needed)
            throw new IOException(L.Format("Not enough free disk space: the voice needs {0}, {1} is free.", FormatSize(needed), FormatSize(free)));

        Set(voice, new VoiceStatus(VoiceInstallState.Downloading, -1));
        var archive = Path.Combine(Root, voice.Archive);
        try
        {
            var total = (double)voice.TotalDownloadBytes(!needLexicon);
            var progress = new Progress(f => Set(voice, new VoiceStatus(VoiceInstallState.Downloading, f < 0 ? -1 : f * voice.DownloadBytes / total)));
            await _download(voice.Url, archive, progress, cancel).ConfigureAwait(false);
            CheckSize(archive, voice.DownloadBytes);

            if (needLexicon)
            {
                await _lexiconGate.WaitAsync(cancel).ConfigureAwait(false);
                try
                {
                    if (!SharedLexiconInstalled)
                    {
                        var lexicon = new Progress(f => Set(voice, new VoiceStatus(VoiceInstallState.Downloading,
                            (voice.DownloadBytes + Math.Max(0, f) * VoiceCatalog.SharedLexicon.Bytes) / total)));
                        var temp = SharedLexiconPath + ".download";
                        await _download(VoiceCatalog.SharedLexicon.Url, temp, lexicon, cancel).ConfigureAwait(false);
                        CheckSize(temp, VoiceCatalog.SharedLexicon.Bytes);
                        File.Move(temp, SharedLexiconPath, overwrite: true);
                    }
                }
                finally
                {
                    _lexiconGate.Release();
                }
            }

            Set(voice, new VoiceStatus(VoiceInstallState.Installing, 0));
            InstallArchive(voice, archive, f => Set(voice, new VoiceStatus(VoiceInstallState.Installing, f)), cancel);
        }
        finally
        {
            TryDeleteFile(archive);
            TryDeleteFile(SharedLexiconPath + ".download");
        }
    }

    /// <summary>Unpacks a downloaded archive and moves the voice into place.</summary>
    internal void InstallArchive(VoiceInfo voice, string archive, Action<double>? progress, CancellationToken cancel)
    {
        var temp = Path.Combine(Root, ".tmp-" + voice.Id + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        try
        {
            var files = Extract(archive, temp, voice.Folder, progress, cancel);
            var unpacked = Path.Combine(temp, voice.Folder);
            foreach (var required in RequiredFiles(voice))
            {
                var path = Path.Combine(unpacked, required);
                if (!File.Exists(path) || new FileInfo(path).Length == 0)
                    throw new InvalidDataException(L.Format("The voice archive has no {0}.", required));
            }

            var marker = new VoiceMarker(voice.Id, voice.Archive, DateTime.UtcNow, files);
            File.WriteAllText(Path.Combine(unpacked, MarkerName), JsonSerializer.Serialize(marker));

            cancel.ThrowIfCancellationRequested();
            var dir = VoiceDirectory(voice);
            if (Directory.Exists(dir))
            {
                var old = Path.Combine(Root, ".deleted-" + voice.Folder + "-" + Guid.NewGuid().ToString("N")[..8]);
                Directory.Move(dir, old);
                TryDeleteDirectory(old);
            }

            Directory.Move(unpacked, dir);
        }
        finally
        {
            TryDeleteDirectory(temp);
        }
    }

    /// <summary>The files the engine opens, relative to the voice folder.</summary>
    public static IReadOnlyList<string> RequiredFiles(VoiceInfo voice) => voice.Kind switch
    {
        VoiceModelKind.Kokoro => [voice.ModelFile, "voices.bin", "tokens.txt", "lexicon-us-en.txt", "lexicon-gb-en.txt"],
        VoiceModelKind.Kitten => [voice.ModelFile, "voices.bin", "tokens.txt"],
        _ => [voice.ModelFile, "tokens.txt"],
    };

    /// <summary>Archive entries the engine never reads (sherpa-onnx 2.x has no eSpeak; no Chinese voices are used).</summary>
    internal static bool Skipped(string relative)
    {
        var first = relative.Split('/')[0];
        if (first is "espeak-ng-data" or "dict")
            return true;
        var name = Path.GetFileName(relative);
        return name.EndsWith(".fst", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".far", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("lexicon-zh.txt", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Unpacks the files of <paramref name="folder"/>/ from a .tar.bz2 into <paramref name="target"/>. Only
    /// regular files and folders under that one top folder; no links, no absolute or "..": anything else is
    /// skipped. Returns the unpacked files and their sizes.
    /// </summary>
    internal static Dictionary<string, long> Extract(string archive, string target, string folder, Action<double>? progress, CancellationToken cancel)
    {
        var files = new Dictionary<string, long>(StringComparer.Ordinal);
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        using var file = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var length = Math.Max(1, file.Length);
        var lastReport = 0L;
        // progress by compressed bytes read, so a large model file moves the bar too
        using var counted = new ReadProgressStream(file, position =>
        {
            if (progress != null && Environment.TickCount64 - lastReport > 100)
            {
                lastReport = Environment.TickCount64;
                progress(Math.Clamp((double)position / length, 0, 1));
            }
        });
        using var bz = new BZip2InputStream(counted);
        using var tar = new TarInputStream(bz);
        TarEntry? entry;
        while ((entry = tar.GetNextEntry()) != null)
        {
            cancel.ThrowIfCancellationRequested();
            var name = entry.Name.Replace('\\', '/');
            while (name.StartsWith("./", StringComparison.Ordinal))
                name = name[2..];
            if (name.Length == 0 || name.StartsWith('/') || name.Split('/').Any(p => p == ".."))
                continue;
            if (!name.StartsWith(folder + "/", StringComparison.Ordinal))
                continue;
            var relative = name[(folder.Length + 1)..].TrimEnd('/');
            if (relative.Length == 0 || Skipped(relative))
                continue;

            var type = entry.TarHeader.TypeFlag;
            var path = Path.GetFullPath(Path.Combine(target, folder, relative));
            if (!path.StartsWith(root, StringComparison.Ordinal))
                continue;
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(path);
                continue;
            }

            if (type != TarHeader.LF_NORMAL && type != TarHeader.LF_OLDNORM)
                continue; // links, devices, pax headers

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                tar.CopyEntryContents(output);
            files[relative] = new FileInfo(path).Length;
        }

        progress?.Invoke(1);
        return files;
    }

    private static void CheckSize(string path, long expected)
    {
        var actual = new FileInfo(path).Length;
        if (expected > 0 && actual != expected)
            throw new InvalidDataException(L.Format("The download is {0} bytes, expected {1}: it is damaged or the voice was changed on the server.", actual, expected));
    }

    private void Set(VoiceInfo voice, VoiceStatus status)
    {
        _status[voice.Id] = status;
        Changed?.Invoke(this, voice.Id);
    }

    public static string FormatSize(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / (1024.0 * 1024 * 1024):0.0} GB"
        : $"{Math.Max(1, (long)Math.Round(bytes / (1024.0 * 1024)))} MB";

    private static long? FreeSpace(string folder)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Info("voice folder delete " + path, ex);
        }
    }

    private sealed class Progress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    /// <summary>A read-only pass-through that reports how far it has read.</summary>
    private sealed class ReadProgressStream(Stream inner, Action<long> read) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            read(inner.Position);
            return n;
        }

        public override int ReadByte()
        {
            var b = inner.ReadByte();
            read(inner.Position);
            return b;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed record VoiceMarker(string Id, string Archive, DateTime Installed, Dictionary<string, long> Files);
}
