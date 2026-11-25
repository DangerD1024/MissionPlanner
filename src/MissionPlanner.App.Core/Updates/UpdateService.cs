using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.Services;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.Updates;

/// <summary>A published release: its version, page, and the installer for this computer (if there is one).</summary>
public sealed record ReleaseInfo(string Version, string PageUrl, string? AssetName, string? AssetUrl, long AssetSize, string? Sha256, string Notes);

/// <summary>Reads GitHub's "latest release" answer and compares versions such as 3.0.4b.</summary>
public static partial class UpdateCheck
{
    /// <summary>Where the releases of Mission Planner 3 are published.</summary>
    public const string Repository = "DangerD1024/MissionPlanner";

    public static string LatestUrl => $"https://api.github.com/repos/{Repository}/releases/latest";

    /// <summary>The installer this computer needs: "win-x64.msi", "linux-x64.tar.gz", "linux-arm64.tar.gz"; null elsewhere.</summary>
    public static string? AssetSuffix(OSPlatform? os = null, Architecture? arch = null)
    {
        var a = arch ?? RuntimeInformation.OSArchitecture;
        var cpu = a == Architecture.Arm64 ? "arm64" : "x64";
        if (os == OSPlatform.Windows || (os == null && OperatingSystem.IsWindows()))
            return cpu == "x64" ? "win-x64.msi" : null;
        if (os == OSPlatform.Linux || (os == null && OperatingSystem.IsLinux()))
            return $"linux-{cpu}.tar.gz";
        return null;
    }

