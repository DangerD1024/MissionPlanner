using System.Net;
using FirmwareInfo = MissionPlanner.ArduPilot.APFirmware.FirmwareInfo;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Firmware;

/// <summary>
/// Boards in STM32 DFU mode found without opening anything: on Linux the USB devices under
/// /sys/bus/usb/devices with vendor 0483, product df11 (only sysfs attribute files are read).
/// </summary>
public static class DfuDeviceScanner
{
    public const string SysfsUsbDevices = "/sys/bus/usb/devices";

    /// <summary>Reads a sysfs tree (a parameter so tests can build one).</summary>
    public static IReadOnlyList<DfuDevice> ScanSysfs(string root = SysfsUsbDevices)
    {
        var result = new List<DfuDevice>();
        IEnumerable<string> entries;
        try
        {
            if (!Directory.Exists(root))
                return result;
            entries = Directory.EnumerateFileSystemEntries(root).ToList();
        }
        catch (Exception)
        {
            return result;
        }

        foreach (var dir in entries.OrderBy(e => e, StringComparer.Ordinal))
        {
            try
            {
                var vid = ReadAttr(dir, "idVendor");
                var pid = ReadAttr(dir, "idProduct");
                if (vid == null || pid == null || UsbId.Parse(vid + ":" + pid) != DfuFlasher.StmDfu)
                    continue;
                var bus = ReadAttr(dir, "busnum");
                var dev = ReadAttr(dir, "devnum");
                var product = ReadAttr(dir, "product");
                result.Add(new DfuDevice(DfuFlasher.StmDfu,
                    string.IsNullOrWhiteSpace(product) ? "STM32 DFU" : string.Join(' ', product.Split(' ', StringSplitOptions.RemoveEmptyEntries)),
                    ReadAttr(dir, "serial"),
                    bus != null && dev != null ? $"USB bus {bus} device {dev}" : Path.GetFileName(dir)));
            }
            catch (Exception)
            {
                // unplugged while scanning
            }
        }

        return result;
    }

    private static string? ReadAttr(string dir, string name)
    {
        var p = Path.Combine(dir, name);
        if (!File.Exists(p))
            return null;
        var text = File.ReadAllText(p).Trim();
        return text.Length == 0 ? null : text;
    }
}

/// <summary>Where the bootloader + firmware image for a manifest entry is.</summary>
public sealed record DfuHexSource(Uri Url, bool FromManifest);

/// <summary>
/// The "_with_bl.hex" ArduPilot builds beside each ChibiOS .apj: the ArduPilot bootloader and the
/// firmware in one image from 0x08000000, for a board without ArduPilot's bootloader (iNav,
/// Betaflight, a blank chip) in STM32 DFU mode. The manifest lists it (format "hex", no board id);
/// when it does not, the name is derived from the .apj and checked on the server.
/// </summary>
public static class DfuHexLocator
{
    public const string Suffix = "_with_bl.hex";

    /// <summary>"…/Plane/stable/MatekH743/arduplane.apj" → "…/Plane/stable/MatekH743/arduplane_with_bl.hex".</summary>
    public static Uri? DerivedUrl(Uri? firmwareUrl)
    {
        if (firmwareUrl == null)
            return null;
        var name = Path.GetFileName(firmwareUrl.AbsolutePath);
        if (name.Length == 0)
            return null;
        if (name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
            return firmwareUrl;
        var stem = Path.GetFileNameWithoutExtension(name);
        return new Uri(firmwareUrl, Uri.EscapeDataString(stem + Suffix));
    }

    /// <summary>The manifest's hex entry for the same build: same folder, platform, vehicle and release (and git commit when both have one).</summary>
    public static FirmwareInfo? FromManifest(IEnumerable<FirmwareInfo> manifest, FirmwareInfo firmware)
    {
        if (firmware.Url == null)
            return null;
        var folder = Folder(firmware.Url);
        var expected = DerivedUrl(firmware.Url)!.AbsoluteUri;
        var hits = manifest.Where(f => f.Url != null &&
                                       string.Equals(f.Format, "hex", StringComparison.OrdinalIgnoreCase) &&
                                       f.Url.AbsolutePath.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase) &&
                                       Folder(f.Url) == folder &&
                                       f.Platform == firmware.Platform &&
                                       f.MavType == firmware.MavType &&
                                       f.MavFirmwareVersionType == firmware.MavFirmwareVersionType &&
                                       (string.IsNullOrEmpty(f.GitSha) || string.IsNullOrEmpty(firmware.GitSha) || f.GitSha == firmware.GitSha))
            .ToList();
        return hits.FirstOrDefault(f => f.Url!.AbsoluteUri == expected) ?? (hits.Count == 1 ? hits[0] : null);
    }

