using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using MissionPlanner.App.Core.Localization;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Firmware;

public enum HostOs
{
    Windows,
    Linux,
    MacOS,
    Other,
}

/// <summary>A dfu-util to run: its path, whether it is the copy shipped with the app, and the environment it needs.</summary>
public sealed record DfuUtilTool(string Path, bool IsBundled, IReadOnlyDictionary<string, string> Environment)
{
    /// <summary>"bundled", or the path of the one found on PATH.</summary>
    public string SourceText => IsBundled ? "bundled" : Path;
}

/// <summary>
/// Finds dfu-util: the copy shipped in <c>Tools/dfu-util</c> next to the app (see
/// src/MissionPlanner.App/Tools/dfu-util/README.txt), then the PATH (dfu-util.exe on Windows; on
/// macOS also Homebrew's and MacPorts' folders, which apps started from the Finder do not have on
/// their PATH). On Apple Silicon a native dfu-util from PATH or Homebrew comes first, since the
/// bundled one is an Intel build that needs Rosetta 2. The bundled macOS build loads its libusb
/// through DYLD_FALLBACK_LIBRARY_PATH, the Linux arm64 one through LD_LIBRARY_PATH.
/// </summary>
public sealed class DfuUtilLocator
{
    private readonly string? _pathVariable;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, string?> _environment;
    private readonly Action<string>? _prepare;

    /// <param name="bundledDirectory">The folder the bundled dfu-util is in (Tools/dfu-util under the app).</param>
    /// <param name="environment">Reads the app's own environment variables (to extend library paths).</param>
    /// <param name="prepare">Run on the bundled executable before it is offered (sets a missing executable bit).</param>
    public DfuUtilLocator(HostOs os, Architecture arch, string bundledDirectory, string? pathVariable, Func<string, bool> fileExists,
        Func<string, string?>? environment = null, Action<string>? prepare = null)
    {
        Os = os;
        Arch = arch;
        BundledDirectory = bundledDirectory;
        _pathVariable = pathVariable;
        _fileExists = fileExists;
        _environment = environment ?? (_ => null);
        _prepare = prepare;
    }

    public HostOs Os { get; }

    public Architecture Arch { get; }

    public string BundledDirectory { get; }

    public string ExecutableName => Os == HostOs.Windows ? "dfu-util.exe" : "dfu-util";

    /// <summary>Where the published app keeps its dfu-util, relative to the app folder.</summary>
    public static string BundledDirectoryIn(string appDirectory) => Path.Combine(appDirectory, "Tools", "dfu-util");

    public static HostOs CurrentOs =>
        OperatingSystem.IsWindows() ? HostOs.Windows :
        OperatingSystem.IsLinux() ? HostOs.Linux :
        OperatingSystem.IsMacOS() ? HostOs.MacOS : HostOs.Other;

    public static DfuUtilLocator ForThisMachine() =>
        new(CurrentOs, RuntimeInformation.OSArchitecture, BundledDirectoryIn(AppContext.BaseDirectory),
            System.Environment.GetEnvironmentVariable("PATH"), File.Exists, System.Environment.GetEnvironmentVariable, EnsureExecutable);

    /// <summary>Every dfu-util worth trying, best first.</summary>
    public IReadOnlyList<DfuUtilTool> Candidates()
    {
        var bundled = Bundled();
        var onPath = OnPath();
        var list = new List<DfuUtilTool>();
        if (Os == HostOs.MacOS && Arch == Architecture.Arm64)
        {
            list.AddRange(onPath);
            if (bundled != null)
                list.Add(bundled);
        }
        else
        {
            if (bundled != null)
                list.Add(bundled);
            list.AddRange(onPath);
        }

        return list;
    }