    /// <summary>Reads the release JSON; prefers the versioned installer (MissionPlanner-3.0.4b-win-x64.msi) over the unversioned copy.</summary>
    public static ReleaseInfo? Parse(string json, string? assetSuffix)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("tag_name", out var tag))
            return null;
        if (r.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
            return null;
        var version = (tag.GetString() ?? "").TrimStart('v', 'V');
        if (ParseVersion(version) == null)
            return null;
        var page = r.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : $"https://github.com/{Repository}/releases";
        var notes = r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

        string? name = null, url = null, sha = null;
        long size = 0;
        if (assetSuffix != null && r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            var matches = assets.EnumerateArray()
                .Where(x => (x.GetProperty("name").GetString() ?? "").EndsWith("-" + assetSuffix, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => (x.GetProperty("name").GetString() ?? "").Contains(version, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count > 0)
            {
                var a = matches[0];
                name = a.GetProperty("name").GetString();
                url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
                if (a.TryGetProperty("digest", out var d) && d.GetString() is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    sha = digest[7..].ToLowerInvariant();
            }
        }

        return new ReleaseInfo(version, page, name, url, size, sha, notes);
    }

    /// <summary>"3.0.4b" → (3.0.4, "b"); a release without a letter is newer than its betas.</summary>
    public static (Version Number, string Suffix)? ParseVersion(string text)
    {
        var m = VersionRegex().Match(text.Trim().TrimStart('v', 'V'));
        if (!m.Success || !Version.TryParse(m.Groups[1].Value.Contains('.') ? m.Groups[1].Value : m.Groups[1].Value + ".0", out var v))
            return null;
        return (Normalise(v), m.Groups[2].Value.ToLowerInvariant());
    }

    /// <summary>True when <paramref name="remote"/> is a later version than <paramref name="local"/>.</summary>
    public static bool IsNewer(string remote, string local)
    {
        if (ParseVersion(remote) is not { } r || ParseVersion(local) is not { } l)
            return false;
        var c = r.Number.CompareTo(l.Number);
        if (c != 0)
            return c > 0;
        if (r.Suffix == l.Suffix)
            return false;
        if (r.Suffix.Length == 0)
            return true; // 3.0.4 after 3.0.4b
        if (l.Suffix.Length == 0)
            return false;
        return string.CompareOrdinal(r.Suffix, l.Suffix) > 0;
    }

    private static Version Normalise(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    [GeneratedRegex(@"^(\d+(?:\.\d+){0,3})([a-zA-Z]*)")]
    private static partial Regex VersionRegex();
}

/// <summary>
/// The update check: at startup (unless switched off) it asks GitHub for the latest release, and
/// when it is newer offers to download the installer and start it (Windows) or show it (Linux).
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    public const string CheckKey = "next_update_check";
    public const string SkipKey = "next_update_skip";

    private static readonly HttpClient DefaultHttp = CreateClient();

    private readonly AppServices _services;
    private readonly Func<string, CancellationToken, Task<string>> _getJson;
    private readonly Func<string, Stream, IProgress<long>, CancellationToken, Task> _download;
    private CancellationTokenSource? _downloadCancel;

    public UpdateViewModel(AppServices services)
        : this(services,
            (url, c) => DefaultHttp.GetStringAsync(url, c),
            async (url, target, progress, c) =>
            {
                using var response = await DefaultHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, c).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var src = await response.Content.ReadAsStreamAsync(c).ConfigureAwait(false);
                var buffer = new byte[81920];
                long total = 0;
                int n;
                while ((n = await src.ReadAsync(buffer, c).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, n), c).ConfigureAwait(false);
                    total += n;
                    progress.Report(total);
                }
            })
    {
    }

    /// <summary>For tests: any way of fetching the release and the file.</summary>
    public UpdateViewModel(AppServices services, Func<string, CancellationToken, Task<string>> getJson,
        Func<string, Stream, IProgress<long>, CancellationToken, Task> download)
    {
        _services = services;
        _getJson = getJson;
        _download = download;
        _checkAtStartup = Settings.Instance.GetBoolean(CheckKey, true);
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // GitHub's API refuses requests without a user agent
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MissionPlanner/" + Regex.Replace(AppVersion.Display, "[^0-9A-Za-z.]", ""));
        return c;
    }

    public string CurrentVersion { get; set; } = AppVersion.Display;

    /// <summary>Where downloads go (the user's Downloads folder).</summary>
    public string DownloadFolder { get; set; } = DefaultDownloadFolder();

    [ObservableProperty]
    private bool _checkAtStartup;

    partial void OnCheckAtStartupChanged(bool value) => Settings.Instance[CheckKey] = value.ToString();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BannerText), nameof(CanDownload))]
    private ReleaseInfo? _available;

    /// <summary>The banner at the top of the window shows while this is set.</summary>
    [ObservableProperty]
    private bool _isBannerVisible;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private double _downloadPercent;

    /// <summary>The downloaded installer, once it is complete and verified.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload), nameof(IsDownloaded))]
    private string? _downloadedFile;

    public bool IsDownloaded => DownloadedFile != null;

    public bool CanDownload => Available?.AssetUrl != null && !IsBusy && DownloadedFile == null;

    public string InstallText => OperatingSystem.IsWindows() || OperatingSystem.IsLinux() ? "Install now" : "Show file";

    public string BannerText => Available is { } r
        ? $"Mission Planner {r.Version} is available (you have {CurrentVersion})."
        : "";

    /// <summary>Raised when the installer has started and the app should close so it can replace the files.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>The startup check: quiet, and only when enabled; a skipped version is not offered again.</summary>
    public async Task CheckAtStartupAsync()
    {
        if (!CheckAtStartup)
            return;
        await CheckAsync(manual: false).ConfigureAwait(false);
    }

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckNow() => CheckAsync(manual: true);

    private bool CanCheck() => !IsBusy;

    public async Task CheckAsync(bool manual)
    {
        await _services.Dispatcher.InvokeAsync(() =>
        {
            IsBusy = true;
            StatusText = "Checking for updates…";
        }).ConfigureAwait(false);

        ReleaseInfo? release = null;
        string? error = null;
        try
        {
            var json = await _getJson(UpdateCheck.LatestUrl, CancellationToken.None).ConfigureAwait(false);
            release = UpdateCheck.Parse(json, UpdateCheck.AssetSuffix());
            if (release == null)
                error = "The release information could not be read.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException or InvalidOperationException)
        {
            error = ex.Message;
        }

        await _services.Dispatcher.InvokeAsync(() =>
        {
            IsBusy = false;
            if (release == null)
            {
                StatusText = "Could not check for updates: " + error;
                return;
            }

            if (!UpdateCheck.IsNewer(release.Version, CurrentVersion))
            {
                Available = null;
                IsBannerVisible = false;
                StatusText = $"You have the latest version ({CurrentVersion}).";
                return;
            }

            Available = release;
            StatusText = release.AssetUrl != null
                ? $"Version {release.Version} is available."
                : $"Version {release.Version} is available (no installer for this computer; see the release page).";
            if (DownloadedFile != null && !DownloadedFile.Contains(release.Version, StringComparison.OrdinalIgnoreCase))
                DownloadedFile = null;
            IsBannerVisible = manual || Settings.Instance[SkipKey] != release.Version;
        }).ConfigureAwait(false);
    }

    [RelayCommand]
    private Task Download() => DownloadAsync();

    public async Task DownloadAsync()
    {
        if (Available is not { AssetUrl: { } url, AssetName: { } name } release || IsBusy)
            return;
        IsBusy = true;
        IsDownloading = true;
        DownloadPercent = 0;
        StatusText = $"Downloading {name}…";
        _downloadCancel = new CancellationTokenSource();
        Directory.CreateDirectory(DownloadFolder);
        var target = Path.Combine(DownloadFolder, name);
        var partial = target + ".part";
        string? error = null;
        try
        {
            var progress = new SyncProgress(n => _services.Dispatcher.Post(() =>
            {
                if (release.AssetSize > 0)
                    DownloadPercent = Math.Min(100, n * 100.0 / release.AssetSize);
            }));
            await using (var file = File.Create(partial))
                await _download(url, file, progress, _downloadCancel.Token).ConfigureAwait(true);

            if (release.AssetSize > 0 && new FileInfo(partial).Length != release.AssetSize)
                error = "the download is incomplete";
            else if (release.Sha256 != null && !string.Equals(await HashAsync(partial).ConfigureAwait(true), release.Sha256, StringComparison.OrdinalIgnoreCase))
                error = "the file is damaged (checksum mismatch)";
            if (error == null)
            {
                File.Move(partial, target, overwrite: true);
                DownloadedFile = target;
                DownloadPercent = 100;
                StatusText = $"Downloaded to {target}";
            }
        }
        catch (OperationCanceledException)
        {
            error = "cancelled";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
        }
        finally
        {
            IsBusy = false;
            IsDownloading = false;
            _downloadCancel.Dispose();
            _downloadCancel = null;
        }

        if (error != null)
        {
            TryDelete(partial);
            StatusText = "Download failed: " + error;
        }
    }

    [RelayCommand]
    private void CancelDownload() => _downloadCancel?.Cancel();

    /// <summary>Windows: starts the MSI and closes the app so it can be replaced.
    /// Linux: unpacks the tar.gz over the current files (via a detached updater) and restarts.</summary>
    [RelayCommand]
    private async Task Install()
    {
        if (DownloadedFile is not { } file || !File.Exists(file))
            return;
        try
        {
            if (OperatingSystem.IsWindows() && file.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            {
                if (!await _services.Dialogs.ConfirmAsync("Install update",
                        "Mission Planner will close and the installer will start. Settings and logs are kept.", "Install"))
                    return;
                using var _ = Process.Start(new ProcessStartInfo("msiexec.exe") { ArgumentList = { "/i", file }, UseShellExecute = true });
                ExitRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (OperatingSystem.IsLinux() && file.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                if (!await _services.Dialogs.ConfirmAsync("Install update",
                        "Mission Planner will close, unpack the update over the current files and restart. Settings and logs are kept.", "Install"))
                    return;
                var target = Path.GetDirectoryName(Environment.ProcessPath)!.TrimEnd(Path.DirectorySeparatorChar);
                var script = StageLinuxUpdate(file, target);
                using var _ = Process.Start(new ProcessStartInfo("/bin/sh") { ArgumentList = { script }, UseShellExecute = false });
                ExitRequested?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                using var _ = Process.Start(new ProcessStartInfo { FileName = Path.GetDirectoryName(file)!, UseShellExecute = true });
                StatusText = $"Unpack {Path.GetFileName(file)} over your current copy (or next to it) and start it from there.";
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            await _services.Dialogs.ShowMessageAsync("Install update", "Could not start it: " + ex.Message + "\n\n" + file, MessageKind.Error);
        }
    }

    /// <summary>Linux: extract the tar.gz to a staging dir and write an updater script that waits
    /// for this process to exit, copies the new files over <paramref name="targetDir"/>, and relaunches.
    /// Returns the script path. Public/static so it can be tested without launching anything.</summary>
    public static string StageLinuxUpdate(string tarGz, string targetDir)
    {
        targetDir = targetDir.TrimEnd(Path.DirectorySeparatorChar);
        var staging = Path.Combine(Path.GetDirectoryName(targetDir)!, ".mp-update-staged");
        if (Directory.Exists(staging))
            Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        using (var fs = File.OpenRead(tarGz))
        using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            TarFile.ExtractToDirectory(gz, staging, overwriteFiles: true);
        var src = FindAppRoot(staging);
        var exeName = Path.GetFileName(Environment.ProcessPath ?? "MissionPlanner.App");
        var exe = Path.Combine(targetDir, exeName);
        var script = Path.Combine(Path.GetTempPath(), "mp-update-" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(script,
            "#!/bin/sh\n" +
            "sleep 2\n" +
            $"cp -a '{src}/.' '{targetDir}/'\n" +
            $"chmod +x '{exe}' 2>/dev/null\n" +
            $"nohup '{exe}' >/dev/null 2>&1 &\n");
        return script;
    }

    /// <summary>The extracted dir that actually holds the app (tarballs may wrap it in one or more folders).</summary>
    private static string FindAppRoot(string dir)
    {
        if (File.Exists(Path.Combine(dir, "MissionPlanner.App.dll")))
            return dir;
        foreach (var sub in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories))
            if (File.Exists(Path.Combine(sub, "MissionPlanner.App.dll")))
                return sub;
        return dir;
    }

    [RelayCommand]
    private void OpenReleasePage()
    {
        if (Available?.PageUrl is not { Length: > 0 } url)
            return;
        try
        {
            using var _ = Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            StatusText = url;
        }
    }

    /// <summary>Later: hides the banner until the next start.</summary>
    [RelayCommand]
    private void Later() => IsBannerVisible = false;

    /// <summary>Skip this version: not offered again at startup (Help > Check for updates still shows it).</summary>
    [RelayCommand]
    private void Skip()
    {
        if (Available != null)
            Settings.Instance[SkipKey] = Available.Version;
        IsBannerVisible = false;
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var s = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(s).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string DefaultDownloadFolder()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(home, "Downloads");
        return Directory.Exists(downloads) ? downloads : Path.Combine(Settings.GetUserDataDirectory(), "updates");
    }

    /// <summary>Reports on the calling thread (Progress&lt;T&gt; would post to a context the download thread lacks).</summary>
    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
