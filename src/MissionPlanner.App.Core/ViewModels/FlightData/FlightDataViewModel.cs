using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.Maps;
using MissionPlanner.App.Core.Services;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.FlightData;

public sealed record MapProviderOption(string Name, GMap.NET.MapProviders.GMapProvider Provider)
{
    public override string ToString() => Name;
}

/// <summary>Tabs under the HUD, in display order (Mission Planner's order).</summary>
public enum FlightDataTab
{
    Quick,
    Actions,
    Messages,
    PreFlight,
    Gauges,
    Transponder,
    Status,
    Servo,
    AuxFunction,
    Payload,
    TelemetryLogs,
    DataFlashLogs,
}

/// <summary>
/// DATA screen, after Mission Planner's Flight Data: HUD, telemetry tabs (Quick, Actions,
/// Messages, PreFlight, Gauges, Transponder, Status, Servo/Relay, Aux Function, Payload,
/// Telemetry Logs, DataFlash Logs) and the map. Everything is copied from CurrentState in
/// <see cref="Tick"/>; values that did not change raise no notifications, and nothing reads
/// properties by reflection while running.
/// </summary>
public sealed partial class FlightDataViewModel : PageViewModel
{
    // ArduPilot's default SITL location (CMAC, Canberra)
    private const double DefaultLat = -35.363261;
    private const double DefaultLng = 149.165230;

    private const int QuickEvery = 2; // 10 Hz
    private const int MapEvery = 2; // 10 Hz
    private const int MessagesEvery = 4; // 5 Hz
    private const int StatusEvery = 10; // 2 Hz
    private const int SlowEvery = 5; // 4 Hz: link state, mode list, buttons

    private long _tick;
    private CurrentState? _cs;
    private bool _shown;
    private float _lastGpsStatus = -1;
    private float _lastSatCount = -1;
    private long _toastUntil;

    public FlightDataViewModel(AppServices services)
        : base("Flight Data")
    {
        Services = services;
        MapProviders = MapTiles.CommonProviders.Select(p => new MapProviderOption(p.Name, p)).ToArray();
        _selectedMapProvider = MapProviders.FirstOrDefault(p => p.Name == Settings.Instance["MapType"])
                               ?? MapProviders.FirstOrDefault(p => p.Name == Settings.Instance[ConfigScreens.PlannerSettingsViewModel.MapProviderKey])
                               ?? MapProviders[0];
        Tlogs = new TlogPlayerViewModel(services);
        Tuning = new TuningGraphViewModel(services);

        QuickTiles = QuickDefault.All.Select((d, i) => new QuickTile(i, d)).ToArray();
        foreach (var tile in QuickTiles)
        {
            if (Settings.Instance["next_quick" + tile.Index] is { Length: > 0 } name)
                tile.Show(name);
        }

        MaxZoom = SelectedMapProvider.Provider.MaxZoom ?? 20;
        RestoreMapPosition();

        services.Mission.Changed += (_, _) => OnMissionChanged();
        OnMissionChanged();
        // Plane, Copter and Rover mode tables come from the parameter metadata
        services.Params.MetadataChanged += (_, _) => RefreshModes(services.Session.Cs, force: true);

        InitExtras();
        InitHudWindow();
        InitThreeD();
    }

    public AppServices Services { get; }

    public HudModel Hud { get; } = new();

    /// <summary>Raised at the end of every <see cref="Tick"/>, after <see cref="Hud"/> and the map model were refreshed.</summary>
    public event EventHandler? TelemetryUpdated;

    public TlogPlayerViewModel Tlogs { get; }

    public TuningGraphViewModel Tuning { get; }

    public IReadOnlyList<MapProviderOption> MapProviders { get; }

    [ObservableProperty]
    private MapProviderOption _selectedMapProvider;

    [ObservableProperty]
    private double _centerLat = DefaultLat;

    [ObservableProperty]
    private double _centerLng = DefaultLng;

