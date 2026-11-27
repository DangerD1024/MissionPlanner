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

/// <summary>Where a web address ended up after its redirects, and the size of what is there.</summary>
public sealed record WebProbe(Uri Url, long Length);

/// <summary>Reads GitHub's "latest release" answer and compares versions such as 3.0.4b.</summary>
public static partial class UpdateCheck
{
    /// <summary>Where the releases of Mission Planner 3 are published.</summary>
    public const string Repository = "DangerD1024/MissionPlanner";

    public static string LatestUrl => $"https://api.github.com/repos/{Repository}/releases/latest";

    /// <summary>The release page: redirects to the latest release's tag, and is not under the API's hourly limit.</summary>
    public static string LatestPageUrl => $"https://github.com/{Repository}/releases/latest";

    public static string DownloadUrl(string tag, string name) =>
        $"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(name)}";

    /// <summary>
    /// The latest release without the API, which allows only 60 requests an hour per network without a
    /// GitHub account (several computers behind one router run out): <see cref="LatestPageUrl"/> redirects
    /// to the release's tag, and each installer is at a fixed address named after the version. No notes or
    /// checksum this way; the download is still checked for its size. Installers are only looked for when
    /// the release is newer than <paramref name="current"/>. Null when the answer is not a release.
    /// </summary>
    public static async Task<ReleaseInfo?> FromReleasePageAsync(Func<string, CancellationToken, Task<WebProbe?>> probe,
        IReadOnlyList<string> assetSuffixes, string? current, CancellationToken cancel)
    {
        if (await probe(LatestPageUrl, cancel).ConfigureAwait(false) is not { } latest)
            return null;
        var m = TagPathRegex().Match(latest.Url.AbsolutePath);
        if (!m.Success)
            return null;
        var tag = Uri.UnescapeDataString(m.Groups[1].Value);
        var version = tag.TrimStart('v', 'V');
        if (ParseVersion(version) == null)
            return null;
        var page = latest.Url.GetLeftPart(UriPartial.Path);
        if (current == null || IsNewer(version, current))
        {
            foreach (var suffix in assetSuffixes)
            {
                var name = $"MissionPlanner-{version}-{suffix}";
                var url = DownloadUrl(tag, name);
                if (await probe(url, cancel).ConfigureAwait(false) is { } asset)
                    return new ReleaseInfo(version, page, name, url, asset.Length, null, "");
            }
        }

        return new ReleaseInfo(version, page, null, null, 0, null, "");
    }

    [GeneratedRegex(@"/releases/tag/([^/]+)/?$")]
    private static partial Regex TagPathRegex();

    /// <summary>The installer this computer needs: "win-x64.msi", "linux-x64.tar.gz", "linux-arm64.tar.gz"; null elsewhere.</summary>
    public static string? AssetSuffix(OSPlatform? os = null, Architecture? arch = null) => AssetSuffixes(os, arch).LastOrDefault();

    /// <summary>
    /// The files that update this computer, best first: the in-place update package (files copied over the
    /// installed copy, no installer), then the full installer. Windows: "win-x64-update.zip", "win-x64.msi";
    /// macOS: "osx-arm64-update.zip", "osx-arm64.dmg"; Linux: "linux-x64.tar.gz".
    /// </summary>
    public static IReadOnlyList<string> AssetSuffixes(OSPlatform? os = null, Architecture? arch = null)
    {
        var a = arch ?? RuntimeInformation.OSArchitecture;
        var cpu = a == Architecture.Arm64 ? "arm64" : "x64";
        if (os == OSPlatform.Windows || (os == null && OperatingSystem.IsWindows()))
            return cpu == "x64" ? ["win-x64-update.zip", "win-x64.msi"] : [];
        if (os == OSPlatform.OSX || (os == null && OperatingSystem.IsMacOS()))
            return [$"osx-{cpu}-update.zip", $"osx-{cpu}.dmg"];
        if (os == OSPlatform.Linux || (os == null && OperatingSystem.IsLinux()))
            return [$"linux-{cpu}.tar.gz"];
        return [];
    }