    /// <summary>The bundled dfu-util, if this build of the app has one.</summary>
    public DfuUtilTool? Bundled()
    {
        var path = Join(BundledDirectory, ExecutableName);
        if (!_fileExists(path))
            return null;
        _prepare?.Invoke(path);

        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Os == HostOs.MacOS)
        {
            // the Intel build looks for /usr/local/opt/libusb/lib/libusb-1.0.0.dylib; dyld then tries the fallback path
            env["DYLD_FALLBACK_LIBRARY_PATH"] = Prepend(BundledDirectory, _environment("DYLD_FALLBACK_LIBRARY_PATH") ?? "/usr/local/lib:/usr/lib");
        }
        else if (Os == HostOs.Linux && _fileExists(Join(BundledDirectory, "libusb-1.0.so.0")))
        {
            // the arm64 build from Debian is linked against libusb-1.0.so.0, shipped beside it
            env["LD_LIBRARY_PATH"] = Prepend(BundledDirectory, _environment("LD_LIBRARY_PATH"));
        }

        return new DfuUtilTool(path, true, env);
    }

    private IEnumerable<DfuUtilTool> OnPath()
    {
        var separator = Os == HostOs.Windows ? ';' : ':';
        var dirs = (_pathVariable ?? "").Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (Os == HostOs.MacOS)
            dirs.AddRange(["/opt/homebrew/bin", "/usr/local/bin", "/opt/local/bin"]);
        else if (Os == HostOs.Linux)
            dirs.AddRange(["/usr/local/bin", "/usr/bin"]);

        var seen = new HashSet<string>(Os == HostOs.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var bundledDir = TrimEnd(BundledDirectory);
        foreach (var dir in dirs)
        {
            var d = dir.Trim('"');
            if (d.Length == 0 || string.Equals(TrimEnd(d), bundledDir, StringComparison.Ordinal))
                continue;
            var path = Join(d, ExecutableName);
            if (seen.Add(path) && _fileExists(path))
                yield return new DfuUtilTool(path, false, new Dictionary<string, string>());
        }
    }

    // paths of the OS being located for (the same as the host in the app; any in tests), not the host's separator
    private char Separator => Os == HostOs.Windows ? '\\' : '/';

    private string Join(string dir, string name) =>
        dir.Length == 0 ? name : dir.TrimEnd('/', Separator) + Separator + name;

    private string TrimEnd(string dir) => dir.Length > 1 ? dir.TrimEnd('/', Separator) : dir;

    private static string Prepend(string dir, string? existing) => string.IsNullOrEmpty(existing) ? dir : dir + ":" + existing;

    /// <summary>Sets the executable bit when a copy lost it (an archive tool or a Windows drive in between).</summary>
    public static void EnsureExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            var mode = File.GetUnixFileMode(path);
            if ((mode & UnixFileMode.UserExecute) == 0)
                File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // read-only install: starting it reports the problem
        }
    }
}

/// <summary>A line of dfu-util output, as the progress panel uses it.</summary>
public readonly record struct DfuLine(string Text, bool IsError, UploadStage? Stage = null, int Percent = -1)
{
    public bool IsProgress => Stage != null;
}

/// <summary>
/// Reads dfu-util 0.11's output (main.c, dfuse.c, dfu_file.c): the "Erase"/"Download" progress bars,
/// "File downloaded successfully", the leave request and the errors that tell why it failed.
/// </summary>
public sealed partial class DfuUtilOutput
{
    private readonly List<string> _errors = new();
    private readonly List<string> _errorsAfterDownload = new();

    /// <summary>"dfu-util 0.11", from the first line.</summary>
    public string? Version { get; private set; }

    /// <summary>The whole image was written ("File downloaded successfully").</summary>
    public bool Downloaded { get; private set; }

    /// <summary>dfu-util asked the board to leave DFU mode and start the firmware.</summary>
    public bool LeaveRequested { get; private set; }

    /// <summary>"No DFU capable USB device available".</summary>
    public bool NoDevice { get; private set; }

    /// <summary>"More than one DFU capable USB device found".</summary>
    public bool MoreThanOneDevice { get; private set; }

    /// <summary>The libusb error when a DFU device was seen but could not be opened (LIBUSB_ERROR_ACCESS, _NOT_SUPPORTED...).</summary>
    public string? OpenError { get; private set; }

    /// <summary>Board in DFU mode that dfu-util -l lists.</summary>
    public List<DfuDevice> Devices { get; } = new();

    public IReadOnlyList<string> Errors => _errors;

    public int LastPercent { get; private set; } = -1;

    public UploadStage? LastStage { get; private set; }