    [ObservableProperty]
    private int _zoom = 17;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusTab))]
    private int _selectedTabIndex;

    public bool IsStatusTab => SelectedTabIndex == (int)FlightDataTab.Status;

    [ObservableProperty]
    private bool _isConnected;

    // ---- Quick tab ---------------------------------------------------------------------------

    /// <summary>The six readouts, two columns by three rows.</summary>
    public IReadOnlyList<QuickTile> QuickTiles { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQuickPickerOpen))]
    private QuickTile? _editingTile;

    public bool IsQuickPickerOpen => EditingTile != null;

    [ObservableProperty]
    private string _quickFilter = "";

    [ObservableProperty]
    private IReadOnlyList<QuickField> _quickFieldOptions = [];

    private IReadOnlyList<QuickField> _allQuickFields = [];

    [RelayCommand]
    private void EditQuickTile(QuickTile? tile)
    {
        if (tile == null)
            return;
        _allQuickFields = QuickValueCatalog.ListFields();
        EditingTile = tile;
        if (QuickFilter.Length == 0)
            ApplyQuickFilter();
        else
            QuickFilter = "";
    }

    [RelayCommand]
    private void PickQuickField(QuickField? field)
    {
        var tile = EditingTile;
        EditingTile = null;
        if (tile == null || field == null)
            return;
        if (!tile.Show(field.Name))
            return;
        if (tile.IsDefault)
            Settings.Instance.Remove("next_quick" + tile.Index);
        else
            Settings.Instance["next_quick" + tile.Index] = field.Name;
        tile.Update(Services.Session.Cs);
    }

    [RelayCommand]
    private void ResetQuickTile()
    {
        var tile = EditingTile;
        EditingTile = null;
        if (tile == null)
            return;
        tile.Show(tile.Default.Name);
        Settings.Instance.Remove("next_quick" + tile.Index);
        tile.Update(Services.Session.Cs);
    }

    [RelayCommand]
    private void CloseQuickPicker() => EditingTile = null;

    partial void OnQuickFilterChanged(string value) => ApplyQuickFilter();

    private void ApplyQuickFilter() => QuickFieldOptions = QuickFilterMatch(_allQuickFields, QuickFilter);

    /// <summary>Case-insensitive match on the label, unit or property name.</summary>
    public static IReadOnlyList<QuickField> QuickFilterMatch(IReadOnlyList<QuickField> fields, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return fields;
        var f = filter.Trim();
        return fields.Where(x => x.Display.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                                 x.Name.Contains(f, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    // ---- Messages tab ------------------------------------------------------------------------

    private readonly MessageLog _messages = new();

    /// <summary>Status texts from the vehicle, newest first.</summary>
    public ObservableCollection<MessageLine> Messages => _messages.Lines;

    // ---- Status tab --------------------------------------------------------------------------

    private List<StatusEntry>? _statusAll;

    [ObservableProperty]
    private IReadOnlyList<StatusEntry> _statusEntries = [];

    [ObservableProperty]
    private string _statusFilter = "";

    partial void OnStatusFilterChanged(string value)
    {
        if (_statusAll == null)
            return;
        StatusEntries = _statusAll.Where(e => StatusCatalog.Matches(e, value)).ToArray();
        RefreshStatus();
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        SwitchTab(value);
        if (value == (int)FlightDataTab.Status)
        {
            if (_statusAll == null)
            {
                _statusAll = StatusCatalog.CreateEntries();
                StatusEntries = _statusAll.Where(e => StatusCatalog.Matches(e, StatusFilter)).ToArray();
            }

            RefreshStatus();
        }
    }

    private void RefreshStatus()
    {
        var cs = Services.Session.Cs;
        foreach (var e in StatusEntries)
            e.Update(cs);
    }

    // ---- DataFlash Logs tab ------------------------------------------------------------------

    [RelayCommand]
    private void OpenLogDownload() => Services.Windows.Show(new Logs.LogDownloadViewModel(Services));

    [RelayCommand]
    private void OpenLogBrowser() => Services.Windows.Show(new Logs.LogBrowserViewModel(Services));

    // ---- toast -------------------------------------------------------------------------------

    [ObservableProperty]
    private string _toastText = "";

    [ObservableProperty]
    private bool _toastIsError;

    [ObservableProperty]
    private bool _isToastVisible;

    /// <summary>Shows a short note over the map; also used for command outcomes.</summary>
    public void ShowToast(string text, bool isError = false, int milliseconds = 4000)
    {
        ToastText = text;
        ToastIsError = isError;
        IsToastVisible = true;
        _toastUntil = Environment.TickCount64 + milliseconds;
    }

    [RelayCommand]
    private void DismissToast() => IsToastVisible = false;

    // ---- page lifecycle ----------------------------------------------------------------------

    public override void OnShown()
    {
        _shown = true;
        Tlogs.OnShown();
        if (ShowTuning)
            Tuning.OnShown();
        ShowExtras(true);
    }

    public override void OnHidden()
    {
        _shown = false;
        Tlogs.OnHidden();
        if (ShowTuning)
            Tuning.OnHidden();
        ShowExtras(false);
        SaveMapPosition();
    }

    public override void Tick()
    {
        var s = Services.Session;
        var cs = s.Cs;
        if (!ReferenceEquals(cs, _cs))
            OnVehicleChanged(cs);

        var tick = _tick++;

        UpdateHud(s, cs);
        FeedScene(s, cs);

        if (tick % QuickEvery == 0)
        {
            foreach (var tile in QuickTiles)
                tile.Update(cs);
        }

        if (tick % MapEvery == 1)
            UpdateMap(s.Vehicle, cs);

        if (tick % MessagesEvery == 0)
        {
            _messages.SeverityOf ??= s.Severities.Find;
            _messages.Update(cs.messages);
        }

        if (tick % StatusEvery == 0 && IsStatusTab)
            RefreshStatus();

        if (tick % SlowEvery == 0)
            UpdateSlow(s, cs);

        if (IsToastVisible && Environment.TickCount64 > _toastUntil && !IsCommandRunning)
            IsToastVisible = false;

        Tlogs.Tick();
        if (ShowTuning)
            Tuning.Tick();
        TickExtras(tick);

        TelemetryUpdated?.Invoke(this, EventArgs.Empty);
    }

    private void OnVehicleChanged(CurrentState cs)
    {
        _cs = cs;
        MapModel.Track.Clear();
        _lastGpsStatus = -1;
        RefreshModes(cs);
    }

    /// <summary>HUD values; the version moves only if something visible changed, so the HUD redraws only then.</summary>
    private void UpdateHud(Session.VehicleSession s, CurrentState cs)
    {
        var h = Hud;
        var c = false;
        c |= Set(ref h.Connected, s.IsConnected || s.Mav.logreadmode || s.Mav.logplaybackfile != null); // a paused log still shows its data
        var linkLost = s.IsReconnecting || s.IsLinkStale;
        c |= Set(ref h.LinkLost, linkLost);
        c |= Set(ref h.LinkLostText, !linkLost ? ""
            : $"{(s.IsReconnecting ? "RECONNECTING" : "NO DATA")} · {(int)s.SinceLastPacket.TotalSeconds} s");
        c |= Set(ref h.Roll, cs.roll);
        c |= Set(ref h.Pitch, cs.pitch);
        c |= Set(ref h.Heading, cs.yaw);
        c |= Set(ref h.TargetHeading, cs.target_bearing);
        c |= Set(ref h.NavBearing, cs.nav_bearing);
        c |= Set(ref h.Airspeed, cs.airspeed);
        c |= Set(ref h.Groundspeed, cs.groundspeed);
        c |= Set(ref h.Altitude, cs.alt);
        c |= Set(ref h.VerticalSpeed, cs.verticalspeed);
        c |= Set(ref h.Armed, cs.armed);
        c |= Set(ref h.Mode, cs.mode ?? "");
        c |= Set(ref h.ReadyToArm, cs.prearmstatus);
        c |= Set(ref h.BatteryVoltage, cs.battery_voltage);
        c |= Set(ref h.BatteryCurrent, cs.current);
        c |= Set(ref h.BatteryRemaining, cs.battery_remaining);

        var gps = cs.gpsstatus;
        var sats = cs.satcount;
        if (gps != _lastGpsStatus || sats != _lastSatCount)
        {
            _lastGpsStatus = gps;
            _lastSatCount = sats;
            h.GpsText = GpsText(gps, sats);
            h.GpsLevel = gps >= 3 ? HudLevel.Ok : gps >= 2 ? HudLevel.Warning : HudLevel.Bad;
            c = true;
        }

        c |= Set(ref h.WpDistance, cs.wp_dist);
        c |= Set(ref h.WpNumber, (int)cs.wpno);
        var tia = TimeSpan.FromSeconds(Math.Floor(cs.timeInAir));
        if (tia != h.TimeInAir)
        {
            h.TimeInAir = tia;
            c = true;
        }

        c |= Set(ref h.LinkQuality, cs.linkqualitygcs);
        var ekf = cs.ekfstatus;
        c |= Set(ref h.EkfLevel, ekf > 0.8 ? HudLevel.Bad : ekf > 0.5 ? HudLevel.Warning : HudLevel.Ok);
        var vibe = Math.Max(cs.vibex, Math.Max(cs.vibey, cs.vibez));
        c |= Set(ref h.VibeLevel, vibe > 60 ? HudLevel.Bad : vibe > 30 ? HudLevel.Warning : HudLevel.Ok);
        c |= Set(ref h.Message, cs.messageHigh ?? "");
        c |= Set(ref h.SpeedUnit, Units.Speed);
        c |= Set(ref h.AltUnit, Units.Altitude);

        if (c)
            h.Version++;
    }

    public static string GpsText(float status, float sats) => status switch
    {
        0 => "No GPS",
        1 => "No Fix",
        2 => $"2D Fix ({sats:0})",
        3 => $"3D Fix ({sats:0})",
        4 => $"3D DGPS ({sats:0})",
        5 => $"RTK Float ({sats:0})",
        6 => $"RTK Fixed ({sats:0})",
        _ => status.ToString("0", CultureInfo.InvariantCulture),
    };

    /// <summary>Link state, buttons and lists that do not need 20 Hz.</summary>
    private void UpdateSlow(Session.VehicleSession s, CurrentState cs)
    {
        IsConnected = s.IsConnected;
        UpdateActionState(cs);
        if ((_tick / SlowEvery) % 40 == 0)
            SaveMapPosition();
    }

    private static bool Set(ref double field, double value)
    {
        if (field.Equals(value))
            return false;
        field = value;
        return true;
    }

    private static bool Set(ref bool field, bool value)
    {
        if (field == value)
            return false;
        field = value;
        return true;
    }

    private static bool Set(ref int field, int value)
    {
        if (field == value)
            return false;
        field = value;
        return true;
    }

    private static bool Set(ref HudLevel field, HudLevel value)
    {
        if (field == value)
            return false;
        field = value;
        return true;
    }

    private static bool Set(ref string field, string value)
    {
        if (string.Equals(field, value, StringComparison.Ordinal))
            return false;
        field = value;
        return true;
    }
}