    /// <summary>The manifest's entry, else the derived name if the server has it (<paramref name="exists"/>: an HTTP HEAD).</summary>
    public static async Task<DfuHexSource?> FindAsync(IEnumerable<FirmwareInfo>? manifest, FirmwareInfo firmware,
        Func<Uri, CancellationToken, Task<bool>> exists, CancellationToken cancel)
    {
        if (manifest != null && FromManifest(manifest, firmware) is { Url: { } url })
            return new DfuHexSource(url, true);
        var derived = DerivedUrl(firmware.Url);
        if (derived != null && await exists(derived, cancel).ConfigureAwait(false))
            return new DfuHexSource(derived, false);
        return null;
    }

    /// <summary>HTTP HEAD: true for a 2xx answer, false for 404 and other failures.</summary>
    public static async Task<bool> ExistsAsync(HttpClient http, Uri url, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return false;
        }
    }

    private static string Folder(Uri url) => new Uri(url, ".").AbsoluteUri;
}

/// <summary>What the DFU install talks to: dfu-util, the device list and the network. Replaced in tests.</summary>
public sealed class DfuEnvironment
{
    public required IProcessRunner Runner { get; init; }

    public required DfuUtilLocator Locator { get; init; }

    /// <summary>Boards in DFU mode found without running dfu-util (Linux sysfs); null where only <c>dfu-util -l</c> can tell.</summary>
    public Func<IReadOnlyList<DfuDevice>>? ScanDevices { get; init; }

    /// <summary>Downloads a URL to a file: (url, path, progress 0..1, cancel).</summary>
    public required Func<Uri, string, IProgress<double>?, CancellationToken, Task> Download { get; init; }

    /// <summary>Whether a URL exists on the server (HTTP HEAD).</summary>
    public required Func<Uri, CancellationToken, Task<bool>> UrlExists { get; init; }

    public HostOs Os => Locator.Os;

    public static DfuEnvironment ForThisMachine() => new()
    {
        Runner = SystemProcessRunner.Instance,
        Locator = DfuUtilLocator.ForThisMachine(),
        ScanDevices = OperatingSystem.IsLinux() ? () => DfuDeviceScanner.ScanSysfs() : null,
        Download = (url, path, progress, cancel) => FileDownloader.DownloadAsync(FirmwareManifest.Http, url, path, progress, cancel),
        UrlExists = (url, cancel) => DfuHexLocator.ExistsAsync(FirmwareManifest.Http, url, cancel),
    };

    /// <summary>For tests that do not use DFU: no dfu-util, no device, no network, nothing is run.</summary>
    public static DfuEnvironment None => new()
    {
        Runner = new NoProcessRunner(),
        Locator = new DfuUtilLocator(HostOs.Other, System.Runtime.InteropServices.Architecture.X64, "", "", _ => false),
        ScanDevices = () => [],
        Download = (_, _, _, _) => throw new HttpRequestException("no network in tests", null, HttpStatusCode.ServiceUnavailable),
        UrlExists = (_, _) => Task.FromResult(false),
    };

    private sealed class NoProcessRunner : IProcessRunner
    {
        public Task<int> RunAsync(ProcessRequest request, Action<string, bool> output, CancellationToken cancel) =>
            throw new System.ComponentModel.Win32Exception("no programs are run here");
    }
}
