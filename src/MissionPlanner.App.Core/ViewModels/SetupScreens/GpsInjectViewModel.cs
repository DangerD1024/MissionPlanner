using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Rtk;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

public sealed record RtcmSourceOption(RtcmSourceKind Kind, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One RTCM message type seen in the stream.</summary>
public sealed partial class RtcmMessageRow(int type, string description) : ObservableObject
{
    public int Type { get; } = type;

    public string Description { get; } = description;

    [ObservableProperty]
    private long _count;

    [ObservableProperty]
    private string _age = "";

    [ObservableProperty]
    private bool _isStale;
}

/// <summary>A green/red indicator: base position or a constellation's observations.</summary>
public sealed partial class ConstellationIndicator(string name) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private string _detail = "not seen";
}

/// <summary>A satellite's signal-strength bar.</summary>
public sealed partial class SatelliteBar(string label) : ObservableObject
{
    public string Label { get; } = label;

    [ObservableProperty]
    private double _snr;

    /// <summary>Bar height in pixels (25-55 dB-Hz spans the chart, as in Mission Planner).</summary>
    [ObservableProperty]
    private double _barHeight;

    [ObservableProperty]
    private bool _isGood;

    [ObservableProperty]
    private bool _isWeak;
}

public sealed record MountpointItem(NtripMountpoint Mountpoint, string Distance, double DistanceKm)
{
    public string Name => Mountpoint.Mountpoint;

    public string Detail => string.Join("  ·  ", new[]
    {
        Mountpoint.Identifier, Mountpoint.Format, Mountpoint.NavSystem, Mountpoint.Country,
    }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public string Gga => Mountpoint.NeedsGga ? "GGA" : "";
}

/// <summary>
/// RTK/GPS Inject (Mission Planner's ConfigSerialInjectGPS): reads RTCM corrections from an NTRIP
/// caster, a serial base station (optionally configuring a u-blox M8P/F9P) or a TCP/UDP stream and
/// injects them into the vehicle as GPS_RTCM_DATA. Injection keeps running when the page is left.
/// </summary>
public sealed partial class GpsInjectViewModel : PageViewModel
{
    private readonly RtkInjector _injector;
    private readonly RateTracker _inRate = new();
    private readonly RateTracker _outRate = new();
    private readonly Dictionary<int, RtcmMessageRow> _rows = new();
    private readonly Dictionary<string, SatelliteBar> _bars = new();
    private readonly Dictionary<string, ConstellationIndicator> _indicators = new();
    private int _tick;
    private bool _loading;