    /// <summary>Reads the release JSON with the first of <paramref name="assetSuffixes"/> the release has.</summary>
    public static ReleaseInfo? ParseBest(string json, IReadOnlyList<string> assetSuffixes)
    {
        ReleaseInfo? first = null;
        foreach (var suffix in assetSuffixes)
        {
            var r = Parse(json, suffix);
            if (r == null)
                return null;
            first ??= r;
            if (r.AssetUrl != null)
                return r;
        }

        return first ?? Parse(json, (string?)null);
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
    private readonly Func<string, CancellationToken, Task<WebProbe?>>? _probe;
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
            },
            async (url, c) =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, url);
                using var response = await DefaultHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, c).ConfigureAwait(false);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return null;
                response.EnsureSuccessStatusCode();
                return new WebProbe(response.RequestMessage?.RequestUri ?? new Uri(url), response.Content.Headers.ContentLength ?? 0);
            })
    {
    }

    /// <summary>
    /// For tests: any way of fetching the release and the file. <paramref name="probe"/> follows a web
    /// address's redirects (null when nothing is there): the way to the release when the API refuses.
    /// </summary>
    public UpdateViewModel(AppServices services, Func<string, CancellationToken, Task<string>> getJson,
        Func<string, Stream, IProgress<long>, CancellationToken, Task> download,
        Func<string, CancellationToken, Task<WebProbe?>>? probe = null)
    {
        _services = services;
        _getJson = getJson;
        _download = download;
        _probe = probe;
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
    [NotifyPropertyChangedFor(nameof(CanDownload), nameof(IsDownloaded), nameof(InstallText))]
    private string? _downloadedFile;

    public bool IsDownloaded => DownloadedFile != null;

    public bool CanDownload => Available?.AssetUrl != null && !IsBusy && DownloadedFile == null;

    public string InstallText => OperatingSystem.IsWindows() || OperatingSystem.IsLinux() ||
                                 (OperatingSystem.IsMacOS() && DownloadedFile?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true)
        ? "Install now"
        : "Show file";

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
            release = UpdateCheck.ParseBest(json, UpdateCheck.AssetSuffixes());
            if (release == null)
                error = "The release information could not be read.";
        }
        catch (Exception ex) when (IsCheckFailure(ex))
        {
            error = Describe(ex);
        }

        // the API allows 60 requests an hour per network without a GitHub account; the release page has no such limit
        if (release == null && _probe != null)
        {
            try
            {
                release = await UpdateCheck.FromReleasePageAsync(_probe, UpdateCheck.AssetSuffixes(), CurrentVersion, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (IsCheckFailure(ex))
            {
                // the API's reason is the one to show
            }
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

    private static bool IsCheckFailure(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or JsonException or IOException or InvalidOperationException or UriFormatException;

    private static string Describe(Exception ex) => ex is HttpRequestException
    {
        StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests
    }
        ? "GitHub is turning away update checks from this network for now (too many requests); try again later."
        : ex.Message;

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
            if (OperatingSystem.IsWindows() && file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var target = Path.GetDirectoryName(Environment.ProcessPath)!.TrimEnd(Path.DirectorySeparatorChar);
                var admin = !CanWrite(target);
                if (!await _services.Dialogs.ConfirmAsync("Install update",
                        "Mission Planner will close, copy the new files over the current ones and start again. Settings and logs are kept." +
                        (admin ? "\n\nWindows asks for administrator rights to write to " + target + "." : ""), "Install"))
                    return;
                var script = StageWindowsUpdate(file, target, Environment.ProcessId, admin);
                using var _ = Process.Start(new ProcessStartInfo("powershell.exe")
                {
                    ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script },
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                ExitRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (OperatingSystem.IsMacOS() && file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                     MacBundlePath(Environment.ProcessPath) is { } bundle)
            {
                if (!await _services.Dialogs.ConfirmAsync("Install update",
                        "Mission Planner will close, replace the app with the new version and start again. Settings and logs are kept.", "Install"))
                    return;
                var script = Path.Combine(Path.GetTempPath(), "mp-update-" + Guid.NewGuid().ToString("N") + ".sh");
                File.WriteAllText(script, MacUpdateScript(file, bundle, Environment.ProcessId));
                using var _ = Process.Start(new ProcessStartInfo("/bin/sh") { ArgumentList = { script }, UseShellExecute = false });
                ExitRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (OperatingSystem.IsWindows() && file.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
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

    /// <summary>
    /// Windows: unpacks the update zip next to the temp folder and writes a PowerShell updater that waits
    /// for this process (<paramref name="pid"/>) to exit, copies the files over <paramref name="targetDir"/>
    /// (elevated when <paramref name="admin"/>, i.e. Program Files), and starts the app again as the user.
    /// Returns the script path. Static so it can be tested without launching anything.
    /// </summary>
    public static string StageWindowsUpdate(string zip, string targetDir, int pid, bool admin)
    {
        targetDir = targetDir.TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.Combine(Path.GetTempPath(), "mp-update-" + Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(root, "files");
        Directory.CreateDirectory(staging);
        ZipFile.ExtractToDirectory(zip, staging, overwriteFiles: true);
        var src = FindAppRoot(staging);
        var exe = Path.Combine(targetDir, Path.GetFileName(Environment.ProcessPath ?? "MissionPlanner.App.exe"));
        var copy = Path.Combine(root, "copy.ps1");
        var log = Path.Combine(root, "update.log");
        File.WriteAllText(copy, WindowsCopyScript(src, targetDir, log));
        var script = Path.Combine(root, "update.ps1");
        File.WriteAllText(script, WindowsUpdateScript(copy, exe, pid, admin));
        return script;
    }

    private static string Ps(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>Copies the unpacked files over the installed ones (robocopy: retries files still in use for a moment).</summary>
    internal static string WindowsCopyScript(string src, string targetDir, string log) =>
        "$ErrorActionPreference = 'Continue'\r\n" +
        $"robocopy {Ps(src)} {Ps(targetDir)} /E /R:10 /W:1 /NP /LOG:{Ps(log)} | Out-Null\r\n" +
        // robocopy: 0-7 success (files copied / extra / mismatched), 8 and above failures
        "exit ([int]($LASTEXITCODE -ge 8))\r\n";

    /// <summary>Waits for the app to exit, runs the copy (elevated if needed) and starts the app as the user.</summary>
    internal static string WindowsUpdateScript(string copyScript, string exe, int pid, bool admin)
    {
        var run = admin
            ? $"$p = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -WindowStyle Hidden -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',{Ps(copyScript)})\r\n$ok = $p -and $p.ExitCode -eq 0\r\n"
            : $"& powershell.exe -NoProfile -ExecutionPolicy Bypass -File {Ps(copyScript)}\r\n$ok = $LASTEXITCODE -eq 0\r\n";
        return
            $"try {{ Wait-Process -Id {pid} -Timeout 60 -ErrorAction SilentlyContinue }} catch {{ }}\r\n" +
            "Start-Sleep -Milliseconds 500\r\n" +
            "try {\r\n" + run + "} catch { $ok = $false }\r\n" +
            "if (-not $ok) { Add-Type -AssemblyName PresentationFramework; [System.Windows.MessageBox]::Show('The update could not be copied into place. Mission Planner starts in its previous version; install the update with the installer from the release page instead.', 'Mission Planner update') | Out-Null }\r\n" +
            $"Start-Process -FilePath {Ps(exe)}\r\n";
    }

    /// <summary>The .app bundle around the running executable (…/Mission Planner 3.app/Contents/MacOS/MissionPlanner.App), or null.</summary>
    public static string? MacBundlePath(string? processPath)
    {
        // POSIX paths, handled as text so this behaves the same on every OS (and in tests on Windows)
        var dir = processPath;
        while (dir != null && dir.LastIndexOf('/') is var cut and > 0)
        {
            dir = dir[..cut];
            if (dir.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                return dir;
        }

        return null;
    }

    private static string Sh(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>
    /// macOS: waits for the app to exit, unpacks the update zip (made with ditto, so permissions survive),
    /// swaps the new bundle in for <paramref name="bundle"/> (asking for an administrator password only when
    /// its folder is not writable), signs it ad hoc again (changed files break the old signature), clears
    /// the quarantine flag and opens it. Falls back to the old bundle if anything fails.
    /// </summary>
    public static string MacUpdateScript(string zip, string bundle, int pid)
    {
        var cut = bundle.LastIndexOf('/');
        var name = bundle[(cut + 1)..];
        var parent = cut > 0 ? bundle[..cut] : "/";
        return
            "#!/bin/sh\n" +
            $"while kill -0 {pid} 2>/dev/null; do sleep 0.5; done\n" +
            "stage=$(mktemp -d /tmp/mp-update.XXXXXX)\n" +
            $"ditto -x -k {Sh(zip)} \"$stage\" || {{ open {Sh(bundle)}; exit 1; }}\n" +
            $"new=\"$stage/{name}\"\n" +
            "[ -d \"$new\" ] || new=$(find \"$stage\" -maxdepth 2 -name '*.app' -type d | head -1)\n" +
            $"[ -n \"$new\" ] && [ -d \"$new\" ] || {{ open {Sh(bundle)}; exit 1; }}\n" +
            "codesign --force --deep -s - \"$new\" >/dev/null 2>&1\n" +
            "xattr -cr \"$new\" 2>/dev/null\n" +
            // the swap in a file of its own: the administrator prompt then runs one plain command (mktemp paths have no quotes)
            "cat > \"$stage/swap.sh\" <<EOF\n" +
            $"rm -rf {Sh(bundle + ".old")} && mv {Sh(bundle)} {Sh(bundle + ".old")} && mv \"$new\" {Sh(bundle)} && rm -rf {Sh(bundle + ".old")}\n" +
            "EOF\n" +
            $"if [ -w {Sh(parent)} ]; then sh \"$stage/swap.sh\"; else osascript -e \"do shell script \\\"sh $stage/swap.sh\\\" with administrator privileges\"; fi\n" +
            $"[ -d {Sh(bundle)} ] || mv {Sh(bundle + ".old")} {Sh(bundle)} 2>/dev/null\n" +
            "rm -rf \"$stage\"\n" +
            $"open {Sh(bundle)}\n";
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".mp-write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
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
