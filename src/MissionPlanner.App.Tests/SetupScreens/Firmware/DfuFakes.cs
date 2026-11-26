using System.Runtime.InteropServices;
using System.Text;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Firmware;

namespace MissionPlanner.App.Tests.SetupScreens.Firmware;

/// <summary>
/// Stands in for dfu-util: answers --version, -l and downloads from a script, printing output as
/// dfu-util 0.11 does (progress bars redrawn with '\r'). Never starts a program.
/// </summary>
internal sealed class FakeDfuRunner : IProcessRunner
{
    public List<ProcessRequest> Requests { get; } = new();

    /// <summary>The image dfu-util was given with -D, read while it "ran".</summary>
    public byte[]? Flashed { get; private set; }

    public string? FlashedPath { get; private set; }

    /// <summary>Paths whose --version fails to start (a bundled binary that cannot run).</summary>
    public HashSet<string> Broken { get; } = new(StringComparer.Ordinal);

    /// <summary>What a download prints and returns; the default is a clean flash with the leave reset error.</summary>
    public Func<ProcessRequest, Action<string, bool>, CancellationToken, Task<int>>? OnDownload { get; set; }

    public string ListOutput { get; set; } = "";

    public bool Started { get; private set; }

    public Task<int> RunAsync(ProcessRequest request, Action<string, bool> output, CancellationToken cancel)
    {
        lock (Requests)
            Requests.Add(request);
        if (request.Arguments is ["--version"])
        {
            if (Broken.Contains(request.FileName))
                throw new System.ComponentModel.Win32Exception("Bad CPU type in executable");
            Print(output, false, Banner);
            return Task.FromResult(0);
        }

        if (request.Arguments.Contains("-l"))
        {
            Print(output, false, Banner + ListOutput);
            return Task.FromResult(0);
        }

        var d = request.Arguments.ToList().IndexOf("-D");
        if (d >= 0)
        {
            FlashedPath = request.Arguments[d + 1];
            Flashed = File.ReadAllBytes(FlashedPath);
        }

        Started = true;
        return (OnDownload ?? CleanFlashWithLeaveError)(request, output, cancel);
    }

    public IReadOnlyList<ProcessRequest> Downloads => Requests.Where(r => r.Arguments.Contains("-D")).ToList();

    public const string Banner = """
        dfu-util 0.11

        Copyright 2005-2009 Weston Schmidt, Harald Welte and OpenMoko Inc.
        Copyright 2010-2021 Tormod Volden and Stefan Schmidt
        This program is Free Software and has ABSOLUTELY NO WARRANTY
        Please report bugs to http://sourceforge.net/p/dfu-util/tickets/


        """;

    /// <summary>dfu-util 0.11 writing an image to an STM32H743, as dfuse.c and dfu_file.c print it.</summary>
    public static string DownloadTranscript(int size)
    {
        var sb = new StringBuilder();
        sb.Append("Opening DFU capable USB device...\nDevice ID 0483:df11\nDevice DFU version 011a\nClaiming USB DFU Interface...\n");
        sb.Append("Setting Alternate Interface #0 ...\nDetermining device status...\n");
        sb.Append("DFU state(2) = dfuIDLE, status(0) = No error condition is present\nDFU mode device DFU version 011a\n");
        sb.Append("Device returned transfer size 1024\nDfuSe interface name: \"Internal Flash   \"\n");
        sb.Append($"Downloading element to address = 0x08000000, size = {size}\n");
        foreach (var stage in new[] { "Erase   ", "Download" })
        {
            foreach (var p in new[] { 0, 20, 40, 60, 80, 100 })
                sb.Append($"\r{stage}\t[{new string('=', p / 4)}{new string(' ', 25 - p / 4)}] {p,3}% {size * p / 100,12} bytes");
            sb.Append($"\n{stage} done.\n");
        }

        sb.Append("File downloaded successfully\nSubmitting leave request...\nTransitioning to dfuMANIFEST state\n");
        return sb.ToString();
    }