    public GpsInjectViewModel(AppServices services)
        : base("RTK/GPS Inject")
    {
        Services = services;
        _injector = new RtkInjector(new SessionRtcmSink(services.Session));
        _injector.BasePositionReceived += OnBasePosition;
        _injector.SerialActionCompleted += (_, error) =>
        {
            if (error != null)
                services.Dispatcher.Post(() => _ = services.Dialogs.ShowMessageAsync("u-blox", error, MessageKind.Warning));
        };

        SourceOptions =
        [
            new RtcmSourceOption(RtcmSourceKind.Ntrip, "NTRIP caster"),
            new RtcmSourceOption(RtcmSourceKind.Serial, "Serial base station"),
            new RtcmSourceOption(RtcmSourceKind.TcpClient, "TCP client"),
            new RtcmSourceOption(RtcmSourceKind.UdpListen, "UDP listen"),
        ];

        foreach (var name in new[] { "Base", "GPS", "GLONASS", "Galileo", "BeiDou", "QZSS", "SBAS" })
        {
            var ind = new ConstellationIndicator(name);
            _indicators[name] = ind;
            Indicators.Add(ind);
        }

        LoadSettings();
        RefreshPorts();
        foreach (var b in SavedBasePositions.Load())
            SavedBases.Add(b);
        SavedBases.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSavedBases));
    }

    public AppServices Services { get; }

    public IReadOnlyList<RtcmSourceOption> SourceOptions { get; }

    public IReadOnlyList<int> BaudRates { get; } = [4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600];

    // ---- source selection

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNtrip), nameof(IsSerial), nameof(IsTcp), nameof(IsUdp), nameof(ShowUblox))]
    private RtcmSourceOption _selectedSource = null!;

    public bool IsNtrip => SelectedSource.Kind == RtcmSourceKind.Ntrip;

    public bool IsSerial => SelectedSource.Kind == RtcmSourceKind.Serial;

    public bool IsTcp => SelectedSource.Kind == RtcmSourceKind.TcpClient;

    public bool IsUdp => SelectedSource.Kind == RtcmSourceKind.UdpListen;

    // ---- NTRIP

    [ObservableProperty]
    private string _ntripHost = "";

    [ObservableProperty]
    private decimal _ntripPort = 2101;

    [ObservableProperty]
    private string _ntripMountpoint = "";

    [ObservableProperty]
    private string _ntripUsername = "";

    [ObservableProperty]
    private string _ntripPassword = "";

    [ObservableProperty]
    private bool _ntripTls;

    [ObservableProperty]
    private bool _ntripV1;

    [ObservableProperty]
    private bool _sendGga = true;

    public ObservableCollection<MountpointItem> Mountpoints { get; } = new();

    [ObservableProperty]
    private MountpointItem? _selectedMountpoint;

    [ObservableProperty]
    private string _mountpointStatus = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMountpointsCommand))]
    private bool _isLoadingMountpoints;

    public bool HasMountpoints => Mountpoints.Count > 0;

    [ObservableProperty]
    private string _ggaText = "";

    // ---- serial base

    public ObservableCollection<string> Ports { get; } = new();

    [ObservableProperty]
    private string? _serialPort;

    [ObservableProperty]
    private int _serialBaud = 115200;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUblox))]
    private bool _ubloxConfigure;

    /// <summary>The u-blox base panel applies to a serial base being configured by the page.</summary>
    public bool ShowUblox => IsSerial && UbloxConfigure;

    [ObservableProperty]
    private bool _ubloxMsm4;

    [ObservableProperty]
    private decimal _surveyInSeconds = 60;

    [ObservableProperty]
    private decimal _surveyInAccuracy = 2;

    [ObservableProperty]
    private bool _useFixedPosition;

    [ObservableProperty]
    private decimal _fixedLat;

    [ObservableProperty]
    private decimal _fixedLng;

    [ObservableProperty]
    private decimal _fixedAlt;

    [ObservableProperty]
    private string _surveyInText = "";

    [ObservableProperty]
    private bool _surveyInValid;

    [ObservableProperty]
    private bool _hasSurveyIn;

    public ObservableCollection<SavedBasePosition> SavedBases { get; } = new();

    public bool HasSavedBases => SavedBases.Count > 0;

    // ---- TCP / UDP

    [ObservableProperty]
    private string _tcpHost = "127.0.0.1";

    [ObservableProperty]
    private decimal _tcpPort = 2101;

    [ObservableProperty]
    private decimal _udpPort = 9000;

    [ObservableProperty]
    private bool _logRaw = true;

    // ---- status

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectText), nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(RestartSurveyInCommand), nameof(ApplyBasePositionCommand))]
    private bool _isRunning;

    public bool IsIdle => !IsRunning;

    public string ConnectText => IsRunning ? "Stop" : "Connect";

    [ObservableProperty]
    private string _stateText = "Not connected";

    [ObservableProperty]
    private bool _stateOk;

    [ObservableProperty]
    private bool _stateWarn;

    [ObservableProperty]
    private bool _stateError;

    [ObservableProperty]
    private string _bytesInText = "0 B";

    [ObservableProperty]
    private string _bytesOutText = "0 B";

    [ObservableProperty]
    private string _inRateText = "0 B/s";

    [ObservableProperty]
    private string _outRateText = "0 B/s";

    [ObservableProperty]
    private string _packetsText = "0";

    [ObservableProperty]
    private string _lastMessageText = "—";

    [ObservableProperty]
    private string _errorsText = "";

    [ObservableProperty]
    private string _vehicleGpsText = "—";

    [ObservableProperty]
    private bool _vehicleRtk;

    [ObservableProperty]
    private string _basePositionText = "No base position yet (RTCM 1005/1006).";

    [ObservableProperty]
    private string _baseDistanceText = "";

    [ObservableProperty]
    private bool _hasBasePosition;

    public ObservableCollection<RtcmMessageRow> Messages { get; } = new();

    public ObservableCollection<ConstellationIndicator> Indicators { get; } = new();

    public ObservableCollection<SatelliteBar> Satellites { get; } = new();

    [ObservableProperty]
    private bool _hasSatellites;

    [ObservableProperty]
    private bool _hasMessages;

    /// <summary>The worker, for tests.</summary>
    internal RtkInjector Injector => _injector;

    // ---- settings

    private void LoadSettings()
    {
        _loading = true;
        var s = RtkSettings.Load(Settings.Instance);
        SelectedSource = SourceOptions.First(o => o.Kind == s.Source);
        NtripHost = s.NtripHost;
        NtripPort = s.NtripPort;
        NtripMountpoint = s.NtripMountpoint;
        NtripUsername = s.NtripUsername;
        NtripPassword = s.NtripPassword;
        NtripTls = s.NtripTls;
        NtripV1 = s.NtripV1;
        SendGga = s.SendGga;
        SerialPort = string.IsNullOrEmpty(s.SerialPort) ? null : s.SerialPort;
        SerialBaud = s.SerialBaud;
        UbloxConfigure = s.UbloxConfigure;
        UbloxMsm4 = s.UbloxMsm4;
        SurveyInSeconds = s.SurveyInSeconds;
        SurveyInAccuracy = (decimal)s.SurveyInAccuracyM;
        UseFixedPosition = s.UseFixedPosition;
        FixedLat = (decimal)s.FixedLat;
        FixedLng = (decimal)s.FixedLng;
        FixedAlt = (decimal)s.FixedAlt;
        TcpHost = s.TcpHost;
        TcpPort = s.TcpPort;
        UdpPort = s.UdpPort;
        LogRaw = s.LogRaw;
        _loading = false;
    }

    public RtkSettings CurrentSettings() => new()
    {
        Source = SelectedSource.Kind,
        NtripHost = NtripHost.Trim(),
        NtripPort = (int)NtripPort,
        NtripMountpoint = NtripMountpoint.Trim(),
        NtripUsername = NtripUsername,
        NtripPassword = NtripPassword,
        NtripTls = NtripTls,
        NtripV1 = NtripV1,
        SendGga = SendGga,
        SerialPort = SerialPort ?? "",
        SerialBaud = SerialBaud,
        UbloxConfigure = UbloxConfigure,
        UbloxMsm4 = UbloxMsm4,
        SurveyInSeconds = (int)SurveyInSeconds,
        SurveyInAccuracyM = (double)SurveyInAccuracy,
        UseFixedPosition = UseFixedPosition,
        FixedLat = (double)FixedLat,
        FixedLng = (double)FixedLng,
        FixedAlt = (double)FixedAlt,
        TcpHost = TcpHost.Trim(),
        TcpPort = (int)TcpPort,
        UdpPort = (int)UdpPort,
        LogRaw = LogRaw,
    };

    private void SaveSettings()
    {
        if (!_loading)
            CurrentSettings().Save(Settings.Instance);
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        switch (e.PropertyName)
        {
            case nameof(SelectedSource):
            case nameof(NtripPort):
            case nameof(NtripMountpoint):
            case nameof(NtripUsername):
            case nameof(NtripPassword):
            case nameof(NtripTls):
            case nameof(NtripV1):
            case nameof(SendGga):
            case nameof(SerialPort):
            case nameof(SerialBaud):
            case nameof(UbloxConfigure):
            case nameof(UbloxMsm4):
            case nameof(SurveyInSeconds):
            case nameof(SurveyInAccuracy):
            case nameof(UseFixedPosition):
            case nameof(FixedLat):
            case nameof(FixedLng):
            case nameof(FixedAlt):
            case nameof(TcpHost):
            case nameof(TcpPort):
            case nameof(UdpPort):
            case nameof(LogRaw):
                SaveSettings();
                break;
        }
    }

    /// <summary>A pasted Mission Planner style URL (http://user:pass@host:port/mount) fills every field.</summary>
    partial void OnNtripHostChanged(string value)
    {
        if (!_loading && (value.Contains("://", StringComparison.Ordinal) || value.Contains('@') || value.Contains('/')) &&
            NtripEndpoint.TryParseUrl(value) is { } ep)
        {
            _loading = true;
            NtripHost = ep.Host;
            NtripPort = ep.Port;
            if (ep.Mountpoint.Length > 0)
                NtripMountpoint = ep.Mountpoint;
            if (ep.Username.Length > 0)
                NtripUsername = ep.Username;
            if (ep.Password.Length > 0)
                NtripPassword = ep.Password;
            NtripTls = ep.UseTls;
            _loading = false;
        }

        SaveSettings();
    }

    partial void OnSelectedMountpointChanged(MountpointItem? value)
    {
        if (value != null)
            NtripMountpoint = value.Name;
    }

    // ---- commands

    [RelayCommand]
    private void RefreshPorts()
    {
        var keep = SerialPort;
        Ports.Clear();
        foreach (var p in LinkFactory.GetSerialPortNames())
            Ports.Add(p);
        if (keep != null && !Ports.Contains(keep))
            Ports.Add(keep);
        SerialPort = keep ?? Ports.FirstOrDefault();
    }

    private NtripEndpoint Endpoint() =>
        new(NtripHost.Trim(), (int)NtripPort, NtripMountpoint.Trim(), NtripUsername, NtripPassword, NtripTls, NtripV1);

    private bool CanLoadMountpoints() => !IsLoadingMountpoints;

    [RelayCommand(CanExecute = nameof(CanLoadMountpoints))]
    private async Task LoadMountpointsAsync()
    {
        if (string.IsNullOrWhiteSpace(NtripHost))
        {
            MountpointStatus = "Enter the caster address first.";
            return;
        }

        IsLoadingMountpoints = true;
        MountpointStatus = "Loading the caster's source table...";
        try
        {
            var list = await NtripClient.GetSourceTableAsync(Endpoint() with { Mountpoint = "" }, CancellationToken.None);
            var here = ReferencePosition();
            var items = list.Select(m =>
            {
                double km = double.NaN;
                if (here != null && m.HasPosition)
                    km = new PointLatLngAlt(m.Lat, m.Lng).GetDistance(here) / 1000.0;
                return new MountpointItem(m, double.IsNaN(km) ? "" : km.ToString("0.0", CultureInfo.InvariantCulture) + " km", km);
            }).OrderBy(i => double.IsNaN(i.DistanceKm) ? double.MaxValue : i.DistanceKm).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase);

            Mountpoints.Clear();
            foreach (var i in items)
                Mountpoints.Add(i);
            MountpointStatus = Mountpoints.Count == 0
                ? "The caster's source table lists no mountpoints."
                : $"{Mountpoints.Count} mountpoints" + (here != null ? ", nearest first." : ".");
        }
        catch (Exception ex)
        {
            MountpointStatus = ex is NtripException ? ex.Message : "Could not load the list: " + ex.Message;
        }
        finally
        {
            IsLoadingMountpoints = false;
            OnPropertyChanged(nameof(HasMountpoints));
        }
    }

    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (IsRunning)
        {
            await _injector.StopAsync();
            IsRunning = false;
            UpdateStatus();
            return;
        }

        IRtcmSource source;
        UbloxBaseSettings? ublox = null;
        switch (SelectedSource.Kind)
        {
            case RtcmSourceKind.Ntrip:
                if (string.IsNullOrWhiteSpace(NtripHost))
                {
                    await Services.Dialogs.ShowMessageAsync(Title, "Enter the NTRIP caster's address.", MessageKind.Warning);
                    return;
                }

                if (string.IsNullOrWhiteSpace(NtripMountpoint))
                {
                    await Services.Dialogs.ShowMessageAsync(Title, "Enter a mountpoint, or load the caster's list and pick one.", MessageKind.Warning);
                    return;
                }

                source = new NtripRtcmSource(Endpoint(), SendGga ? GgaFromVehicle : null);
                break;
            case RtcmSourceKind.Serial:
                if (string.IsNullOrWhiteSpace(SerialPort))
                {
                    await Services.Dialogs.ShowMessageAsync(Title, "Choose the base station's serial port.", MessageKind.Warning);
                    return;
                }

                source = new SerialRtcmSource(SerialPort, SerialBaud);
                if (UbloxConfigure)
                    ublox = UbloxSettings();
                break;
            case RtcmSourceKind.TcpClient:
                source = new TcpRtcmSource(TcpHost.Trim(), (int)TcpPort);
                break;
            default:
                source = new UdpRtcmSource((int)UdpPort);
                break;
        }

        SaveSettings();
        Settings.Instance.Save();
        string? rawLog = null;
        if (LogRaw)
            rawLog = Path.Combine(Settings.Instance.LogDir, DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture) + ".gpsbase");

        ClearDisplay();
        _injector.Start(source, ublox, rawLog);
        IsRunning = true;
        UpdateStatus();
    }

    private UbloxBaseSettings UbloxSettings()
    {
        PointLatLngAlt? fixedPos = UseFixedPosition && (FixedLat != 0 || FixedLng != 0)
            ? new PointLatLngAlt((double)FixedLat, (double)FixedLng, (double)FixedAlt)
            : null;
        return new UbloxBaseSettings(fixedPos, (int)SurveyInSeconds, (double)SurveyInAccuracy, UbloxMsm4);
    }

    private bool CanConfigureBase() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanConfigureBase))]
    private async Task RestartSurveyInAsync()
    {
        if (!await Services.Dialogs.ConfirmAsync(Title,
                "Restart the survey-in? The base receiver is reset and stops sending corrections until the new survey-in completes.",
                "Restart"))
            return;
        UseFixedPosition = false;
        var settings = UbloxSettings();
        if (!_injector.EnqueueSerialAction(port => UbloxBase.RestartSurveyIn(port, settings)))
            await Services.Dialogs.ShowMessageAsync(Title, "Connect to a serial u-blox base station first.", MessageKind.Warning);
    }

    [RelayCommand(CanExecute = nameof(CanConfigureBase))]
    private async Task ApplyBasePositionAsync()
    {
        var settings = UbloxSettings();
        if (UseFixedPosition && settings.FixedPosition == null)
        {
            await Services.Dialogs.ShowMessageAsync(Title, "Enter the base antenna's latitude, longitude and altitude.", MessageKind.Warning);
            return;
        }

        if (!_injector.EnqueueSerialAction(port => UbloxBase.ApplyPosition(port, settings)))
            await Services.Dialogs.ShowMessageAsync(Title, "Connect to a serial u-blox base station first.", MessageKind.Warning);
    }

    [RelayCommand]
    private async Task SaveCurrentBaseAsync()
    {
        var status = _injector.Statistics.Snapshot(DateTime.UtcNow);
        double lat, lng, alt;
        if (status.Base != null)
        {
            (lat, lng, alt) = (status.Base.Lat, status.Base.Lng, status.Base.Alt);
        }
        else if (_injector.Ublox?.SurveyIn is { Valid: true } svin)
        {
            (lat, lng, alt) = (svin.Lat, svin.Lng, svin.Alt);
        }
        else
        {
            await Services.Dialogs.ShowMessageAsync(Title, "No valid base position has been received yet.", MessageKind.Warning);
            return;
        }

        var name = await Services.Dialogs.PromptAsync(Title, "A name for this base position:", "Base " + (SavedBases.Count + 1));
        if (string.IsNullOrWhiteSpace(name))
            return;
        SavedBases.Add(new SavedBasePosition(name.Trim(), lat, lng, alt));
        SavedBasePositions.Save(SavedBases);
    }

    [RelayCommand]
    private void UseSavedBase(SavedBasePosition? item)
    {
        if (item == null)
            return;
        FixedLat = (decimal)item.Lat;
        FixedLng = (decimal)item.Lng;
        FixedAlt = (decimal)item.Alt;
        UseFixedPosition = true;
        if (IsRunning && IsSerial && UbloxConfigure)
        {
            var settings = UbloxSettings();
            _injector.EnqueueSerialAction(port => UbloxBase.ApplyPosition(port, settings));
        }
    }

    [RelayCommand]
    private async Task DeleteSavedBaseAsync(SavedBasePosition? item)
    {
        if (item == null || !await Services.Dialogs.ConfirmAsync(Title, L.Format("Delete the saved base position \"{0}\"?", item.Name), "Delete"))
            return;
        SavedBases.Remove(item);
        SavedBasePositions.Save(SavedBases);
    }

    [RelayCommand]
    private void ClearStatistics()
    {
        _injector.Statistics.Clear();
        ClearDisplay();
    }

    // ---- positions

    /// <summary>
    /// GGA position for the caster: the vehicle's own fix when it has 3D, else its home, else the
    /// planned home (Mission Planner sends the planned home; the live fix suits VRS better).
    /// </summary>
    internal GgaPosition? GgaFromVehicle()
    {
        var cs = Services.Session.Cs;
        if (cs.gpsstatus >= 3 && (cs.lat != 0 || cs.lng != 0))
            return new GgaPosition(cs.lat, cs.lng, cs.altasl / Math.Max(CurrentState.multiplieralt, 1e-6f),
                NmeaGga.QualityFromFixType((int)cs.gpsstatus), (int)cs.satcount, cs.gpshdop > 0 ? cs.gpshdop : 1);
        var home = cs.HomeLocation;
        if (home != null && (home.Lat != 0 || home.Lng != 0))
            return new GgaPosition(home.Lat, home.Lng, home.Alt);
        var planned = cs.PlannedHomeLocation;
        if (planned != null && (planned.Lat != 0 || planned.Lng != 0))
            return new GgaPosition(planned.Lat, planned.Lng, planned.Alt);
        return null;
    }

    private PointLatLngAlt? ReferencePosition() => GgaFromVehicle() is { } p ? new PointLatLngAlt(p.Lat, p.Lng, p.AltMsl) : null;

    private void OnBasePosition(object? sender, RtcmBasePosition pos)
    {
        // Mission Planner shows the RTK base on the map and measures from it
        try
        {
            Services.Session.Cs.Base = new PointLatLngAlt(pos.Lat, pos.Lng, pos.Alt);
        }
        catch (Exception)
        {
        }
    }

    // ---- page

    public override void OnShown()
    {
        UpdateStatus();
        UpdateVehicle();
    }

    public override void Tick()
    {
        _tick++;
        if (_tick % 5 == 0)
            UpdateStatus();
        if (_tick % 10 == 0)
            UpdateVehicle();
    }

    private void ClearDisplay()
    {
        _rows.Clear();
        Messages.Clear();
        _bars.Clear();
        Satellites.Clear();
        _inRate.Reset();
        _outRate.Reset();
        foreach (var ind in Indicators)
        {
            ind.IsActive = false;
            ind.Detail = "not seen";
        }

        HasMessages = false;
        HasSatellites = false;
        HasBasePosition = false;
        BasePositionText = "No base position yet (RTCM 1005/1006).";
        BaseDistanceText = "";
        SurveyInText = "";
        HasSurveyIn = false;
    }

    /// <summary>Copies the worker's counters and the RTCM statistics into the page (4 Hz).</summary>
    internal void UpdateStatus()
    {
        var now = DateTime.UtcNow;
        var st = _injector.GetStatus();
        if (IsRunning && !_injector.IsRunning)
            IsRunning = false;

        StateText = st.State switch
        {
            RtkState.Stopped => IsRunning ? st.Message : "Not connected",
            _ => st.Message,
        };
        StateOk = st.State == RtkState.Running && st.LastDataUtc != null && now - st.LastDataUtc.Value < TimeSpan.FromSeconds(5);
        StateWarn = st.State is RtkState.Connecting or RtkState.Reconnecting || (st.State == RtkState.Running && !StateOk);
        StateError = st.State == RtkState.Failed;

        BytesInText = FormatBytes(st.BytesIn);
        BytesOutText = FormatBytes(st.BytesForwarded);
        InRateText = FormatBytes((long)_inRate.Update(st.BytesIn, now)) + "/s";
        OutRateText = FormatBytes((long)_outRate.Update(st.BytesForwarded, now)) + "/s";
        PacketsText = st.PacketsSent.ToString("N0", CultureInfo.InvariantCulture);
        var errors = new List<string>();
        if (st.FramesNotSent > 0)
            errors.Add($"{st.FramesNotSent} frames not sent (no vehicle link)");
        if (st.CrcErrors > 0)
            errors.Add($"{st.CrcErrors} CRC errors");
        if (st.Reconnects > 0)
            errors.Add($"{st.Reconnects} reconnects");
        ErrorsText = string.Join(", ", errors);

        if (_injector.Source is NtripRtcmSource ntrip)
            GgaText = ntrip.LastGga != null ? $"GGA sent ×{ntrip.GgaSent}: {ntrip.LastGga}" : SendGga ? "GGA: waiting for a vehicle or home position" : "";

        var rtcm = _injector.Statistics.Snapshot(now);
        LastMessageText = rtcm.LastMessageAge is { } age ? FormatAge(age) + " ago" : "—";
        UpdateMessages(rtcm);
        UpdateIndicators(rtcm);
        UpdateBars(rtcm);
        UpdateBase(rtcm);
        UpdateSurveyIn();
    }

    private void UpdateMessages(RtcmStatus rtcm)
    {
        foreach (var m in rtcm.Messages)
        {
            if (!_rows.TryGetValue(m.Type, out var row))
            {
                row = new RtcmMessageRow(m.Type, m.Description);
                _rows[m.Type] = row;
                var index = 0;
                while (index < Messages.Count && Messages[index].Type < m.Type)
                    index++;
                Messages.Insert(index, row);
            }

            row.Count = m.Count;
            row.Age = FormatAge(m.Age);
            row.IsStale = m.Age > (RtcmMessages.IsBasePosition(m.Type) ? RtcmStatistics.BaseTimeout : RtcmStatistics.ObservationTimeout);
        }

        HasMessages = Messages.Count > 0;
    }

    private void UpdateIndicators(RtcmStatus rtcm)
    {
        var baseInd = _indicators["Base"];
        baseInd.IsActive = rtcm.BaseActive;
        baseInd.Detail = rtcm.BaseAge is { } ba ? FormatAge(ba) + " ago" : "not seen";

        foreach (var c in rtcm.Constellations)
        {
            if (!_indicators.TryGetValue(RtcmMessages.Name(c.System), out var ind))
                continue;
            ind.IsActive = c.Active;
            ind.Detail = c.Active ? $"{c.Satellites} sats" : FormatAge(c.Age) + " ago";
        }
    }

    private void UpdateBars(RtcmStatus rtcm)
    {
        var keys = new HashSet<string>();
        var index = 0;
        foreach (var s in rtcm.Satellites)
        {
            var label = RtcmMessages.Letter(s.System) + s.Prn.ToString(CultureInfo.InvariantCulture);
            keys.Add(label);
            if (!_bars.TryGetValue(label, out var bar))
            {
                bar = new SatelliteBar(label);
                _bars[label] = bar;
                Satellites.Insert(Math.Min(index, Satellites.Count), bar);
            }
            else if (Satellites.IndexOf(bar) != index)
            {
                Satellites.Move(Satellites.IndexOf(bar), Math.Min(index, Satellites.Count - 1));
            }

            bar.Snr = s.Snr;
            bar.BarHeight = Math.Clamp((s.Snr - 20) / (55 - 20), 0.04, 1) * 110;
            bar.IsGood = s.Snr >= 40;
            bar.IsWeak = s.Snr < 30;
            index++;
        }

        foreach (var gone in _bars.Keys.Where(k => !keys.Contains(k)).ToList())
        {
            Satellites.Remove(_bars[gone]);
            _bars.Remove(gone);
        }

        HasSatellites = Satellites.Count > 0;
    }

    private void UpdateBase(RtcmStatus rtcm)
    {
        if (rtcm.Base is not { } b)
            return;
        HasBasePosition = true;
        var inv = CultureInfo.InvariantCulture;
        BasePositionText = string.Format(inv, "{0:0.0000000}, {1:0.0000000}, {2:0.00} m (station {3}, RTCM {4})",
            b.Lat, b.Lng, b.Alt, b.StationId, b.MessageType);
        var cs = Services.Session.Cs;
        if (Services.Session.IsConnected && cs.gpsstatus >= 3 && (cs.lat != 0 || cs.lng != 0))
        {
            var d = new PointLatLngAlt(b.Lat, b.Lng).GetDistance(new PointLatLngAlt(cs.lat, cs.lng));
            BaseDistanceText = d < 10000 ? $"Vehicle is {d.ToString("0", inv)} m from the base" : $"Vehicle is {(d / 1000).ToString("0.0", inv)} km from the base";
        }
        else
        {
            BaseDistanceText = "";
        }
    }

    private void UpdateSurveyIn()
    {
        var mon = _injector.Ublox;
        if (mon == null || (mon.SurveyIn == null && mon.Mode == null))
        {
            HasSurveyIn = false;
            return;
        }

        HasSurveyIn = true;
        var inv = CultureInfo.InvariantCulture;
        var parts = new List<string>();
        if (mon.Mode != null)
            parts.Add("Mode: " + mon.Mode);
        if (mon.SurveyIn is { } s)
        {
            SurveyInValid = s.Valid;
            parts.Add(s.Valid ? "Position valid" : s.Active ? "Survey-in in progress" : "Survey-in not running");
            parts.Add($"{s.DurationS} s, {s.Observations} observations");
            parts.Add("accuracy " + s.MeanAccuracyM.ToString("0.000", inv) + " m");
        }

        SurveyInText = string.Join("  ·  ", parts);
    }

    internal void UpdateVehicle()
    {
        var session = Services.Session;
        if (!session.IsConnected)
        {
            VehicleGpsText = "No vehicle connected";
            VehicleRtk = false;
            return;
        }

        var cs = session.Cs;
        var text = $"GPS: {FixName((int)cs.gpsstatus)}, {cs.satcount:0} sats, HDOP {cs.gpshdop:0.0}";
        if (cs.gpsstatus2 > 0)
            text += $"   GPS2: {FixName((int)cs.gpsstatus2)}, {cs.satcount2:0} sats";
        VehicleGpsText = text;
        VehicleRtk = cs.gpsstatus >= 5 || cs.gpsstatus2 >= 5;
    }

    public static string FixName(int fix) => fix switch
    {
        <= 1 => "No fix",
        2 => "2D fix",
        3 => "3D fix",
        4 => "DGPS",
        5 => "RTK Float",
        6 => "RTK Fixed",
        7 => "Static",
        8 => "PPP",
        _ => "Fix " + fix,
    };

    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => bytes.ToString(CultureInfo.InvariantCulture) + " B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB",
        _ => (bytes / 1024.0 / 1024.0).ToString("0.00", CultureInfo.InvariantCulture) + " MB",
    };

    public static string FormatAge(TimeSpan age) => age.TotalSeconds switch
    {
        < 10 => age.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s",
        < 120 => age.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " s",
        < 7200 => age.TotalMinutes.ToString("0", CultureInfo.InvariantCulture) + " min",
        _ => age.TotalHours.ToString("0", CultureInfo.InvariantCulture) + " h",
    };
}
