using MissionPlanner.ArduPilot;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Firmware;

/// <summary>
/// The live manifest, loaded once per run through <c>APFirmware.GetList</c> (which also appends
/// CubePilot's peripheral manifest). Shared by the Install Firmware and Simulation pages.
/// </summary>
public static class FirmwareManifest
{
    /// <summary>Mission Planner's mirror, used when the main server cannot be reached.</summary>
    public const string MirrorUrl = "https://firmware.oborne.me/manifest.json.gz";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static FirmwareCatalog? _catalog;

    public static FirmwareCatalog? Loaded => _catalog;

    /// <summary>Downloads (or returns) the manifest. Blocking network work runs on the thread pool.</summary>
    /// <exception cref="InvalidOperationException">Neither server answered.</exception>
    public static async Task<FirmwareCatalog> LoadAsync(bool force = false, CancellationToken cancel = default)
    {
        if (!force && _catalog != null)
            return _catalog;

        await Gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            if (!force && _catalog != null)
                return _catalog;

            var catalog = await Task.Run(() =>
            {
                // GetList logs and swallows errors; an empty Manifest is the only sign of failure
                APFirmware.GetList(force: force);
                if (APFirmware.Manifest?.Firmware is not { Length: > 0 })
                    APFirmware.GetList(MirrorUrl, force: true);
                var firmware = APFirmware.Manifest?.Firmware;
                if (firmware is not { Length: > 0 })
                    throw new InvalidOperationException(
                        "Could not download the firmware list from firmware.ardupilot.org. Check the internet connection and try again.");
                return new FirmwareCatalog(firmware);
            }, cancel).ConfigureAwait(false);

            _catalog = catalog;
            return catalog;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>One HttpClient for firmware, simulator and parameter downloads.</summary>
    public static HttpClient Http { get; } = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            var ua = Settings.Instance.UserAgent;
            if (!string.IsNullOrEmpty(ua))
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ua);
        }
        catch (Exception)
        {
            // settings not available: send no user agent
        }

        return client;
    }
}

/// <summary>Downloads a file with progress, to a temporary name first so a cancelled download leaves nothing half-written.</summary>
public static class FileDownloader
{
    /// <param name="progress">Fraction 0..1, or -1 while the size is unknown.</param>
    public static async Task DownloadAsync(HttpClient client, Uri url, string path, IProgress<double>? progress,
        CancellationToken cancel, TimeSpan? stallTimeout = null)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var temp = path + ".part";

        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var stallAfter = stallTimeout ?? TimeSpan.FromSeconds(30);
        stall.CancelAfter(stallAfter);

        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using (var src = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false))
            await using (var dst = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long done = 0;
                var lastReport = 0L;
                progress?.Report(total > 0 ? 0 : -1);
                int n;
                while ((n = await src.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
                {
                    stall.CancelAfter(stallAfter);
                    await dst.WriteAsync(buffer.AsMemory(0, n), stall.Token).ConfigureAwait(false);
                    done += n;
                    if (total > 0 && Environment.TickCount64 - lastReport > 100)
                    {
                        lastReport = Environment.TickCount64;
                        progress?.Report((double)done / total.Value);
                    }
                }

                if (total > 0 && done != total)
                    throw new IOException($"Download ended early ({done} of {total} bytes).");
            }

            File.Move(temp, path, overwrite: true);
            progress?.Report(1);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            TryDelete(temp);
            throw new TimeoutException($"The download from {url.Host} stalled.");
        }
        catch
        {
            TryDelete(temp);
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
}