    public static Task<int> CleanFlashWithLeaveError(ProcessRequest request, Action<string, bool> output, CancellationToken cancel)
    {
        Print(output, false, Banner);
        Print(output, true, "dfu-util: Warning: Invalid DFU suffix signature\ndfu-util: A valid DFU suffix will be required in a future dfu-util release\n");
        Print(output, false, DownloadTranscript(4096));
        // the board resets as soon as it is told to leave
        Print(output, true, "dfu-util: Error during download get_status\n");
        return Task.FromResult(74);
    }

    /// <summary>Prints like a process: raw text split into lines at '\r' and '\n'.</summary>
    public static void Print(Action<string, bool> output, bool error, string text)
    {
        var splitter = new LineSplitter(l => output(l, error));
        splitter.Add(text);
        splitter.Flush();
    }
}

internal static class DfuTestSupport
{
    public const string AppDir = "/opt/mp/Tools/dfu-util";

    public static string BundledLinux => Path.Combine(AppDir, "dfu-util");

    /// <summary>A Linux x64 machine with the bundled dfu-util and nothing on PATH.</summary>
    public static DfuUtilLocator LinuxLocator(params string[] alsoExisting) =>
        new(HostOs.Linux, Architecture.X64, AppDir, "/usr/bin:/bin", p => p == BundledLinux || alsoExisting.Contains(p));

    public static DfuEnvironment Environment(FakeDfuRunner runner, DfuUtilLocator? locator = null,
        Func<IReadOnlyList<DfuDevice>>? devices = null, Func<Uri, string, Task>? download = null, Func<Uri, bool>? exists = null,
        List<Uri>? downloads = null, List<Uri>? heads = null) => new()
    {
        Runner = runner,
        Locator = locator ?? LinuxLocator(),
        ScanDevices = devices ?? (() => [OneBoard]),
        Download = async (url, path, progress, cancel) =>
        {
            downloads?.Add(url);
            progress?.Report(0.5);
            if (download == null)
                throw new HttpRequestException("no download in this test");
            await download(url, path);
            progress?.Report(1);
        },
        UrlExists = (url, _) =>
        {
            heads?.Add(url);
            return Task.FromResult(exists?.Invoke(url) ?? false);
        },
    };

    public static readonly DfuDevice OneBoard = new(DfuFlasher.StmDfu, "STM32 BOOTLOADER", "357A35673133", "USB bus 1 device 7");

    /// <summary>One Intel HEX record with its checksum.</summary>
    public static string Record(byte type, ushort offset, params byte[] data)
    {
        var bytes = new List<byte> { (byte)data.Length, (byte)(offset >> 8), (byte)offset, type };
        bytes.AddRange(data);
        var sum = 0;
        foreach (var b in bytes)
            sum += b;
        bytes.Add((byte)(-sum & 0xFF));
        return ":" + Convert.ToHexString(bytes.ToArray()) + "\n";
    }

    public static string Linear(ushort upper) => Record(0x04, 0, (byte)(upper >> 8), (byte)upper);

    public const string End = ":00000001FF\n";

    /// <summary>
    /// A small "_with_bl.hex": bootloader bytes at 0x08000000, the firmware at 0x08020000 (as on an H7,
    /// whose first 128 KB sector holds the bootloader), 16 bytes per data record like ArduPilot's.
    /// </summary>
    public static (string Hex, byte[] Image) WithBootloader(int bootloaderSize = 64, int firmwareSize = 96)
    {
        var bl = FirmwareFixtures.Image(bootloaderSize, seed: 3);
        var fw = FirmwareFixtures.Image(firmwareSize, seed: 4);
        var sb = new StringBuilder();
        sb.Append(Linear(0x0800));
        for (var i = 0; i < bl.Length; i += 16)
            sb.Append(Record(0, (ushort)i, bl.Skip(i).Take(16).ToArray()));
        sb.Append(Linear(0x0802));
        for (var i = 0; i < fw.Length; i += 16)
            sb.Append(Record(0, (ushort)i, fw.Skip(i).Take(16).ToArray()));
        sb.Append(Record(0x05, 0, 0x08, 0x02, 0x02, 0xE1));
        sb.Append(End);

        var image = Enumerable.Repeat((byte)0xFF, 0x20000 + fw.Length).ToArray();
        bl.CopyTo(image, 0);
        fw.CopyTo(image, 0x20000);
        return (sb.ToString(), image);
    }
}