    [GeneratedRegex(@"^\s*(Erase|Download|Upload)\s*\[[= ]*\]\s*(\d{1,3})%\s*(\d+)\s*bytes", RegexOptions.CultureInvariant)]
    private static partial Regex ProgressRegex();

    [GeneratedRegex(@"^dfu-util\s+(\d+\.\d+\S*)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"Cannot open (?:DFU )?device.*?(LIBUSB_ERROR_[A-Z_]+)", RegexOptions.CultureInvariant)]
    private static partial Regex OpenErrorRegex();

    [GeneratedRegex(@"^Found DFU: \[([0-9a-fA-F]{4}):([0-9a-fA-F]{4})\](.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex FoundRegex();

    [GeneratedRegex(@"(\w+)=(""[^""]*""|[^,]*)", RegexOptions.CultureInvariant)]
    private static partial Regex FieldRegex();

    public DfuLine Add(string line, bool isError)
    {
        var text = line.TrimEnd();
        var m = ProgressRegex().Match(text);
        if (m.Success)
        {
            var stage = m.Groups[1].Value switch
            {
                "Erase" => UploadStage.Erasing,
                "Download" => UploadStage.Programming,
                _ => UploadStage.Verifying,
            };
            var percent = Math.Clamp(int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), 0, 100);
            LastStage = stage;
            LastPercent = percent;
            return new DfuLine(text, false, stage, percent);
        }

        if (Version == null && VersionRegex().Match(text) is { Success: true } v)
            Version = "dfu-util " + v.Groups[1].Value;

        if (text.Contains("File downloaded successfully", StringComparison.Ordinal))
            Downloaded = true;
        else if (text.Contains("Submitting leave request", StringComparison.Ordinal))
            LeaveRequested = true;
        else if (text.Contains("No DFU capable USB device", StringComparison.Ordinal))
            NoDevice = true;
        else if (text.Contains("More than one DFU capable USB device", StringComparison.Ordinal))
            MoreThanOneDevice = true;

        if (OpenErrorRegex().Match(text) is { Success: true } o)
            OpenError ??= o.Groups[1].Value;

        if (FoundRegex().Match(text) is { Success: true } f)
            AddFound(f);

        // errx/warnx print "dfu-util: ..." on stderr; the DFU status after a failed chunk too
        var error = isError && !IsSuffixWarning(text) && !text.StartsWith("dfu-util 0", StringComparison.Ordinal) &&
                    !text.StartsWith("Copyright", StringComparison.Ordinal);
        if (error)
        {
            _errors.Add(text);
            if (Downloaded)
                _errorsAfterDownload.Add(text);
        }

        return new DfuLine(text, error);
    }

    private void AddFound(Match f)
    {
        var id = new UsbId(Convert.ToUInt16(f.Groups[1].Value, 16), Convert.ToUInt16(f.Groups[2].Value, 16));
        var fields = FieldRegex().Matches(f.Groups[3].Value)
            .GroupBy(x => x.Groups[1].Value)
            .ToDictionary(g => g.Key, g => g.First().Groups[2].Value.Trim().Trim('"'));
        var path = fields.GetValueOrDefault("path");
        var serial = fields.GetValueOrDefault("serial");
        if (serial is "UNKNOWN" or "")
            serial = null;
        // one line per alternate setting: keep one device per USB path
        if (Devices.Any(d => d.Id == id && d.Location == path && d.Serial == serial))
            return;
        var name = fields.GetValueOrDefault("name") ?? "";
        Devices.Add(new DfuDevice(id, name.StartsWith('@') ? "STM32 DFU" : name, serial, path));
    }

    /// <summary>
    /// The image is on the board: dfu-util said "File downloaded successfully". With <c>:leave</c> the
    /// board resets as soon as it is asked to start the firmware, so dfu-util may then fail to read its
    /// status ("Error during download get_status") and exit with an error code; that still counts.
    /// </summary>
    public bool Succeeded(int exitCode) => Downloaded;

    /// <summary>Errors printed after the image was written (the board resetting under dfu-util).</summary>
    public IReadOnlyList<string> ErrorsAfterDownload => _errorsAfterDownload;

    /// <summary>dfu-util's warnings for a raw .bin without a DFU suffix, expected here.</summary>
    public static bool IsSuffixWarning(string line) =>
        line.Contains("DFU suffix", StringComparison.Ordinal) && !line.Contains("needed", StringComparison.Ordinal);

    /// <summary>The most telling error line, without dfu-util's prefix.</summary>
    public string? LastError => _errors.Count == 0 ? null : Strip(_errors.LastOrDefault(e => e.StartsWith("dfu-util:", StringComparison.Ordinal)) ?? _errors[^1]);

    private static string Strip(string s) => s.StartsWith("dfu-util:", StringComparison.Ordinal) ? s["dfu-util:".Length..].Trim() : s;
}

/// <summary>A board in DFU mode.</summary>
public sealed record DfuDevice(UsbId Id, string Name, string? Serial = null, string? Location = null)
{
    public string Description =>
        (Name.Length > 0 ? Name : "DFU device") + $" ({Id})" +
        (string.IsNullOrEmpty(Serial) ? "" : ", serial " + Serial) +
        (string.IsNullOrEmpty(Location) ? "" : ", " + Location);
}

public enum DfuOutcome
{
    /// <summary>The image was written and the board was asked to start it.</summary>
    Flashed,

    /// <summary>No dfu-util could be started.</summary>
    NoTool,

    /// <summary>No board in DFU mode was found (or it could not be opened).</summary>
    NoDevice,

    Failed,

    Cancelled,
}

public sealed record DfuResult(DfuOutcome Outcome, string Message);

/// <summary>The dfu-util that will be used and the version it reported.</summary>
public sealed record DfuToolInfo(DfuUtilTool Tool, string Version)
{
    /// <summary>"dfu-util 0.11 (bundled)".</summary>
    public string Text => $"{Version} ({Tool.SourceText})";
}

/// <summary>
/// Writes a <see cref="FlashImage"/> to an STM32 in its ROM DFU bootloader with dfu-util:
/// <c>dfu-util -a 0 -d 0483:df11 -s 0x08000000:leave -D image.bin</c>. The DfuSe address
/// erases exactly the pages the image covers, and <c>:leave</c> starts the new firmware.
/// </summary>
public sealed class DfuFlasher
{
    /// <summary>The STM32 system bootloader in DFU mode.</summary>
    public static readonly UsbId StmDfu = new(0x0483, 0xDF11);

    public const string StmDfuId = "0483:df11";

    private readonly IProcessRunner _runner;
    private readonly DfuUtilLocator _locator;
    private DfuToolInfo? _tool;

    public DfuFlasher(IProcessRunner runner, DfuUtilLocator locator)
    {
        _runner = runner;
        _locator = locator;
    }

    public Action<string>? Log { get; init; }

    public IProgress<UploadProgress>? Progress { get; init; }

    public HostOs Os => _locator.Os;

    public static IReadOnlyList<string> DownloadArguments(uint address, string binPath) =>
        ["-a", "0", "-d", StmDfuId, "-s", $"0x{address:X8}:leave", "-D", binPath];

    public static IReadOnlyList<string> ListArguments { get; } = ["-l", "-d", StmDfuId];

    /// <summary>
    /// The first dfu-util that starts (<c>--version</c>, which does not touch USB), in the locator's order.
    /// Null when none does; <see cref="MissingToolMessage"/> then says what to install.
    /// </summary>
    public async Task<DfuToolInfo?> FindToolAsync(CancellationToken cancel)
    {
        if (_tool != null)
            return _tool;
        BundledFailed = false;
        foreach (var tool in _locator.Candidates())
        {
            cancel.ThrowIfCancellationRequested();
            var output = new DfuUtilOutput();
            try
            {
                var code = await _runner.RunAsync(new ProcessRequest(tool.Path, ["--version"], tool.Environment),
                    (line, err) => output.Add(line, err), cancel).ConfigureAwait(false);
                if (code == 0 && output.Version != null)
                {
                    _tool = new DfuToolInfo(tool, output.Version);
                    Log?.Invoke("Using " + _tool.Text + ".");
                    return _tool;
                }

                Log?.Invoke($"{tool.Path} --version failed (exit code {code}).");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Cannot start {tool.Path}: {ex.Message}");
            }

            if (tool.IsBundled)
                BundledFailed = true;
        }

        return null;
    }

    /// <summary>The bundled dfu-util was there but would not start (last <see cref="FindToolAsync"/>).</summary>
    public bool BundledFailed { get; private set; }

    /// <summary>What to install when no dfu-util starts.</summary>
    public string MissingToolMessage()
    {
        var head = BundledFailed
            ? L.Tr("The dfu-util that comes with Mission Planner did not start.")
            : L.Tr("dfu-util was not found.");
        return head + "\n\n" + _locator.Os switch
        {
            HostOs.Linux => L.Tr("Install it with: sudo apt install dfu-util (or your distribution's dfu-util package), then try again."),
            HostOs.MacOS => (_locator.Arch == Architecture.Arm64 && BundledFailed
                                ? L.Tr("On Apple Silicon it needs Rosetta 2: softwareupdate --install-rosetta. Or install a native one: brew install dfu-util.")
                                : L.Tr("Install it with Homebrew: brew install dfu-util, then try again.")),
            HostOs.Windows => L.Tr("Install dfu-util (dfu-util.exe on the PATH, from dfu-util.sourceforge.net) and the WinUSB driver for \"STM32 BOOTLOADER\" with Zadig (zadig.akeo.ie), or flash the .hex with STM32CubeProgrammer."),
            _ => L.Tr("Install dfu-util from dfu-util.sourceforge.net, then try again."),
        };
    }

    /// <summary>Boards in DFU mode, from <c>dfu-util -l</c> (only when the user asks: it opens each DFU device).</summary>
    public async Task<(IReadOnlyList<DfuDevice> Devices, string? Problem)> ListDevicesAsync(CancellationToken cancel)
    {
        var tool = await FindToolAsync(cancel).ConfigureAwait(false);
        if (tool == null)
            return ([], MissingToolMessage());
        var output = new DfuUtilOutput();
        try
        {
            await _runner.RunAsync(new ProcessRequest(tool.Tool.Path, ListArguments, tool.Tool.Environment),
                (line, err) => output.Add(line, err), cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ([], ex.Message);
        }

        string? problem = null;
        if (output.OpenError != null)
            problem = OpenProblem(output.OpenError);
        return (output.Devices, problem);
    }

    /// <summary>Writes the image and asks the board to start it.</summary>
    public async Task<DfuResult> FlashAsync(FlashImage image, CancellationToken cancel)
    {
        var tool = await FindToolAsync(cancel).ConfigureAwait(false);
        if (tool == null)
            return new DfuResult(DfuOutcome.NoTool, MissingToolMessage());

        var bin = Path.Combine(Path.GetTempPath(), $"mp-dfu-{Guid.NewGuid():N}.bin");
        var output = new DfuUtilOutput();
        try
        {
            await File.WriteAllBytesAsync(bin, image.Data, cancel).ConfigureAwait(false);
            var request = new ProcessRequest(tool.Tool.Path, DownloadArguments(image.StartAddress, bin), tool.Tool.Environment);
            Log?.Invoke("Running " + request.CommandLine);
            Report(UploadStage.Erasing, -1, L.Tr("Starting dfu-util…"));

            int code;
            var lastLogged = (Stage: (UploadStage?)null, Tenth: -1);
            try
            {
                code = await _runner.RunAsync(request, (line, err) =>
                {
                    var l = output.Add(line, err);
                    if (l.IsProgress)
                    {
                        Report(l.Stage!.Value, l.Percent,
                            l.Stage == UploadStage.Erasing ? L.Format("Erasing… {0}%", l.Percent) : L.Format("Writing… {0}%", l.Percent));
                        // the log gets the 0, 50 and 100 % lines of each stage, not every redraw
                        var tenth = l.Percent / 10;
                        if (lastLogged.Stage != l.Stage || tenth != lastLogged.Tenth)
                        {
                            lastLogged = (l.Stage, tenth);
                            if (tenth % 5 == 0)
                                Log?.Invoke(l.Text.Replace('\t', ' '));
                        }
                    }
                    else if (!IsBanner(l.Text))
                    {
                        Log?.Invoke(l.Text);
                    }
                }, cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new DfuResult(DfuOutcome.Cancelled, L.Tr(
                    "Cancelled. The flash is incomplete: the board stays in DFU mode (or enters it again with BOOT held while plugging in USB). Flash it again."));
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
            {
                return new DfuResult(DfuOutcome.NoTool, L.Tr("Cannot start dfu-util:") + " " + ex.Message);
            }

            Log?.Invoke($"dfu-util exited with code {code}.");
            if (output.Succeeded(code))
            {
                if (code != 0)
                    Log?.Invoke("The error after the download is the board resetting into the new firmware.");
                var message = output.LeaveRequested
                    ? L.Tr("Flashed. The board is starting ArduPilot.")
                    : L.Tr("Flashed, but the board did not leave DFU mode: unplug it and plug it back in (without holding BOOT) to start ArduPilot.");
                Report(UploadStage.Done, 100, message);
                return new DfuResult(DfuOutcome.Flashed, message);
            }

            var result = Explain(output, code);
            Report(UploadStage.Failed, -1, result.Message);
            return result;
        }
        finally
        {
            try
            {
                File.Delete(bin);
            }
            catch (Exception)
            {
            }
        }
    }

    private static bool IsBanner(string line) =>
        DfuUtilOutput.IsSuffixWarning(line) ||
        line.StartsWith("Copyright", StringComparison.Ordinal) ||
        line.StartsWith("This program is Free Software", StringComparison.Ordinal) ||
        line.StartsWith("Please report bugs", StringComparison.Ordinal);

    private DfuResult Explain(DfuUtilOutput output, int code)
    {
        if (output.OpenError != null)
            return new DfuResult(DfuOutcome.NoDevice, OpenProblem(output.OpenError));

        if (output.MoreThanOneDevice)
            return new DfuResult(DfuOutcome.NoDevice, L.Tr("More than one board is in DFU mode. Unplug all but the one to flash, then try again."));

        if (output.NoDevice)
        {
            var text = L.Tr("No board in DFU mode was found.") + "\n\n" + HowToEnterDfu;
            if (_locator.Os == HostOs.Windows)
                text += "\n\n" + WindowsDriverHint;
            return new DfuResult(DfuOutcome.NoDevice, text);
        }

        var error = output.LastError ?? L.Format("dfu-util exited with code {0}.", code);
        var written = output.LastStage == UploadStage.Programming && output.LastPercent > 0;
        return new DfuResult(DfuOutcome.Failed, L.Tr("Flashing failed:") + " " + error +
                                                (written
                                                    ? "\n\n" + L.Tr("The flash is incomplete, so the board will not start. It can always be put in DFU mode again (hold BOOT while plugging in USB): flash it again.")
                                                    : ""));
    }

    private string OpenProblem(string libusbError) => _locator.Os switch
    {
        HostOs.Windows => L.Format("A board in DFU mode was found but Windows cannot open it ({0}).", libusbError) + "\n\n" + WindowsDriverHint,
        HostOs.Linux when libusbError == "LIBUSB_ERROR_ACCESS" =>
            L.Tr("A board in DFU mode was found, but there is no permission to use it. HELP › Allow USB access installs the rule for boards in DFU mode; then unplug the board and plug it back in."),
        _ => L.Format("A board in DFU mode was found but cannot be opened ({0}). Unplug it and plug it back in; close other programs that use it.", libusbError),
    };

    /// <summary>The ways into the STM32 ROM DFU bootloader.</summary>
    public static string HowToEnterDfu => L.Tr(
        "To put the board in DFU mode: hold its BOOT button while plugging in USB. From iNav's CLI type dfu, from Betaflight's CLI type bl. With ArduPilot running: TOOLS › Vehicle commands › Reboot to DFU.");

    public static string WindowsDriverHint => L.Tr(
        "Windows needs the WinUSB driver for a board in DFU mode: run Zadig (zadig.akeo.ie), choose \"STM32 BOOTLOADER\" (Options › List All Devices if it is not shown), select WinUSB and click Install Driver, then try again.");

    private void Report(UploadStage stage, double percent, string text) => Progress?.Report(new UploadProgress(stage, percent, text));
}
