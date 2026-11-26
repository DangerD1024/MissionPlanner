using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using MissionPlanner.App.Core.Localization;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Firmware;
using FirmwareInfo = MissionPlanner.ArduPilot.APFirmware.FirmwareInfo;
using ReleaseType = MissionPlanner.ArduPilot.APFirmware.RELEASE_TYPES;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>
/// Install Firmware (shown while disconnected), like Mission Planner's ConfigFirmwareManifest:
/// the firmware manifest, one tile per vehicle type labelled with the version it installs, a
/// release choice, every option for a board, custom .apj files, the boards on USB and the
/// bootloader upload (download, wait for the bootloader, skip identical firmware, flash).
/// Boards without ArduPilot's bootloader (iNav, Betaflight, blank) are flashed in STM32 DFU mode
/// with dfu-util instead: the manifest's "_with_bl.hex" (bootloader and firmware) or a custom .hex/.bin.
/// </summary>
public sealed partial class FirmwareViewModel : PageViewModel
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(FirmwareViewModel));
    private const int MaxLogLines = 400;

    private readonly IBootloaderFactory _bootloaders;
    private readonly Func<IReadOnlyList<SerialDevice>> _scanPorts;
    private readonly Func<IReadOnlyList<string>> _probePorts;
    private readonly DfuEnvironment _dfu;
    private FirmwareCatalog? _catalog;
    private CancellationTokenSource? _cancel;
    private bool _scanning;
    private int _tick;
    private bool _shown;

    public FirmwareViewModel(AppServices services)
        : this(services, SerialBootloaderFactory.Instance, SerialDeviceScanner.Scan, SerialDeviceScanner.ProbePorts,
            DfuEnvironment.ForThisMachine())
    {
    }

    /// <summary>For tests: fake bootloaders, port lists and DFU tools (none at all when <paramref name="dfu"/> is null).</summary>
    internal FirmwareViewModel(AppServices services, IBootloaderFactory bootloaders,
        Func<IReadOnlyList<SerialDevice>> scanPorts, Func<IReadOnlyList<string>> probePorts, DfuEnvironment? dfu = null)
        : base("Install Firmware")
    {
        Services = services;
        _bootloaders = bootloaders;
        _scanPorts = scanPorts;
        _probePorts = probePorts;
        _dfu = dfu ?? DfuEnvironment.None;
        Tiles = VehicleTile.CreateAll();
        _selectedRelease = ReleaseOption.All[0];
        _dfuStatusText = _dfu.ScanDevices != null
            ? "No board in DFU mode on USB."
            : "Put the board in DFU mode, then click Check (it runs dfu-util -l).";
    }

    public AppServices Services { get; }

    // ---- manifest ------------------------------------------------------------------------------

    public IReadOnlyList<VehicleTile> Tiles { get; }

    public IReadOnlyList<ReleaseOption> Releases => ReleaseOption.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReleaseWarning), nameof(HasReleaseWarning))]
    private ReleaseOption _selectedRelease;

    public string ReleaseWarning => SelectedRelease.Type switch
    {
        ReleaseType.DEV => "Dev builds are the latest development code. They are for testing only and may have bugs that crash the vehicle.",
        ReleaseType.BETA => "Beta builds are release candidates for testing before the next official release.",
        _ => "",
    };

    public bool HasReleaseWarning => ReleaseWarning.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorText = "";

    public bool HasError => ErrorText.Length > 0;

    public bool IsReady => !IsLoading && _catalog != null;

    [ObservableProperty] private string _manifestText = "";

    partial void OnSelectedReleaseChanged(ReleaseOption value)
    {
        foreach (var t in Tiles)
            t.Update(_catalog, value.Type);
    }

    [RelayCommand]
    private Task ReloadManifest() => LoadManifestAsync(force: true);

    /// <summary>For tests: a manifest from a fixture instead of the network.</summary>
    internal void UseCatalog(FirmwareCatalog catalog)
    {
        _catalog = catalog;
        foreach (var t in Tiles)
            t.Update(catalog, SelectedRelease.Type);
        RebuildPortRows(Ports.Select(p => p.Device).ToList());
        OnPropertyChanged(nameof(IsReady));
    }

    private async Task LoadManifestAsync(bool force)
    {
        if (IsLoading)
            return;
        IsLoading = true;
        ErrorText = "";
        ManifestText = "Downloading the firmware list…";
        try
        {
            var catalog = await FirmwareManifest.LoadAsync(force);
            _catalog = catalog;
            ManifestText = $"{catalog.Uploadable.Count:N0} firmware files for {catalog.Uploadable.Select(f => f.Platform).Distinct().Count():N0} boards.";
            foreach (var t in Tiles)
                t.Update(catalog, SelectedRelease.Type);
            RebuildPortRows(Ports.Select(p => p.Device).ToList());
            if (ShowAllOptions)
                Options ??= CreateOptions();
        }
        catch (Exception ex)
        {
            Log.Warn("manifest", ex);
            ErrorText = ex.Message;
            ManifestText = "";
            foreach (var t in Tiles)
                t.VersionText = "unavailable";
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsReady));
        }
    }

    // ---- boards on USB -------------------------------------------------------------------------

    public ObservableCollection<PortRow> Ports { get; } = new();

    [ObservableProperty] private PortRow? _selectedPort;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetected))]
    private string _detectedText = "";

    public bool HasDetected => DetectedText.Length > 0;

    /// <summary>Board id read from a bootloader by "Detect bootloader"; wins over the USB guess.</summary>
    public long? DetectedBoardId { get; private set; }

    [RelayCommand]
    private Task RefreshPorts() => ScanPortsAsync();

    private async Task ScanPortsAsync()
    {
        if (_scanning)
            return;
        _scanning = true;
        try
        {
            var (devices, dfu) = await Task.Run(() =>
            {
                IReadOnlyList<SerialDevice> ports;
                try
                {
                    ports = _scanPorts();
                }
                catch (Exception ex)
                {
                    Log.Warn("port scan", ex);
                    ports = [];
                }

                return (ports, ScanDfuDevices());
            });
            if (!devices.SequenceEqual(Ports.Select(p => p.Device)))
                RebuildPortRows(devices);
            if (dfu != null)
                ApplyDfuDevices(dfu, null);
        }
        finally
        {
            _scanning = false;
        }
    }

    private void RebuildPortRows(IReadOnlyList<SerialDevice> devices)
    {
        var selected = SelectedPort?.PortName;
        Ports.Clear();
        foreach (var d in devices)
            Ports.Add(PortRow.Create(d, _catalog));
        SelectedPort = Ports.FirstOrDefault(p => p.PortName == selected) ?? Ports.FirstOrDefault(p => p.IsRecognised);
        OnPropertyChanged(nameof(PortsText));
    }

    public string PortsText => Ports.Count == 0
        ? "No serial ports. Plug the board in with a USB cable."
        : Ports.Any(p => p.IsRecognised)
            ? ""
            : "No board recognised from its USB details. Use \"Detect bootloader\" and replug the board, or pick a file in All options.";

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task DetectBootloader()
    {
        if (!BeginWork("Looking for a bootloader… plug the board in now, or unplug it and plug it back in."))
            return;
        var cancel = _cancel!.Token;
        try
        {
            AddLog("Probing serial ports for a bootloader (up to 20 s).");
            var start = Environment.TickCount64;
            (IBootloader Bootloader, BootloaderInfo Info)? hit = null;
            while (hit == null && Environment.TickCount64 - start < 20_000)
            {
                cancel.ThrowIfCancellationRequested();
                var ports = await Task.Run(_probePorts, cancel);
                hit = await BootloaderScanner.ProbeAsync(_bootloaders, ports, _ => true, null, null, cancel);
                if (hit == null)
                    await Task.Delay(300, cancel);
            }

            if (hit is not { } h)
            {
                SetStatus("No bootloader answered. A board stays in its bootloader for a few seconds after it is plugged in.", -1);
                AddLog("No bootloader found.");
                return;
            }

            // closing without a reboot leaves the board in its bootloader, ready for the upload
            h.Bootloader.Dispose();
            DetectedBoardId = h.Info.BoardId;
            var name = _catalog?.BoardName(h.Info.BoardId) ?? $"board id {h.Info.BoardId}";
            DetectedText = $"Bootloader on {h.Info.PortName}: {name} (board id {h.Info.BoardId}), bootloader v{h.Info.BootloaderRevision}" +
                           (string.IsNullOrEmpty(h.Info.ChipDescription) ? "" : ", " + h.Info.ChipDescription);
            AddLog("Found " + h.Info);
            SetStatus("Bootloader found. Choose the firmware to install; the board waits in its bootloader.", 100);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Cancelled.", -1);
        }
        finally
        {
            EndWork();
        }
    }

    [RelayCommand]
    private void ClearDetected()
    {
        DetectedBoardId = null;
        DetectedText = "";
    }

    /// <summary>
    /// Board ids to install for: the detected bootloader, else the selected port's USB guess, else
    /// the first recognised port (ConfigFirmwareManifest walks the port list the same way).
    /// </summary>
    internal (IReadOnlyList<long> Ids, string Board, string? Platform) ResolveBoard()
    {
        if (DetectedBoardId is { } id)
            return ([id], _catalog?.BoardName(id) ?? $"board id {id}", _catalog?.PlatformsFor(id).FirstOrDefault());

        var row = SelectedPort is { IsRecognised: true } s ? s : Ports.FirstOrDefault(p => p.IsRecognised);
        if (row == null)
            return ([], "", null);
        var platform = row.Device.Board;
        if (platform.EndsWith("-BL", StringComparison.OrdinalIgnoreCase))
            platform = platform[..^3];
        return (row.BoardIds, row.BoardName, platform);
    }

    // ---- all options ---------------------------------------------------------------------------

    [ObservableProperty] private bool _showAllOptions;

    [ObservableProperty] private FirmwareOptionsViewModel? _options;

    partial void OnShowAllOptionsChanged(bool value)
    {
        if (value && _catalog != null)
            Options ??= CreateOptions();
    }

    private FirmwareOptionsViewModel CreateOptions()
    {
        var options = new FirmwareOptionsViewModel(_catalog!.Uploadable, ResolveBoard().Platform);
        options.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FirmwareOptionsViewModel.Selected))
            {
                InstallSelectedOptionCommand.NotifyCanExecuteChanged();
                InstallSelectedViaDfuCommand.NotifyCanExecuteChanged();
            }
        };
        return options;
    }

    [RelayCommand]
    private void ClearOptionFilters() => Options?.ClearFilters();

    private bool CanInstallSelected() => CanStart() && Options?.Selected != null;

    [RelayCommand(CanExecute = nameof(CanInstallSelected))]
    private async Task InstallSelectedOption()
    {
        if (Options?.Selected is not { } row)
            return;
        if (!await Services.Dialogs.ConfirmAsync("Install firmware",
                $"Install {row.Summary}?\n\n{row.File}\n\nThe board must be on USB; do not unplug it while it is being flashed.", "Install"))
            return;
        await DownloadAndFlashAsync(row.Info);
    }

    // ---- installing ----------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Install(VehicleTile tile)
    {
        if (_catalog == null)
        {
            await Services.Dialogs.ShowMessageAsync("Install firmware", "The firmware list has not loaded yet.", MessageKind.Warning);
            return;
        }

        var (ids, board, platform) = ResolveBoard();
        if (ids.Count == 0)
        {
            await Services.Dialogs.ShowMessageAsync("Install firmware",
                "Could not tell which board is connected (unknown USB board name and VID/PID).\n\n" +
                "Plug the board in and click \"Detect bootloader\", or pick the file for your board in \"All options\".",
                MessageKind.Error);
            return;
        }

        var release = SelectedRelease;
        var candidates = _catalog.Candidates(ids, tile.Type, release.Type);
        if (candidates.Count == 0)
        {
            await Services.Dialogs.ShowMessageAsync("Install firmware",
                $"No {release.Name} {tile.Title} firmware is available for {board}.", MessageKind.Error);
            return;
        }

        FirmwareInfo pick;
        if (candidates.Count == 1)
        {
            pick = candidates[0];
        }
        else
        {
            var choice = new FirmwareChoiceViewModel(
                $"Several {tile.Title} files fit {board}. Choose the one for your board.", candidates, platform);
            if (!await Services.Windows.ShowDialogAsync(choice, 820, 560) || choice.Result == null)
                return;
            pick = choice.Result;
        }

        var row = new FirmwareRow(pick);
        if (!await Services.Dialogs.ConfirmAsync("Install firmware",
                $"Install {row.Summary}?\n\nThe board must be on USB; do not unplug it while it is being flashed.", "Install"))
            return;

        await DownloadAndFlashAsync(pick);
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task LoadCustomFirmware()
    {
        var path = await Services.Dialogs.OpenFileAsync("Load custom firmware",
            new FileFilter("Firmware", "apj", "px4", "hex", "bin"),
            new FileFilter("ArduPilot firmware (through its bootloader)", "apj", "px4"),
            new FileFilter("STM32 DFU image (hex, bin)", "hex", "bin"),
            new FileFilter("All files", "*"));
        if (string.IsNullOrEmpty(path))
            return;

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".hex" or ".bin")
        {
            await DfuInstallFileAsync(path, ext == ".hex");
            return;
        }

        if (ext == ".dfu")
        {
            await Services.Dialogs.ShowMessageAsync("Load custom firmware",
                "DfuSe .dfu files are not supported here. Use the firmware's .hex (or a .bin that starts at 0x08000000) to flash over DFU, or its .apj to install through ArduPilot's bootloader.",
                MessageKind.Warning);
            return;
        }

        ApjFirmware fw;
        try
        {
            fw = await Task.Run(() => ApjFirmware.Load(path));
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            await Services.Dialogs.ShowMessageAsync("Load custom firmware", "Cannot read the firmware file:\n" + ex.Message, MessageKind.Error);
            return;
        }

        var boardName = _catalog?.BoardName(fw.BoardId) ?? $"board id {fw.BoardId}";
        var details = string.Join("\n", new[]
        {
            $"For: {boardName} (board id {fw.BoardId})",
            string.IsNullOrEmpty(fw.Summary) ? null : "Firmware: " + fw.Summary,
            string.IsNullOrEmpty(fw.GitIdentity) ? null : "Git: " + fw.GitIdentity,
            $"Image: {fw.ImageSize / 1024} KB" + (fw.ExtfImageSize > 0 ? $" + {fw.ExtfImageSize / 1024} KB external flash" : ""),
        }.Where(s => s != null));
        if (!await Services.Dialogs.ConfirmAsync("Install firmware",
                $"Install {Path.GetFileName(path)}?\n\n{details}\n\nDo not unplug the board while it is being flashed.", "Install"))
            return;

        if (!BeginWork("Starting…"))
            return;
        try
        {
            await FlashAsync(fw);
        }
        finally
        {
            EndWork();
        }
    }

    private async Task DownloadAndFlashAsync(FirmwareInfo info)
    {
        if (info.Url == null || !BeginWork("Downloading firmware…"))
            return;
        var cancel = _cancel!.Token;
        var temp = Path.Combine(Path.GetTempPath(), $"mp-firmware-{Guid.NewGuid():N}.apj");
        try
        {
            AddLog("Downloading " + info.Url.AbsoluteUri);
            await FileDownloader.DownloadAsync(FirmwareManifest.Http, info.Url, temp,
                new DispatcherProgress<double>(Services.Dispatcher, f =>
                {
                    if (IsBusy)
                        SetStatus("Downloading firmware…", f < 0 ? -1 : f * 100);
                }), cancel);
            AddLog("Downloaded.");

            var fw = await Task.Run(() => ApjFirmware.Load(temp), cancel);
            if (fw.BoardId != info.BoardId)
                AddLog($"Note: the file is for board id {fw.BoardId}, the manifest said {info.BoardId}.");
            await FlashAsync(fw);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Cancelled.", -1);
            AddLog("Cancelled.");
        }
        catch (Exception ex)
        {
            Log.Warn("firmware download", ex);
            SetStatus("Download failed: " + ex.Message, -1);
            AddLog("Download failed: " + ex.Message);
            await Services.Dialogs.ShowMessageAsync("Install firmware", "Failed to download the firmware:\n" + ex.Message, MessageKind.Error);
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception)
            {
            }

            EndWork();
        }
    }

    /// <summary>Runs the bootloader upload; the caller holds the busy state.</summary>
    private async Task FlashAsync(ApjFirmware fw)
    {
        var rebootPorts = Ports.Where(p => p.IsRecognised && !p.IsBootloader).Select(p => p.PortName).ToList();
        var flow = new FirmwareUploadFlow(_bootloaders, _probePorts)
        {
            ConfirmSameFirmware = message => Services.Dispatcher.InvokeAsync(() =>
                Services.Dialogs.ConfirmAsync("Same firmware", message, "Upload anyway")),
            RebootToBootloader = rebootPorts.Count == 0
                ? null
                : ct => MavlinkBootloaderReboot.TryPortsAsync(rebootPorts, AddLogFromWorker, ct),
            Progress = new DispatcherProgress<UploadProgress>(Services.Dispatcher, p =>
            {
                if (IsBusy)
                    SetStatus(p.Text, p.Percent);
            }),
            Log = AddLogFromWorker,
        };

        AddLog($"Firmware for board id {fw.BoardId}, {fw.ImageSize / 1024} KB.");
        var result = await flow.RunAsync(fw, _cancel!.Token);
        SetStatus(result.Message, result.Outcome == UploadOutcome.Flashed ? 100 : -1);
        LastOutcome = result.Outcome;

        if (result.Outcome == UploadOutcome.Flashed)
        {
            // the board has left its bootloader
            ClearDetected();
            await Services.Dialogs.ShowMessageAsync("Install firmware",
                "Upload done.\n\nThe board is restarting with the new firmware. Wait a few seconds before you connect.");
        }
        else if (result.Outcome is UploadOutcome.NotNeeded)
        {
            await Services.Dialogs.ShowMessageAsync("Install firmware", result.Message);
        }
        else if (result.Outcome is UploadOutcome.Failed or UploadOutcome.NoBoard)
        {
            await Services.Dialogs.ShowMessageAsync("Install firmware", result.Message, MessageKind.Error);
        }
    }

    /// <summary>The last flash's outcome (tests and status display).</summary>
    public UploadOutcome? LastOutcome { get; private set; }

    // ---- STM32 DFU ---------------------------------------------------------------------------

    /// <summary>Boards in STM32 DFU mode (sysfs on Linux, <c>dfu-util -l</c> elsewhere when Check is clicked).</summary>
    public ObservableCollection<DfuDevice> DfuDevices { get; } = new();

    public bool HasDfuDevice => DfuDevices.Count > 0;

    [ObservableProperty] private string _dfuStatusText;

    /// <summary>Which dfu-util will be used ("dfu-util: bundled with Mission Planner").</summary>
    [ObservableProperty] private string _dfuToolText = "";

    /// <summary>The last DFU flash's outcome (tests).</summary>
    public DfuOutcome? LastDfuOutcome { get; private set; }

    private IReadOnlyList<DfuDevice>? ScanDfuDevices()
    {
        if (_dfu.ScanDevices == null)
            return null;
        try
        {
            return _dfu.ScanDevices();
        }
        catch (Exception ex)
        {
            Log.Warn("DFU scan", ex);
            return [];
        }
    }

    private void ApplyDfuDevices(IReadOnlyList<DfuDevice> devices, string? problem)
    {
        if (!devices.SequenceEqual(DfuDevices))
        {
            DfuDevices.Clear();
            foreach (var d in devices)
                DfuDevices.Add(d);
            OnPropertyChanged(nameof(HasDfuDevice));
        }

        DfuStatusText = problem ?? devices.Count switch
        {
            0 => _dfu.ScanDevices != null ? "No board in DFU mode on USB." : "No board in DFU mode was found.",
            1 => L.Format("Board in DFU mode: {0}", devices[0].Description),
            _ => L.Format("{0} boards in DFU mode: unplug all but the one to flash.", devices.Count),
        };
    }

    private void UpdateDfuToolText()
    {
        DfuUtilTool? tool;
        try
        {
            tool = _dfu.Locator.Candidates().FirstOrDefault();
        }
        catch (Exception ex)
        {
            Log.Warn("dfu-util lookup", ex);
            tool = null;
        }

        DfuToolText = tool == null ? L.Tr("dfu-util: not found") :
            tool.IsBundled ? L.Tr("dfu-util: bundled with Mission Planner") : L.Format("dfu-util: {0}", tool.Path);
    }

    private DfuFlasher CreateFlasher() => new(_dfu.Runner, _dfu.Locator)
    {
        Log = AddLogFromWorker,
        Progress = new DispatcherProgress<UploadProgress>(Services.Dispatcher, p =>
        {
            if (IsBusy)
                SetStatus(p.Text, p.Percent);
        }),
    };

    /// <summary>Linux: re-reads sysfs. Elsewhere: runs <c>dfu-util -l</c> (only here, when asked).</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task CheckDfu()
    {
        if (_dfu.ScanDevices != null)
        {
            var devices = await Task.Run(ScanDfuDevices) ?? [];
            ApplyDfuDevices(devices, null);
            return;
        }

        if (!BeginWork("Looking for a board in DFU mode…"))
            return;
        try
        {
            AddLog("Listing DFU devices (dfu-util -l).");
            var (found, problem) = await CreateFlasher().ListDevicesAsync(_cancel!.Token);
            ApplyDfuDevices(found, problem);
            foreach (var d in found)
                AddLog("DFU: " + d.Description);
            if (problem != null)
                AddLog(problem);
            SetStatus(DfuStatusText, found.Count > 0 ? 100 : -1);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Cancelled.", -1);
        }
        finally
        {
            EndWork();
        }
    }

    [RelayCommand(CanExecute = nameof(CanInstallSelected))]
    private async Task InstallSelectedViaDfu()
    {
        if (Options?.Selected is { } row)
            await DfuInstallAsync(row);
    }

    /// <summary>"Install via DFU": the row's "_with_bl.hex" (manifest, else derived and checked), downloaded, converted and flashed.</summary>
    internal async Task DfuInstallAsync(FirmwareRow row)
    {
        if (!BeginWork("Looking for the DFU image (bootloader and firmware)…"))
            return;
        var cancel = _cancel!.Token;
        var temp = Path.Combine(Path.GetTempPath(), $"mp-firmware-{Guid.NewGuid():N}.hex");
        try
        {
            var source = await DfuHexLocator.FindAsync(_catalog?.All, row.Info, _dfu.UrlExists, cancel);
            if (source == null)
            {
                var none = L.Format(
                    "ArduPilot publishes no bootloader + firmware image (_with_bl.hex) for {0}, so it cannot be flashed over DFU from here.",
                    row.Platform);
                SetStatus(none, -1);
                AddLog(none);
                await Services.Dialogs.ShowMessageAsync("Install via DFU", none, MessageKind.Error);
                return;
            }

            AddLog((source.FromManifest ? "DFU image (from the manifest): " : "DFU image (not in the manifest, found on the server): ") +
                   source.Url.AbsoluteUri);

            var flasher = CreateFlasher();
            if (!await DfuReadyAsync(flasher, cancel))
                return;

            var file = Path.GetFileName(source.Url.AbsolutePath);
            if (!await Services.Dialogs.ConfirmAsync("Install via DFU",
                    L.Format("Flash {0} over DFU?", row.Summary) + "\n\n" + file + "\n\n" + DfuWarning(null, ardupilot: true),
                    "Flash via DFU"))
            {
                SetStatus("Not flashed.", -1);
                return;
            }

            SetStatus("Downloading firmware…", -1);
            AddLog("Downloading " + source.Url.AbsoluteUri);
            await _dfu.Download(source.Url, temp, new DispatcherProgress<double>(Services.Dispatcher, f =>
            {
                if (IsBusy)
                    SetStatus("Downloading firmware…", f < 0 ? -1 : f * 100);
            }), cancel);
            AddLog("Downloaded.");

            var image = await Task.Run(() => IntelHex.Load(temp), cancel);
            AddLog(DescribeImage(image));
            if (!image.StartsAtFlashStart)
            {
                var bad = L.Format("The downloaded image starts at 0x{0:X8}, not at 0x08000000: it is not a bootloader + firmware image. Nothing was flashed.",
                    image.StartAddress);
                SetStatus(bad, -1);
                await Services.Dialogs.ShowMessageAsync("Install via DFU", bad, MessageKind.Error);
                return;
            }

            await RunDfuFlashAsync(flasher, image);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Cancelled.", -1);
            AddLog("Cancelled.");
        }
        catch (FormatException ex)
        {
            SetStatus(L.Tr("The firmware file is damaged:") + " " + ex.Message, -1);
            AddLog("Bad hex file: " + ex.Message);
            await Services.Dialogs.ShowMessageAsync("Install via DFU", L.Tr("The firmware file is damaged:") + "\n" + ex.Message, MessageKind.Error);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException)
        {
            Log.Warn("DFU firmware download", ex);
            SetStatus(L.Tr("Download failed:") + " " + ex.Message, -1);
            AddLog("Download failed: " + ex.Message);
            await Services.Dialogs.ShowMessageAsync("Install via DFU", L.Tr("Failed to download the firmware:") + "\n" + ex.Message, MessageKind.Error);
        }
        catch (Exception ex)
        {
            await DfuFailedAsync(ex);
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception)
            {
            }

            EndWork();
        }
    }

    /// <summary>A .hex (any layout inside the STM32 flash) or a .bin (written from 0x08000000) from "Load custom firmware".</summary>
    private async Task DfuInstallFileAsync(string path, bool isHex)
    {
        FlashImage image;
        try
        {
            image = await Task.Run(() => isHex ? IntelHex.Load(path) : FlashImage.FromBinary(File.ReadAllBytes(path)));
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            await Services.Dialogs.ShowMessageAsync("Load custom firmware", L.Tr("Cannot read the firmware file:") + "\n" + ex.Message, MessageKind.Error);
            return;
        }

        if (image.StartAddress < FlashImage.Stm32FlashStart || image.EndAddress > Stm32FlashEnd)
        {
            await Services.Dialogs.ShowMessageAsync("Load custom firmware",
                L.Format("The image covers {0}, outside the STM32 internal flash (from 0x08000000). It cannot be flashed over DFU.", image.RangeText),
                MessageKind.Error);
            return;
        }

        if (!BeginWork("Checking dfu-util…"))
            return;
        var cancel = _cancel!.Token;
        try
        {
            AddLog($"{Path.GetFileName(path)}: " + DescribeImage(image));
            var flasher = CreateFlasher();
            if (!await DfuReadyAsync(flasher, cancel))
                return;

            var details = L.Format("Image: {0}, {1:N0} KB", image.RangeText, (image.Data.Length + 1023) / 1024) +
                          (isHex ? "" : "\n" + L.Tr("A .bin is written as it is from 0x08000000, the start of the flash."));
            if (!await Services.Dialogs.ConfirmAsync("Install via DFU",
                    L.Format("Flash {0} over DFU?", Path.GetFileName(path)) + "\n\n" + details + "\n\n" + DfuWarning(image, ardupilot: false),
                    "Flash via DFU"))
            {
                SetStatus("Not flashed.", -1);
                return;
            }

            await RunDfuFlashAsync(flasher, image);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Cancelled.", -1);
            AddLog("Cancelled.");
        }
        catch (Exception ex)
        {
            await DfuFailedAsync(ex);
        }
        finally
        {
            EndWork();
        }
    }

    private async Task DfuFailedAsync(Exception ex)
    {
        Log.Warn("DFU install", ex);
        var text = L.Tr("Flashing failed:") + " " + ex.Message;
        SetStatus(text, -1);
        AddLog(text);
        LastDfuOutcome = DfuOutcome.Failed;
        await Services.Dialogs.ShowMessageAsync("Install via DFU", text, MessageKind.Error);
    }

    /// <summary>End of the address range a DFU image may cover (STM32 internal flash is at most a few MB from 0x08000000).</summary>
    private const ulong Stm32FlashEnd = 0x0900_0000;

    /// <summary>A dfu-util starts and, where the OS lists USB devices without opening them, one board is in DFU mode.</summary>
    private async Task<bool> DfuReadyAsync(DfuFlasher flasher, CancellationToken cancel)
    {
        SetStatus("Checking dfu-util…", -1);
        var tool = await flasher.FindToolAsync(cancel);
        if (tool == null)
        {
            var missing = flasher.MissingToolMessage();
            SetStatus(missing, -1);
            AddLog(missing);
            LastDfuOutcome = DfuOutcome.NoTool;
            await Services.Dialogs.ShowMessageAsync("Install via DFU", missing, MessageKind.Error);
            return false;
        }

        DfuToolText = tool.Text;

        if (ScanDfuDevices() is { } devices)
        {
            ApplyDfuDevices(devices, null);
            if (devices.Count != 1)
            {
                var text = devices.Count == 0
                    ? L.Tr("No board in DFU mode is on USB.") + "\n\n" + DfuFlasher.HowToEnterDfu
                    : L.Tr("More than one board is in DFU mode. Unplug all but the one to flash, then try again.");
                SetStatus(devices.Count == 0 ? "No board in DFU mode is on USB." : "More than one board is in DFU mode.", -1);
                AddLog(devices.Count == 0 ? "No board in DFU mode (0483:df11) on USB." : $"{devices.Count} boards in DFU mode.");
                LastDfuOutcome = DfuOutcome.NoDevice;
                await Services.Dialogs.ShowMessageAsync("Install via DFU", text, MessageKind.Warning);
                return false;
            }

            AddLog("Board in DFU mode: " + devices[0].Description);
        }

        return true;
    }

    private async Task RunDfuFlashAsync(DfuFlasher flasher, FlashImage image)
    {
        AddLog($"Flashing {image.Data.Length:N0} bytes at 0x{image.StartAddress:X8} over DFU.");
        var result = await flasher.FlashAsync(image, _cancel!.Token);
        LastDfuOutcome = result.Outcome;
        SetStatus(result.Message, result.Outcome == DfuOutcome.Flashed ? 100 : -1);
        AddLog(result.Message);

        switch (result.Outcome)
        {
            case DfuOutcome.Flashed:
                AddLog("Connect the board over USB once it has started; it needs its setup (frame type, calibration).");
                await Services.Dialogs.ShowMessageAsync("Install via DFU", result.Message + "\n\n" + L.Tr(
                    "Wait a few seconds, then connect it over USB: it appears as a serial port (choose it at the top and click Connect). " +
                    "Before it flies it needs its setup: frame type, accelerometer and compass calibration, radio calibration, flight modes and failsafes. " +
                    "If it does not start, unplug it and plug it back in."));
                ScanDfuAfterFlash();
                break;
            case DfuOutcome.Cancelled:
                break;
            default:
                await Services.Dialogs.ShowMessageAsync("Install via DFU", result.Message, MessageKind.Error);
                break;
        }
    }

    private void ScanDfuAfterFlash()
    {
        if (ScanDfuDevices() is { } devices)
            ApplyDfuDevices(devices, null);
    }

    private static string DescribeImage(FlashImage image) =>
        $"image {image.RangeText}, {image.Data.Length:N0} bytes" +
        (image.GapBytes > 0 ? $" ({image.GapBytes:N0} bytes between blocks filled with 0xFF)" : "") +
        (image.StartsAtFlashStart ? ", from the start of the flash (with bootloader)" : "") + ".";

    /// <summary>What a DFU flash replaces, for the confirmation.</summary>
    private static string DfuWarning(FlashImage? image, bool ardupilot)
    {
        string text;
        if (image == null || image.StartsAtFlashStart)
            text = ardupilot
                ? L.Tr("This replaces everything on the board: the whole firmware it has now (for example iNav or Betaflight) and the bootloader are overwritten with ArduPilot's bootloader and firmware. The board's current settings (iNav's, for example) are lost; save them first if you want them (iNav/Betaflight CLI: diff all).")
                : L.Tr("This replaces everything on the board: the whole firmware it has now (for example iNav or Betaflight) and the bootloader are overwritten with this file. The board's current settings (iNav's, for example) are lost; save them first if you want them (iNav/Betaflight CLI: diff all).");
        else
            text = L.Format("This overwrites the board's flash from 0x{0:X8} to 0x{1:X8}. The image does not start at 0x08000000, so it holds no bootloader; the board's current settings may be lost.",
                image.StartAddress, image.EndAddress - 1);
        return text + "\n\n" + L.Tr("The board must be in DFU mode. Do not unplug it while it is being flashed.");
    }

    // ---- busy state, progress and log ----------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(LoadCustomFirmwareCommand), nameof(DetectBootloaderCommand),
        nameof(InstallSelectedOptionCommand), nameof(InstallSelectedViaDfuCommand), nameof(CheckDfuCommand), nameof(CancelCommand))]
    private bool _isBusy;

    public bool IsIdle => !IsBusy;

    [ObservableProperty] private string _statusText = "Connect the board with USB, then choose the vehicle firmware to install.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressValue))]
    private double _progressPercent;

    [ObservableProperty] private bool _isProgressIndeterminate;

    public double ProgressValue => Math.Clamp(ProgressPercent, 0, 100);

    public ObservableCollection<string> LogLines { get; } = new();

    private bool CanStart() => !IsBusy;

    private bool CanCancel() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cancel?.Cancel();

    private bool BeginWork(string status)
    {
        if (IsBusy)
            return false;
        _cancel?.Dispose();
        _cancel = new CancellationTokenSource();
        IsBusy = true;
        SetStatus(status, -1);
        return true;
    }

    private void EndWork()
    {
        IsBusy = false;
        IsProgressIndeterminate = false;
    }

    private void SetStatus(string text, double percent)
    {
        StatusText = text;
        IsProgressIndeterminate = percent < 0 && IsBusy;
        if (percent >= 0)
            ProgressPercent = percent;
    }

    private void AddLogFromWorker(string line) => Services.Dispatcher.Post(() => AddLog(line));

    private void AddLog(string line)
    {
        LogLines.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        while (LogLines.Count > MaxLogLines)
            LogLines.RemoveAt(0);
    }

    // ---- page lifecycle ------------------------------------------------------------------------

    public override void OnShown()
    {
        _shown = true;
        UpdateDfuToolText();
        if (_catalog == null && !IsLoading)
            _ = LoadManifestAsync(force: false);
        _ = ScanPortsAsync();
    }

    public override void OnHidden() => _shown = false;

    /// <summary>Polls the port list every 2 s while visible (no hot-plug events off Windows).</summary>
    public override void Tick()
    {
        if (!_shown || IsBusy)
            return;
        if (++_tick % 40 == 0)
            _ = ScanPortsAsync();
    }
}
