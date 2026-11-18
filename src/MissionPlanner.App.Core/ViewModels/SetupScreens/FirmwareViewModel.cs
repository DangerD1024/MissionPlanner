using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
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
/// </summary>
public sealed partial class FirmwareViewModel : PageViewModel
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(FirmwareViewModel));
    private const int MaxLogLines = 400;

    private readonly IBootloaderFactory _bootloaders;
    private readonly Func<IReadOnlyList<SerialDevice>> _scanPorts;
    private readonly Func<IReadOnlyList<string>> _probePorts;
    private FirmwareCatalog? _catalog;
    private CancellationTokenSource? _cancel;
    private bool _scanning;
    private int _tick;
    private bool _shown;

    public FirmwareViewModel(AppServices services)
        : this(services, SerialBootloaderFactory.Instance, SerialDeviceScanner.Scan, SerialDeviceScanner.ProbePorts)
    {
    }

    /// <summary>For tests: fake bootloaders and port lists.</summary>
    internal FirmwareViewModel(AppServices services, IBootloaderFactory bootloaders,
        Func<IReadOnlyList<SerialDevice>> scanPorts, Func<IReadOnlyList<string>> probePorts)
        : base("Install Firmware")
    {
        Services = services;
        _bootloaders = bootloaders;
        _scanPorts = scanPorts;
        _probePorts = probePorts;
        Tiles = VehicleTile.CreateAll();
        _selectedRelease = ReleaseOption.All[0];
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
            var devices = await Task.Run(() =>
            {
                try
                {
                    return _scanPorts();
                }
                catch (Exception ex)
                {
                    Log.Warn("port scan", ex);
                    return (IReadOnlyList<SerialDevice>)[];
                }
            });
            if (!devices.SequenceEqual(Ports.Select(p => p.Device)))
                RebuildPortRows(devices);
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
                InstallSelectedOptionCommand.NotifyCanExecuteChanged();
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
            new FileFilter("ArduPilot firmware", "apj", "px4"), new FileFilter("All files", "*"));
        if (string.IsNullOrEmpty(path))
            return;

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".hex" or ".bin" or ".dfu")
        {
            await Services.Dialogs.ShowMessageAsync("Load custom firmware",
                "Flashing over DFU (.hex, .bin, .dfu) is not supported here. Use the board's .apj file, which installs through the bootloader.",
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

    // ---- busy state, progress and log ----------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(LoadCustomFirmwareCommand), nameof(DetectBootloaderCommand),
        nameof(InstallSelectedOptionCommand), nameof(CancelCommand))]
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
