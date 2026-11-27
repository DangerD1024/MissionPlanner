using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using MissionPlanner.App.Core.Localization;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.ViewModels.FlightData;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>A choice stored under <see cref="Key"/> and shown as <see cref="Label"/>.</summary>
public sealed record UnitChoice(string Key, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A telemetry stream rate; -1 leaves the stream to the vehicle.</summary>
public sealed record RateChoice(int Hz)
{
    public string Label => Hz switch
    {
        -1 => L.N("Don't request"),
        0 => L.N("0 (off)"),
        _ => $"{Hz} Hz",
    };

    public override string ToString() => Label;
}

/// <summary>One telemetry stream group (Mission Planner's CMB_rate* boxes).</summary>
public sealed partial class StreamRateSetting : ObservableObject
{
    private readonly PlannerSettingsViewModel _owner;

    internal StreamRateSetting(PlannerSettingsViewModel owner, PlannerSettings.StreamGroup group, string label, string detail, int hz)
    {
        _owner = owner;
        Group = group;
        Label = label;
        Detail = detail;
        _selected = PlannerSettings.Choice(hz);
    }

    public PlannerSettings.StreamGroup Group { get; }

    public string Label { get; }

    public string Detail { get; }

    public IReadOnlyList<RateChoice> Choices => PlannerSettingsViewModel.RateChoices;

    [ObservableProperty]
    private RateChoice? _selected;

    partial void OnSelectedChanged(RateChoice? value)
    {
        if (value != null)
            _owner.OnRateChanged(this, value.Hz);
    }
}

/// <summary>Application settings that other screens read (rates, units), applied to the shared libraries.</summary>
public static class PlannerSettings
{
    public enum StreamGroup
    {
        Attitude,
        Position,
        Status,
        Rc,
        Sensors,
    }

    /// <summary>The settings key of each group, shared with the WinForms app's names.</summary>
    public static string Key(StreamGroup g) => g switch
    {
        StreamGroup.Attitude => "CMB_rateattitude",
        StreamGroup.Position => "CMB_rateposition",
        StreamGroup.Status => "CMB_ratestatus",
        StreamGroup.Rc => "CMB_raterc",
        _ => "CMB_ratesensors",
    };

    public static int StoredRate(StreamGroup g) => g switch
    {
        StreamGroup.Attitude => CurrentState.rateattitudebackup,
        StreamGroup.Position => CurrentState.ratepositionbackup,
        StreamGroup.Status => CurrentState.ratestatusbackup,
        StreamGroup.Rc => CurrentState.ratercbackup,
        _ => CurrentState.ratesensorsbackup,
    };

    /// <summary>Sets the rate new connections start with (CurrentState copies these on reset).</summary>
    public static void SetStoredRate(StreamGroup g, int hz)
    {
        switch (g)
        {
            case StreamGroup.Attitude: CurrentState.rateattitudebackup = hz; break;
            case StreamGroup.Position: CurrentState.ratepositionbackup = hz; break;
            case StreamGroup.Status: CurrentState.ratestatusbackup = hz; break;
            case StreamGroup.Rc: CurrentState.ratercbackup = hz; break;
            default: CurrentState.ratesensorsbackup = hz; break;
        }
    }

    /// <summary>A connected vehicle's rate for the group (what its streams are kept at).</summary>
    public static int Rate(CurrentState cs, StreamGroup g) => g switch
    {
        StreamGroup.Attitude => cs.rateattitude,
        StreamGroup.Position => cs.rateposition,
        StreamGroup.Status => cs.ratestatus,
        StreamGroup.Rc => cs.raterc,
        _ => cs.ratesensors,
    };

    /// <summary>The rate shown for the group: the connected vehicle's, else the one new connections start with.</summary>
    public static int CurrentRate(Session.VehicleSession session, StreamGroup g) =>
        session.IsConnected ? Rate(session.Cs, g) : StoredRate(g);

    /// <summary>The offered choice for a rate (one of the list when it is there, so a ComboBox selects it).</summary>
    public static RateChoice Choice(int hz) =>
        PlannerSettingsViewModel.RateChoices.FirstOrDefault(c => c.Hz == hz) ?? new RateChoice(hz);

    /// <summary>
    /// Sets a telemetry rate the way SETTINGS › App Settings › Telemetry rates does: saved (with the
    /// WinForms app's key) as the rate new connections start with, applied to the connected vehicle and
    /// its streams requested at it, then <see cref="AppServices.StreamRateChanged"/> so every place that
    /// shows the rate follows. The one path for changing a rate, wherever the control is.
    /// </summary>
    public static void SetRate(AppServices services, StreamGroup g, int hz)
    {
        Settings.Instance[Key(g)] = hz.ToString(System.Globalization.CultureInfo.InvariantCulture);
        SetStoredRate(g, hz);
        try
        {
            Settings.Instance.Save();
        }
        catch (Exception ex)
        {
            Log.Warn("settings save", ex);
        }

        var session = services.Session;
        if (session.IsConnected)
        {
            var streams = Apply(session.Cs, g, hz);
            var v = session.Vehicle;
            _ = Task.Run(() =>
            {
                try
                {
                    foreach (var s in streams)
                        session.Mav.requestDatastream(s, hz, v.sysid, v.compid);
                }
                catch (Exception ex)
                {
                    Log.Warn("request stream", ex);
                }
            });
        }

        services.OnStreamRateChanged(g);
    }

    private static readonly ILog Log = LogManager.GetLogger(typeof(PlannerSettings));

    /// <summary>Applies the rate to a connected vehicle's state and returns the streams to request.</summary>
    public static MAVLink.MAV_DATA_STREAM[] Apply(CurrentState cs, StreamGroup g, int hz)
    {
        switch (g)
        {
            case StreamGroup.Attitude:
                cs.rateattitude = hz;
                return [MAVLink.MAV_DATA_STREAM.EXTRA1, MAVLink.MAV_DATA_STREAM.EXTRA2];
            case StreamGroup.Position:
                cs.rateposition = hz;
                return [MAVLink.MAV_DATA_STREAM.POSITION];
            case StreamGroup.Status:
                cs.ratestatus = hz;
                return [MAVLink.MAV_DATA_STREAM.EXTENDED_STATUS];
            case StreamGroup.Rc:
                cs.raterc = hz;
                return [MAVLink.MAV_DATA_STREAM.RC_CHANNELS];
            default:
                cs.ratesensors = hz;
                return [MAVLink.MAV_DATA_STREAM.EXTRA3, MAVLink.MAV_DATA_STREAM.RAW_SENSORS];
        }
    }

    public const string DistanceKey = "distunits";
    public const string AltitudeKey = "altunits";
    public const string SpeedKey = "speedunits";

    /// <summary>Display multiplier and unit text for a distance or altitude choice (an enum name).</summary>
    public static (float Multiplier, string Unit) Distance(string? key) =>
        Enum.TryParse<distances>(key, out var d) && d == distances.Feet ? (3.2808399f, "ft") : (1f, "m");

    public static (float Multiplier, string Unit) Speed(string? key) =>
        Enum.TryParse<speeds>(key, out var s)
            ? s switch
            {
                speeds.fps => (3.2808399f, "fps"),
                speeds.kph => (3.6f, "kph"),
                speeds.mph => (2.23693629f, "mph"),
                speeds.knots => (1.94384449f, "kts"),
                _ => (1f, "m/s"),
            }
            : (1f, "m/s");

    /// <summary>Mission Planner's ChangeUnits: sets CurrentState's display multipliers from the settings.</summary>
    public static void ApplyUnits()
    {
        (CurrentState.multiplierdist, CurrentState.DistanceUnit) = Distance(Settings.Instance[DistanceKey]);
        (CurrentState.multiplieralt, CurrentState.AltUnit) = Distance(Settings.Instance[AltitudeKey]);
        (CurrentState.multiplierspeed, CurrentState.SpeedUnit) = Speed(Settings.Instance[SpeedKey]);
    }

    /// <summary>
    /// Applies the stored rates, units and GCS ID to the libraries, as Mission Planner does at start-up.
    /// Safe to call more than once.
    /// </summary>
    public static void ApplyStored()
    {
        foreach (var g in Enum.GetValues<StreamGroup>())
        {
            if (Settings.Instance[Key(g)] != null)
                SetStoredRate(g, Settings.Instance.GetInt32(Key(g), StoredRate(g)));
        }

        ApplyUnits();
        ConnectOptions.ApplyGcsId();
    }
}

/// <summary>
/// Planner: application settings (Mission Planner's ConfigPlanner): language, theme, speech, map,
/// connection options, log folder, parameter documentation, telemetry rates and display units.
/// Changes apply and save immediately, under Mission Planner's settings keys.
/// </summary>
public sealed partial class PlannerSettingsViewModel : PageViewModel
{
    /// <summary>Version, data folders and the Linux menu entry, shown at the bottom (there is no HELP screen).</summary>

    private static readonly ILog Log = LogManager.GetLogger(typeof(PlannerSettingsViewModel));

    /// <summary>Name of the map provider the map screens start with.</summary>
    public const string MapProviderKey = "next_map_provider";

    public static IReadOnlyList<RateChoice> RateChoices { get; } =
        new[] { -1, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 50, 100 }.Select(h => new RateChoice(h)).ToArray();

    public static IReadOnlyList<UnitChoice> DistanceChoices { get; } =
    [
        new(nameof(distances.Meters), L.N("Meters (m)")),
        new(nameof(distances.Feet), L.N("Feet (ft)")),
    ];

    public static IReadOnlyList<UnitChoice> SpeedChoices { get; } =
    [
        new(nameof(speeds.meters_per_second), L.N("Meters per second (m/s)")),
        new(nameof(speeds.kph), L.N("Kilometers per hour (kph)")),
        new(nameof(speeds.mph), L.N("Miles per hour (mph)")),
        new(nameof(speeds.knots), L.N("Knots (kts)")),
        new(nameof(speeds.fps), L.N("Feet per second (fps)")),
    ];

    private bool _loading = true;

    // ---- language -------------------------------------------------------------------------------

    /// <summary>English and the languages Mission Planner has translations for, named in their own language.</summary>
    public IReadOnlyList<Localization.LanguageInfo> Languages { get; }

    [ObservableProperty]
    private Localization.LanguageInfo _selectedLanguage;

    partial void OnSelectedLanguageChanged(Localization.LanguageInfo value)
    {
        if (!_loading && value != null && value.Code != Services.Localizer.Culture)
            Services.Localizer.SetLanguage(value.Code, save: true);
    }

    // ---- appearance -----------------------------------------------------------------------------

    public IReadOnlyList<ThemeInfo> Themes { get; }

    [ObservableProperty]
    private ThemeInfo _selectedTheme;

    partial void OnSelectedThemeChanged(ThemeInfo value)
    {
        if (value != null && value.Name != Services.Themes.Current)
            Services.Themes.Apply(value.Name);
    }

    public PlannerSettingsViewModel(AppServices services)
        : base("App Settings")
    {
        Services = services;
        Languages = services.Localizer.Languages;
        _selectedLanguage = Languages.FirstOrDefault(l => l.Code == services.Localizer.Culture) ?? Languages[0];
        Themes = services.Themes.Themes;
        _selectedTheme = Themes.FirstOrDefault(t => t.Name == services.Themes.Current) ?? Themes[0];
        PlannerSettings.ApplyStored();

        // speech: the same switch as DATA > Actions and the Speech dialog
        services.SpeechSettingsChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(SpeechEnabled));
            OnPropertyChanged(nameof(SpeechSeverity));
        };

        _gcsId = ConnectOptions.GcsId;
        _resetOnConnect = ConnectOptions.ResetOnConnect;
        _esp32RtsFix = ConnectOptions.Esp32RtsFix;
        _loadWaypointsOnConnect = ConnectOptions.LoadWaypoints;
        _noRcReceiver = ConnectOptions.NoRcReceiver;

        _logDirectory = Settings.Instance.LogDir;

        int Rate(PlannerSettings.StreamGroup g) => PlannerSettings.CurrentRate(services.Session, g);
        Rates =
        [
            new(this, PlannerSettings.StreamGroup.Attitude, L.N("Attitude"), "ATTITUDE, VFR_HUD (EXTRA1, EXTRA2)", Rate(PlannerSettings.StreamGroup.Attitude)),
            new(this, PlannerSettings.StreamGroup.Position, L.N("Position"), "GLOBAL_POSITION_INT, GPS (POSITION)", Rate(PlannerSettings.StreamGroup.Position)),
            new(this, PlannerSettings.StreamGroup.Status, L.N("Mode / Status"), "SYS_STATUS, mission (EXTENDED_STATUS)", Rate(PlannerSettings.StreamGroup.Status)),
            new(this, PlannerSettings.StreamGroup.Rc, L.N("RC"), "RC_CHANNELS, SERVO_OUTPUT_RAW (RC_CHANNELS)", Rate(PlannerSettings.StreamGroup.Rc)),
            new(this, PlannerSettings.StreamGroup.Sensors, L.N("Sensors"), "raw IMU, extra (EXTRA3, RAW_SENSORS)", Rate(PlannerSettings.StreamGroup.Sensors)),
        ];
        // the same rates are offered elsewhere (the 3D view's servo tilt sets the RC one): follow them
        services.StreamRateChanged += (_, g) => ShowRate(g);

        _distanceUnit = Choose(DistanceChoices, Settings.Instance[PlannerSettings.DistanceKey]);
        _altitudeUnit = Choose(DistanceChoices, Settings.Instance[PlannerSettings.AltitudeKey]);
        _speedUnit = Choose(SpeedChoices, Settings.Instance[PlannerSettings.SpeedKey]);

        services.Params.MetadataChanged += (_, _) => UpdateDocsStatus();
        UpdateDocsStatus();
        _loading = false;
    }

    public AppServices Services { get; }

    /// <summary>The TOOLS screen (Mission Planner's Ctrl+F screen).</summary>
    [RelayCommand]
    private void OpenTools() => Services.ShowScreen(ViewModels.Screen.Tools);

    // ---- speech

    /// <summary>Mission Planner's "Enable Speech" (speechenable): the same switch as DATA > Actions.</summary>
    public bool SpeechEnabled
    {
        get => SpeechOptions.Enabled;
        set
        {
            if (value != SpeechOptions.Enabled)
                AppSpeech.SetEnabled(Services, value);
        }
    }

    public IReadOnlyList<string> SpeechSeverities => SpeechOptions.Severities;

    /// <summary>Vehicle messages at or above this MAV_SEVERITY are spoken (severity), as in the Speech dialog.</summary>
    public int SpeechSeverity
    {
        get => SpeechOptions.Severity;
        set
        {
            // a combo box that loses its selection reports -1
            if (value >= 0 && value != SpeechOptions.Severity)
                AppSpeech.SetSeverity(Services, value);
        }
    }

    /// <summary>Every announcement and its text (the dialog DATA > Actions "Speech..." opens).</summary>
    [RelayCommand]
    private Task OpenSpeechSettingsAsync() => SpeechSettingsViewModel.ShowAsync(Services);

    // ---- map

    /// <summary>The map settings page; the card shows its default map, the dialog the rest.</summary>
    public MapSettingsViewModel Map => Services.MapSettings;

    [RelayCommand]
    private Task OpenMapSettingsAsync() => MapSettingsViewModel.ShowAsync(Services);

    // ---- connection

    private int _gcsId;

    /// <summary>The MAVLink system ID this ground station sends with (gcsid, 1 to 255, Mission Planner's 255 by default).</summary>
    public int GcsId
    {
        get => _gcsId;
        set
        {
            var id = Math.Clamp(value, 1, 255);
            if (id != _gcsId && !_loading)
            {
                _gcsId = ConnectOptions.SetGcsId(id);
                Save();
            }

            // shows the clamped value when the entry was out of range
            OnPropertyChanged();
        }
    }

    /// <summary>Pulse DTR and RTS on a serial port before connecting, so the board restarts (CHK_resetapmonconnect).</summary>
    [ObservableProperty]
    private bool _resetOnConnect;

    /// <summary>Keep RTS from resetting an ESP32 when its USB serial port opens (CHK_rtsresetesp32).</summary>
    [ObservableProperty]
    private bool _esp32RtsFix;

    /// <summary>Read the vehicle's mission into the plan after connecting (loadwpsonconnect).</summary>
    [ObservableProperty]
    private bool _loadWaypointsOnConnect;

    /// <summary>The vehicle flies without an RC receiver: no "No RC Receiver" warning (norcreceiver).</summary>
    [ObservableProperty]
    private bool _noRcReceiver;

    partial void OnResetOnConnectChanged(bool value) => StoreFlag(ConnectOptions.ResetOnConnectKey, value);

    partial void OnEsp32RtsFixChanged(bool value) => StoreFlag(ConnectOptions.Esp32RtsKey, value);

    partial void OnLoadWaypointsOnConnectChanged(bool value) => StoreFlag(ConnectOptions.LoadWaypointsKey, value);

    partial void OnNoRcReceiverChanged(bool value) => StoreFlag(ConnectOptions.NoRcReceiverKey, value);

    private void StoreFlag(string key, bool value)
    {
        if (_loading)
            return;
        Settings.Instance[key] = value.ToString(); // "True" / "False", as Mission Planner and the library read them
        Save();
    }

    // ---- logs

    [ObservableProperty]
    private string _logDirectory;

    [ObservableProperty]
    private string _logDirectoryError = "";

    partial void OnLogDirectoryChanged(string value)
    {
        if (_loading)
            return;
        if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
        {
            Settings.Instance.LogDir = value;
            LogDirectoryError = "";
            Save();
        }
        else
        {
            LogDirectoryError = L.N("This folder does not exist; logs keep going to the previous one.");
        }
    }

    [RelayCommand]
    private async Task BrowseLogDirectoryAsync()
    {
        var folder = await Services.Dialogs.PickFolderAsync("Log folder");
        if (folder != null)
            LogDirectory = folder;
    }

    [RelayCommand]
    private void ResetLogDirectory()
    {
        Settings.Instance.Remove("logdirectory");
        Save();
        _loading = true;
        LogDirectory = Settings.Instance.LogDir;
        LogDirectoryError = "";
        _loading = false;
    }

    // ---- parameter documentation

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateParamDocsCommand))]
    private bool _isUpdatingDocs;

    [ObservableProperty]
    private string _docsStatus = "";

    [ObservableProperty]
    private string _docsFiles = "";

    private bool CanUpdateDocs => !IsUpdatingDocs;

    [RelayCommand(CanExecute = nameof(CanUpdateDocs))]
    private async Task UpdateParamDocsAsync()
    {
        IsUpdatingDocs = true;
        DocsStatus = L.Tr("Downloading parameter documentation...");
        try
        {
            await ParameterMetaDataRepositoryAPMpdef.GetMetaData(true);
            if (Services.Session.IsConnected)
                await Services.Params.LoadMetadataAsync();
            DocsStatus = L.Tr("Parameter documentation updated.");
        }
        catch (Exception ex)
        {
            Log.Warn("metadata update", ex);
            DocsStatus = L.Tr("Update failed:") + " " + ex.Message;
        }
        finally
        {
            IsUpdatingDocs = false;
            UpdateDocsFiles();
        }
    }

    private void UpdateDocsStatus()
    {
        if (!IsUpdatingDocs)
        {
            DocsStatus = Services.Params.MetadataLoaded
                ? L.Format("Documentation loaded for the connected {0}.", Services.Session.Cs.firmware)
                : L.Tr(Services.Session.IsConnected ? "No documentation loaded for this vehicle yet." : "Documentation loads when a vehicle connects.");
        }

        UpdateDocsFiles();
    }

    private void UpdateDocsFiles()
    {
        try
        {
            var dir = Settings.GetDataDirectory();
            var files = Directory.Exists(dir)
                ? Directory.GetFiles(dir, "*.apm.pdef.xml").Select(f => new FileInfo(f)).OrderBy(f => f.Name).ToList()
                : [];
            if (files.Count == 0)
            {
                DocsFiles = L.Tr("No downloaded files yet; the copy shipped with the app is used.");
                return;
            }

            // generic per-vehicle files, then the ones for exact firmware versions (Copter4.6.3...)
            static string Vehicle(FileInfo f) => f.Name[..^".apm.pdef.xml".Length];
            var generic = files.Where(f => !Vehicle(f).Any(char.IsDigit)).ToList();
            var versioned = files.Count - generic.Count;
            var text = generic.Count > 0
                ? $"{string.Join(", ", generic.Select(Vehicle))}\nlast updated {generic.Max(f => f.LastWriteTime):yyyy-MM-dd HH:mm}"
                : "";
            if (versioned > 0)
                text += (text.Length > 0 ? "\n" : "") + $"{versioned} files for specific firmware versions";
            DocsFiles = text;
        }
        catch (Exception ex)
        {
            DocsFiles = ex.Message;
        }
    }

    // ---- telemetry rates

    public IReadOnlyList<StreamRateSetting> Rates { get; }

    private bool _showingRate;

    internal void OnRateChanged(StreamRateSetting setting, int hz)
    {
        if (_loading || _showingRate)
            return;
        PlannerSettings.SetRate(Services, setting.Group, hz);
    }

    /// <summary>Shows a rate set from elsewhere, without setting it again.</summary>
    private void ShowRate(PlannerSettings.StreamGroup group)
    {
        var row = Rates.FirstOrDefault(r => r.Group == group);
        if (row == null)
            return;
        _showingRate = true;
        try
        {
            row.Selected = PlannerSettings.Choice(PlannerSettings.CurrentRate(Services.Session, group));
        }
        finally
        {
            _showingRate = false;
        }
    }

    // ---- units

    [ObservableProperty]
    private UnitChoice _distanceUnit;

    [ObservableProperty]
    private UnitChoice _altitudeUnit;

    [ObservableProperty]
    private UnitChoice _speedUnit;

    partial void OnDistanceUnitChanged(UnitChoice value) => StoreUnit(PlannerSettings.DistanceKey, value);

    partial void OnAltitudeUnitChanged(UnitChoice value) => StoreUnit(PlannerSettings.AltitudeKey, value);

    partial void OnSpeedUnitChanged(UnitChoice value) => StoreUnit(PlannerSettings.SpeedKey, value);

    public string UnitsExample =>
        L.Format("Shown as: distance {0}, altitude {1}, speed {2}", CurrentState.DistanceUnit, CurrentState.AltUnit, CurrentState.SpeedUnit);

    private void StoreUnit(string key, UnitChoice? value)
    {
        if (_loading || value == null)
            return;
        Settings.Instance[key] = value.Key;
        PlannerSettings.ApplyUnits();
        OnPropertyChanged(nameof(UnitsExample));
        Save();
    }

    private static UnitChoice Choose(IReadOnlyList<UnitChoice> choices, string? key) =>
        choices.FirstOrDefault(c => c.Key == key) ?? choices[0];

    private static void Save()
    {
        try
        {
            Settings.Instance.Save();
        }
        catch (Exception ex)
        {
            Log.Warn("settings save", ex);
        }
    }
}
