using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.Maps;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.Session;
using MissionPlanner.App.Core.ViewModels.FlightData;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Firmware;
using MissionPlanner.Utilities;
using ReleaseType = MissionPlanner.ArduPilot.APFirmware.RELEASE_TYPES;

namespace MissionPlanner.App.Core.ViewModels.Simulation;

/// <summary>A vehicle picture of the Simulation page.</summary>
public sealed partial class SitlVehicleTile(SitlVehicle vehicle, string title, string icon) : ObservableObject
{
    public SitlVehicle Vehicle { get; } = vehicle;

    public string Title { get; } = title;

    public string Icon { get; } = icon;

    public string ModelText => "-M " + SitlVehicles.DefaultModel(Vehicle);

    [ObservableProperty] private bool _isSelected;

    [ObservableProperty] private string _versionText = "";

    /// <summary>A copy is in the sitl folder already.</summary>
    [ObservableProperty] private bool _isDownloaded;
}

/// <summary>
/// Simulation: download ArduPilot SITL for this machine, start it at a chosen home and connect
/// to it over TCP 5760 (GCSViews/SITL.cs). The simulator's console output is shown on the page.
/// </summary>
public sealed partial class SimulationViewModel : PageViewModel
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(SimulationViewModel));
    public const string DefaultModelOption = "Default for the vehicle";
    private const int MaxConsoleLines = 1500;

    private readonly ConcurrentQueue<string> _pending = new();
    private SitlProcess? _process;
    private CancellationTokenSource? _cancel;
    private LinkSpec? _sitlLink;
    private int _altRequest;

    public SimulationViewModel(AppServices services)
        : base("Simulation")
    {
        Services = services;
        Host = SitlPlatform.Current;
        SitlDir = Path.Combine(Settings.GetUserDataDirectory(), "sitl");

        Vehicles =
        [
            new SitlVehicleTile(SitlVehicle.Plane, "Plane", "plane"),
            new SitlVehicleTile(SitlVehicle.Copter, "Copter", "copter"),
            new SitlVehicleTile(SitlVehicle.Rover, "Rover", "rover"),
            new SitlVehicleTile(SitlVehicle.Heli, "Heli", "heli"),
        ];
        ModelOptions = [DefaultModelOption, .. SitlVehicles.ModelOverrides];
        Releases =
        [
            new ReleaseOption(ReleaseType.OFFICIAL, "Stable"),
            new ReleaseOption(ReleaseType.BETA, "Beta"),
            new ReleaseOption(ReleaseType.DEV, "Dev (latest)"),
        ];

        MapProviders = MapTiles.CommonProviders.Select(p => new MapProviderOption(p.Name, p)).ToArray();
        _selectedMapProvider = MapProviders.FirstOrDefault(p => p.Name == Settings.Instance["MapType"]) ?? MapProviders[0];

        LoadSettings();
        _mapCenterLat = Home.Lat;
        _mapCenterLng = Home.Lng;

        services.Session.StateChanged += (_, _) => services.Dispatcher.Post(() => OnPropertyChanged(nameof(ConnectionText)));
    }

    public AppServices Services { get; }

    public SitlHost Host { get; }

    public string SitlDir { get; }

    public string HostText => "Simulator build: " + SitlPlatform.Describe(Host);

    public bool IsSupported => Host != SitlHost.Unsupported;

    // ---- vehicle, model, release ----------------------------------------------------------------

    public IReadOnlyList<SitlVehicleTile> Vehicles { get; }

    [ObservableProperty] private SitlVehicleTile? _selectedVehicle;

    partial void OnSelectedVehicleChanged(SitlVehicleTile? value)
    {
        foreach (var v in Vehicles)
            v.IsSelected = ReferenceEquals(v, value);
        OnPropertyChanged(nameof(ModelText));
        StartCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void SelectVehicle(SitlVehicleTile tile) => SelectedVehicle = tile;

    public IReadOnlyList<string> ModelOptions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelText))]
    private string _selectedModel = DefaultModelOption;

    /// <summary>The -M model the simulator will get.</summary>
    public string Model => SelectedModel is { Length: > 0 } m && m != DefaultModelOption
        ? m
        : SitlVehicles.DefaultModel(SelectedVehicle?.Vehicle ?? SitlVehicle.Copter);

    public string ModelText => "Model: " + Model;

    public IReadOnlyList<ReleaseOption> Releases { get; }

    [ObservableProperty] private ReleaseOption? _selectedRelease;

    partial void OnSelectedReleaseChanged(ReleaseOption? value) => UpdateTiles();

    // ---- home -----------------------------------------------------------------------------------

    [ObservableProperty] private string _latText = "";
    [ObservableProperty] private string _lngText = "";
    [ObservableProperty] private string _altText = "";
    [ObservableProperty] private string _headingText = "";

    [ObservableProperty] private string _homeError = "";

    /// <summary>Raised when the home moves, so the map redraws its marker.</summary>
    public event EventHandler? HomeChanged;

    public SitlHome Home
    {
        get
        {
            var inv = CultureInfo.InvariantCulture;
            var ok = double.TryParse(LatText, NumberStyles.Float, inv, out var lat) &
                     double.TryParse(LngText, NumberStyles.Float, inv, out var lng);
            if (!double.TryParse(AltText, NumberStyles.Float, inv, out var alt))
                alt = 0;
            if (!double.TryParse(HeadingText, NumberStyles.Float, inv, out var hdg))
                hdg = 0;
            return ok ? new SitlHome(lat, lng, alt, hdg) : new SitlHome(double.NaN, double.NaN, alt, hdg);
        }
    }

    partial void OnLatTextChanged(string value) => OnHomeTextChanged();
    partial void OnLngTextChanged(string value) => OnHomeTextChanged();

    private void OnHomeTextChanged()
    {
        HomeError = Home.IsValid ? "" : "Enter the home latitude and longitude in degrees (e.g. -35.363261 and 149.165230).";
        HomeChanged?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(CommandPreview));
        StartCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Moves home (map click, map centre, CMAC); the ground altitude follows from the terrain tiles when known.</summary>
    public void SetHome(double lat, double lng, double? alt = null)
    {
        var inv = CultureInfo.InvariantCulture;
        LatText = lat.ToString("0.#######", inv);
        LngText = lng.ToString("0.#######", inv);
        if (alt is { } a)
        {
            AltText = a.ToString("0.#", inv);
            return;
        }

        var request = ++_altRequest;
        _ = Task.Run(() =>
        {
            try
            {
                var r = srtm.getAltitude(lat, lng);
                return r.currenttype == srtm.tiletype.valid ? r.alt : r.currenttype == srtm.tiletype.ocean ? 0 : (double?)null;
            }
            catch (Exception)
            {
                return null;
            }
        }).ContinueWith(t =>
        {
            if (t.Result is { } ground)
                Services.Dispatcher.Post(() =>
                {
                    if (request == _altRequest)
                        AltText = ground.ToString("0.#", inv);
                });
        }, TaskScheduler.Default);
    }

    [RelayCommand]
    private void UseCmac()
    {
        var c = SitlHome.Cmac;
        SetHome(c.Lat, c.Lng, c.Alt);
        HeadingText = c.Heading.ToString(CultureInfo.InvariantCulture);
        CenterMap(c.Lat, c.Lng);
    }

    [RelayCommand]
    private void UseMapCentre() => SetHome(MapCenterLat, MapCenterLng);

    public bool HasPlannedHome => Services.Mission.HomeSet;

    [RelayCommand]
    private void UsePlannedHome()
    {
        var m = Services.Mission;
        if (!m.HomeSet)
            return;
        SetHome(m.HomeLat, m.HomeLng, m.HomeAlt);
        CenterMap(m.HomeLat, m.HomeLng);
    }

    /// <summary>A click on the page's map, or the end of a drag of the home marker.</summary>
    public void MapClicked(double lat, double lng) => SetHome(lat, lng);

    /// <summary>While the home marker is dragged: position only, the ground altitude follows on release.</summary>
    public void MoveHome(double lat, double lng)
    {
        var inv = CultureInfo.InvariantCulture;
        LatText = lat.ToString("0.#######", inv);
        LngText = lng.ToString("0.#######", inv);
    }

    // ---- map ------------------------------------------------------------------------------------

    public IReadOnlyList<MapProviderOption> MapProviders { get; }

    [ObservableProperty] private MapProviderOption _selectedMapProvider;

    [ObservableProperty] private double _mapCenterLat;
    [ObservableProperty] private double _mapCenterLng;
    [ObservableProperty] private int _mapZoom = 15;

    private void CenterMap(double lat, double lng)
    {
        MapCenterLat = lat;
        MapCenterLng = lng;
    }

    // ---- options --------------------------------------------------------------------------------

    [ObservableProperty] private string _extraArguments = "";

    [ObservableProperty] private bool _wipe;

    [ObservableProperty] private decimal _speedUp = 1;

    /// <summary>The command line the simulator would get now (shown under the options).</summary>
    public string CommandPreview => SitlArguments.Build(new SitlLaunch(Model, Home, (int)SpeedUp,
        Path.Combine("sitl", "<defaults>.parm"), ExtraArguments, Wipe));

    partial void OnExtraArgumentsChanged(string value) => OnPropertyChanged(nameof(CommandPreview));
    partial void OnWipeChanged(bool value) => OnPropertyChanged(nameof(CommandPreview));
    partial void OnSpeedUpChanged(decimal value) => OnPropertyChanged(nameof(CommandPreview));
    partial void OnSelectedModelChanged(string value) => OnPropertyChanged(nameof(CommandPreview));
    partial void OnAltTextChanged(string value) => OnPropertyChanged(nameof(CommandPreview));
    partial void OnHeadingTextChanged(string value)
    {
        OnPropertyChanged(nameof(CommandPreview));
        HomeChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- running --------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(CancelCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isRunning;

    [ObservableProperty] private string _statusText = "Choose a vehicle and a start location, then Start.";

    [ObservableProperty] private double _progressPercent;

    [ObservableProperty] private bool _isProgressIndeterminate;

    public ObservableCollection<string> ConsoleLines { get; } = new();

    public string ConnectionText => Services.Session.State switch
    {
        LinkState.Connected when _sitlLink != null && Services.Session.Link == _sitlLink => "Connected to the simulator on TCP " + _sitlLink.Value,
        LinkState.Connected => "Connected to " + Services.Session.Link,
        LinkState.Connecting => "Connecting…",
        _ => "Not connected",
    };

    private bool CanStart() => !IsBusy && IsSupported && SelectedVehicle != null && Home.IsValid;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        var vehicle = SelectedVehicle!.Vehicle;
        var release = SelectedRelease ?? Releases[0];
        var home = Home;
        var model = Model;

        if (Services.Session.State != LinkState.Disconnected)
        {
            if (!await Services.Dialogs.ConfirmAsync("Start simulator",
                    $"Disconnect from {Services.Session.Link} and start the simulator?", "Disconnect and start"))
                return;
            await Services.Session.DisconnectAsync();
        }

        StopProcess();
        SaveSettings();

        _cancel?.Dispose();
        _cancel = new CancellationTokenSource();
        var cancel = _cancel.Token;
        IsBusy = true;
        try
        {
            Directory.CreateDirectory(SitlDir);

            var exe = await EnsureBinaryAsync(vehicle, release, cancel);
            if (exe == null)
                return;

            SetStatus("Getting the default parameters…", -1);
            var defaults = await SitlDefaults.ResolveAsync(FirmwareManifest.Http, SitlDir, model, AddConsoleFromWorker, cancel);

            var launch = new SitlLaunch(model, home, (int)SpeedUp, defaults, ExtraArguments, Wipe);
            var args = SitlArguments.Build(launch);
            var workDir = SitlArguments.WorkingDirectory(SitlDir, model);
            Directory.CreateDirectory(workDir);

            var psi = SitlProcess.CreateStartInfo(exe, args, workDir, SitlDir, Host == SitlHost.WindowsCygwin);
            AddConsole($"$ cd \"{workDir}\"");
            AddConsole($"$ \"{exe}\" {args}");
            SetStatus("Starting the simulator…", -1);

            SitlProcess? process = null;
            try
            {
                process = SitlProcess.Start(psi, line => _pending.Enqueue(line),
                    code => Services.Dispatcher.Post(() => OnProcessExited(process, code)));
            }
            catch (Exception ex)
            {
                Log.Warn("start sitl", ex);
                SetStatus("Failed to start the simulator: " + ex.Message, -1);
                await Services.Dialogs.ShowMessageAsync("Simulation", "Failed to start the simulator:\n" + ex.Message, MessageKind.Error);
                return;
            }

            _process = process;
            IsRunning = true;

            await ConnectAsync(process, cancel);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Cancelled.", -1);
        }
        catch (Exception ex)
        {
            Log.Warn("sitl", ex);
            SetStatus("Simulation failed: " + ex.Message, -1);
            await Services.Dialogs.ShowMessageAsync("Simulation", ex.Message, MessageKind.Error);
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
            OnPropertyChanged(nameof(ConnectionText));
            UpdateTiles();
        }
    }

    /// <summary>Finds, offers to update or downloads the simulator; null when there is none to run.</summary>
    private async Task<string?> EnsureBinaryAsync(SitlVehicle vehicle, ReleaseOption release, CancellationToken cancel)
    {
        var exe = Path.Combine(SitlDir, SitlPlatform.ExecutableName(Host, vehicle));
        var exists = File.Exists(exe);
        var local = SitlBinaryStore.ReadInfo(exe);
        var title = Vehicles.First(v => v.Vehicle == vehicle).Title;

        SitlBuild? offered = null;
        if (SitlPlatform.ManifestPlatform(Host) is { } platform)
        {
            SetStatus("Getting the firmware list…", -1);
            try
            {
                var catalog = await FirmwareManifest.LoadAsync(cancel: cancel);
                var f = catalog.ForPlatform(platform, SitlVehicles.ManifestType(vehicle), release.Type);
                if (f != null)
                    offered = SitlBuild.From(f);
            }
            catch (InvalidOperationException ex)
            {
                AddConsole(ex.Message);
            }
        }
        else if (Host == SitlHost.WindowsCygwin)
        {
            offered = new SitlBuild(SitlPlatform.CygwinExeUrl(release.Type, vehicle), null, release.Type.ToString(), null);
        }

        switch (SitlBinaryStore.Decide(exists, local, offered))
        {
            case SitlBinaryAction.Unavailable:
                await Services.Dialogs.ShowMessageAsync("Simulation",
                    $"No {release.Name} {title} simulator is available for {SitlPlatform.Describe(Host)}, and none is downloaded.",
                    MessageKind.Error);
                SetStatus("No simulator to run.", -1);
                return null;

            case SitlBinaryAction.Reuse:
                AddConsole($"Using {exe}" + (local != null ? $" ({local.Describe()})" : ""));
                return exe;

            case SitlBinaryAction.AskUpdate:
                var have = local?.Describe() ?? "an unknown build";
                var want = string.Join(" ", new[] { offered!.Version, release.Name }.Where(s => !string.IsNullOrEmpty(s)));
                if (!await Services.Dialogs.ConfirmAsync("Simulation",
                        $"A {title} simulator is already downloaded ({have}).\n\nDownload {want} now? Choose Cancel to use the one you have.",
                        "Download"))
                {
                    AddConsole($"Using {exe} ({have})");
                    return exe;
                }

                break;
        }

        await DownloadBinaryAsync(exe, offered!, vehicle, release, cancel);
        return exe;
    }

    private async Task DownloadBinaryAsync(string exe, SitlBuild build, SitlVehicle vehicle, ReleaseOption release, CancellationToken cancel)
    {
        var progress = new DispatcherProgress<double>(Services.Dispatcher, f =>
        {
            if (IsBusy)
                SetStatus("Downloading the simulator…", f < 0 ? -1 : f * 100);
        });

        AddConsole("Downloading " + build.Url);
        await FileDownloader.DownloadAsync(FirmwareManifest.Http, build.Url, exe, progress, cancel);

        if (Host == SitlHost.WindowsCygwin)
        {
            var baseUrl = SitlPlatform.CygwinBaseUrl(release.Type, vehicle);
            foreach (var dll in SitlPlatform.CygwinDlls)
            {
                AddConsole("Downloading " + dll);
                await FileDownloader.DownloadAsync(FirmwareManifest.Http, new Uri(baseUrl, dll), Path.Combine(SitlDir, dll), null, cancel);
            }
        }

        SitlBinaryStore.MakeExecutable(exe);
        SitlBinaryStore.WriteInfo(exe, build.ToInfo(DateTime.UtcNow));
        AddConsole("Downloaded.");
    }

    /// <summary>Waits for the simulator's TCP port and connects; SITL listens once it has started.</summary>
    private async Task ConnectAsync(SitlProcess process, CancellationToken cancel)
    {
        var spec = LinkSpec.Tcp("127.0.0.1", SitlArguments.TcpPort());
        _sitlLink = spec;
        var progress = new DispatcherProgress<OperationProgress>(Services.Dispatcher, p =>
        {
            if (IsBusy)
                SetStatus(string.IsNullOrEmpty(p.Status) ? "Connecting…" : p.Status, p.Percent);
        });

        SetStatus("Waiting for the simulator…", -1);
        await Task.Delay(2000, cancel);

        var start = Environment.TickCount64;
        while (true)
        {
            if (process.HasExited)
            {
                SetStatus($"The simulator exited (code {process.ExitCode}). See its output below.", -1);
                return;
            }

            try
            {
                SetStatus("Connecting to the simulator on TCP " + spec.Value + "…", -1);
                await Services.Session.ConnectAsync(spec, getParams: true, progress, cancel);
                SetStatus("Simulator running and connected.", 100);
                // as the WinForms app does: go fly it
                Services.ShowScreen(Screen.Data);
                return;
            }
            catch (ConnectException ex)
            {
                if (Environment.TickCount64 - start > 40_000)
                {
                    SetStatus("Could not connect to the simulator: " + ex.Message, -1);
                    await Services.Dialogs.ShowMessageAsync("Simulation",
                        "The simulator is running but did not accept a connection on TCP " + spec.Value + ":\n" + ex.Message,
                        MessageKind.Error);
                    return;
                }

                await Task.Delay(1000, cancel);
            }
            catch (InvalidOperationException)
            {
                // something else connected meanwhile
                SetStatus("Simulator running. Another connection is open; disconnect it and connect to TCP 127.0.0.1:" + spec.Value + ".", -1);
                return;
            }
        }
    }

    private bool CanStop() => IsRunning || IsBusy;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task Stop()
    {
        _cancel?.Cancel();
        if (_sitlLink != null && Services.Session.Link == _sitlLink && Services.Session.State != LinkState.Disconnected)
            await Services.Session.DisconnectAsync();
        StopProcess();
        SetStatus("Simulator stopped.", -1);
        OnPropertyChanged(nameof(ConnectionText));
    }

    private bool CanCancel() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cancel?.Cancel();

    private void StopProcess()
    {
        var p = _process;
        _process = null;
        if (p != null)
        {
            p.Kill();
            p.WaitForExit(TimeSpan.FromSeconds(3));
            p.Dispose();
            AddConsole("Simulator stopped.");
        }

        IsRunning = false;
    }

    private void OnProcessExited(SitlProcess? process, int code)
    {
        FlushConsole();
        if (process == null || !ReferenceEquals(process, _process))
            return; // a stopped (or replaced) simulator
        _process.Dispose();
        _process = null;
        IsRunning = false;
        AddConsole($"Simulator exited with code {code}.");
        if (!IsBusy)
            SetStatus($"The simulator exited (code {code}).", -1);

        // nothing will answer on its port again: drop the link rather than "Reconnecting" for ever,
        // so Start simulation is available straight away
        if (_sitlLink != null && Services.Session.Link == _sitlLink && Services.Session.State != LinkState.Disconnected)
            DisconnectAfterExit();
    }

    private async void DisconnectAfterExit()
    {
        try
        {
            await Services.Session.DisconnectAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("disconnect after sitl exit", ex);
        }

        OnPropertyChanged(nameof(ConnectionText));
    }

    [RelayCommand]
    private void ClearConsole() => ConsoleLines.Clear();

    // ---- helpers --------------------------------------------------------------------------------

    private void SetStatus(string text, double percent)
    {
        StatusText = text;
        IsProgressIndeterminate = percent < 0 && IsBusy;
        if (percent >= 0)
            ProgressPercent = Math.Clamp(percent, 0, 100);
    }

    private void AddConsoleFromWorker(string line) => _pending.Enqueue(line);

    private void AddConsole(string line)
    {
        FlushConsole();
        ConsoleLines.Add(line);
        TrimConsole();
    }

    private void FlushConsole()
    {
        var n = 0;
        while (n++ < 500 && _pending.TryDequeue(out var line))
            ConsoleLines.Add(line);
        TrimConsole();
    }

    private void TrimConsole()
    {
        while (ConsoleLines.Count > MaxConsoleLines)
            ConsoleLines.RemoveAt(0);
    }

    private void UpdateTiles()
    {
        var release = SelectedRelease ?? Releases[0];
        var catalog = FirmwareManifest.Loaded;
        var platform = SitlPlatform.ManifestPlatform(Host);
        foreach (var t in Vehicles)
        {
            var exe = Path.Combine(SitlDir, SitlPlatform.ExecutableName(Host, t.Vehicle));
            var local = File.Exists(exe);
            var offered = catalog != null && platform != null
                ? catalog.ForPlatform(platform, SitlVehicles.ManifestType(t.Vehicle), release.Type)
                : null;
            t.VersionText = offered?.MavFirmwareVersion != null ? "V" + offered.MavFirmwareVersion : "";
            t.IsDownloaded = local;
        }
    }

    private void LoadSettings()
    {
        var s = Settings.Instance;
        var vehicle = s["sitl_vehicle"];
        SelectedVehicle = Vehicles.FirstOrDefault(v => v.Vehicle.ToString() == vehicle) ?? Vehicles[1];
        SelectedRelease = Releases.FirstOrDefault(r => r.Type.ToString() == s["sitl_release"]) ?? Releases[0];
        var model = s["sitl_model"];
        SelectedModel = !string.IsNullOrEmpty(model) && ModelOptions.Contains(model) ? model : DefaultModelOption;
        ExtraArguments = s["sitl_cmdline"] ?? "";
        if (decimal.TryParse(s["sitl_speedup"], NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) && speed >= 1)
            SpeedUp = speed;

        var home = SitlHome.Cmac;
        var saved = (s["sitl_home"] ?? "").Split(',');
        if (saved.Length == 4 &&
            saved.All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            var v = saved.Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            var candidate = new SitlHome(v[0], v[1], v[2], v[3]);
            if (candidate.IsValid)
                home = candidate;
        }

        var inv = CultureInfo.InvariantCulture;
        LatText = home.Lat.ToString("0.#######", inv);
        LngText = home.Lng.ToString("0.#######", inv);
        AltText = home.Alt.ToString("0.#", inv);
        HeadingText = home.Heading.ToString("0", inv);
    }

    private void SaveSettings()
    {
        try
        {
            var s = Settings.Instance;
            s["sitl_vehicle"] = SelectedVehicle?.Vehicle.ToString() ?? "";
            s["sitl_release"] = (SelectedRelease ?? Releases[0]).Type.ToString();
            s["sitl_model"] = SelectedModel == DefaultModelOption ? "" : SelectedModel;
            s["sitl_cmdline"] = ExtraArguments;
            s["sitl_speedup"] = SpeedUp.ToString(CultureInfo.InvariantCulture);
            s["sitl_home"] = Home.ToString();
            s.Save();
        }
        catch (Exception ex)
        {
            Log.Warn("save sitl settings", ex);
        }
    }

    // ---- page lifecycle -------------------------------------------------------------------------

    public override void OnShown()
    {
        OnPropertyChanged(nameof(HasPlannedHome));
        OnPropertyChanged(nameof(ConnectionText));
        UpdateTiles();
        if (FirmwareManifest.Loaded == null && SitlPlatform.ManifestPlatform(Host) != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await FirmwareManifest.LoadAsync();
                    Services.Dispatcher.Post(UpdateTiles);
                }
                catch (Exception ex)
                {
                    Log.Info("manifest", ex);
                }
            });
        }
    }

    public override void Tick()
    {
        if (!_pending.IsEmpty)
            FlushConsole();
    }
}
