using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GMap.NET.MapProviders;
using log4net;
using MissionPlanner.App.Core.Localization;
using MissionPlanner.App.Core.Maps;
using MissionPlanner.App.Core.Services;
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
        _selected = PlannerSettingsViewModel.RateChoices.FirstOrDefault(c => c.Hz == hz) ?? new RateChoice(hz);
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
    /// Applies the stored rates and units to the libraries, as Mission Planner does at start-up.
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
    }
}

/// <summary>
/// Planner: application settings (Mission Planner's ConfigPlanner): map, log folder, parameter
/// documentation, telemetry rates and display units. Changes apply and save immediately.
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

        MapProviders = MapProviderNames();
        var stored = Settings.Instance[MapProviderKey];
        _selectedMapProvider = MapProviders.FirstOrDefault(p => p == stored) ?? MapProviders.FirstOrDefault();

        _logDirectory = Settings.Instance.LogDir;

        var cs = services.Session.IsConnected ? services.Session.Cs : null;
        Rates =
        [
            new(this, PlannerSettings.StreamGroup.Attitude, L.N("Attitude"), "ATTITUDE, VFR_HUD (EXTRA1, EXTRA2)", cs?.rateattitude ?? CurrentState.rateattitudebackup),
            new(this, PlannerSettings.StreamGroup.Position, L.N("Position"), "GLOBAL_POSITION_INT, GPS (POSITION)", cs?.rateposition ?? CurrentState.ratepositionbackup),
            new(this, PlannerSettings.StreamGroup.Status, L.N("Mode / Status"), "SYS_STATUS, mission (EXTENDED_STATUS)", cs?.ratestatus ?? CurrentState.ratestatusbackup),
            new(this, PlannerSettings.StreamGroup.Rc, L.N("RC"), "RC_CHANNELS, SERVO_OUTPUT_RAW (RC_CHANNELS)", cs?.raterc ?? CurrentState.ratercbackup),
            new(this, PlannerSettings.StreamGroup.Sensors, L.N("Sensors"), "raw IMU, extra (EXTRA3, RAW_SENSORS)", cs?.ratesensors ?? CurrentState.ratesensorsbackup),
        ];

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

    // ---- map

    public IReadOnlyList<string> MapProviders { get; }

    [ObservableProperty]
    private string? _selectedMapProvider;

    partial void OnSelectedMapProviderChanged(string? value)
    {
        if (_loading || value == null)
            return;
        Settings.Instance[MapProviderKey] = value;
        Save();
    }

    private static IReadOnlyList<string> MapProviderNames()
    {
        var names = MapTiles.CommonProviders.Select(p => p.Name).ToList();
        try
        {
            names.AddRange(GMapProviders.List
                .Where(p => p != null && p is not EmptyProvider)
                .Select(p => p.Name)
                .Where(n => !string.IsNullOrEmpty(n) && !names.Contains(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Log.Warn("map providers", ex);
        }

        return names.Distinct().ToList();
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

    internal void OnRateChanged(StreamRateSetting setting, int hz)
    {
        if (_loading)
            return;
        Settings.Instance[PlannerSettings.Key(setting.Group)] = hz.ToString(System.Globalization.CultureInfo.InvariantCulture);
        PlannerSettings.SetStoredRate(setting.Group, hz);
        Save();

        var session = Services.Session;
        if (!session.IsConnected)
            return;
        var streams = PlannerSettings.Apply(session.Cs, setting.Group, hz);
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
