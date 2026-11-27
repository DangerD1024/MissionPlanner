using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GMap.NET;
using GMap.NET.MapProviders;
using log4net;
using MissionPlanner.App.Core.Maps;
using MissionPlanner.App.Core.ViewModels.ConfigScreens;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.FlightData;

/// <summary>
/// Map settings (Mission Planner's map options on Config > Planner), opened with the gear button on
/// the DATA and PLAN maps and from SETTINGS > Map: the imagery the maps start with, where tiles come
/// from, how much flown track is kept and the vehicle's lines. Each change is saved under Mission
/// Planner's key and applied to the maps at once. One page for the app (<see cref="AppServices.MapSettings"/>),
/// so SETTINGS and the dialog always show the same values.
/// </summary>
public sealed partial class MapSettingsViewModel : PageViewModel, IDialogPage
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(MapSettingsViewModel));

    /// <summary>Track lengths offered (points), Mission Planner's range; a stored value not in the list is added.</summary>
    private static readonly int[] TrackLengthPresets = [100, 200, 500, 1000, 2000, 5000, 10000, 20000, 50000, 100000, 200000];

    public static IReadOnlyList<UnitChoice> AccessModes { get; } =
    [
        new(nameof(GMap.NET.AccessMode.ServerAndCache), L.N("Download and keep in the cache")),
        new(nameof(GMap.NET.AccessMode.ServerOnly), L.N("Download only (nothing kept)")),
        new(nameof(GMap.NET.AccessMode.CacheOnly), L.N("Cache only (offline)")),
    ];

    private readonly bool _loading = true;

    public MapSettingsViewModel(AppServices services)
        : base("Map settings")
    {
        Services = services;

        MapProviders = MapProviderNames();
        var stored = Settings.Instance[PlannerSettingsViewModel.MapProviderKey];
        _selectedMapProvider = MapProviders.FirstOrDefault(p => p == stored) ?? MapProviders.FirstOrDefault();

        var mode = MapOptions.AccessMode.ToString();
        _accessMode = AccessModes.FirstOrDefault(m => m.Key == mode) ?? AccessModes[0];

        var track = MapOptions.TrackLength;
        TrackLengths = TrackLengthPresets.Contains(track) ? TrackLengthPresets : TrackLengthPresets.Append(track).Order().ToArray();
        _trackLength = track;

        _displayCog = MapOptions.DisplayCog;
        _displayHeading = MapOptions.DisplayHeading;
        _displayNavBearing = MapOptions.DisplayNavBearing;
        _displayTarget = MapOptions.DisplayTarget;
        _showAirports = MapOptions.ShowAirports;
        _lineLength = MapOptions.LineLength;
        _loading = false;
    }

    public AppServices Services { get; }

    public event EventHandler<bool>? CloseRequested;

    /// <summary>Opens the shared page as a dialog (the gear button on a map, SETTINGS > Map).</summary>
    public static Task ShowAsync(AppServices services) => services.Windows.ShowDialogAsync(services.MapSettings, 560, 640);

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, true);

    // ---- imagery ------------------------------------------------------------------------------

    public IReadOnlyList<string> MapProviders { get; }

    /// <summary>The imagery the map screens start with (each map's own choice wins once made there).</summary>
    [ObservableProperty]
    private string? _selectedMapProvider;

    partial void OnSelectedMapProviderChanged(string? value)
    {
        if (_loading || value == null)
            return;
        Settings.Instance[PlannerSettingsViewModel.MapProviderKey] = value;
        Save();
    }

    /// <summary>Mission Planner's map cache mode (GMap.NET's AccessMode): download, cache, or both.</summary>
    [ObservableProperty]
    private UnitChoice _accessMode;

    partial void OnAccessModeChanged(UnitChoice value)
    {
        if (_loading || value == null || !Enum.TryParse<GMap.NET.AccessMode>(value.Key, out var mode))
            return;
        Settings.Instance[MapOptions.AccessModeKey] = value.Key;
        Save();
        MapTiles.SetAccessMode(mode);
        Services.OnMapSettingsChanged();
    }

    // ---- track and vehicle lines ----------------------------------------------------------------

    public IReadOnlyList<int> TrackLengths { get; }

    /// <summary>Points kept for the flown track on the DATA map; the newest stay when it shrinks.</summary>
    [ObservableProperty]
    private int _trackLength;

    partial void OnTrackLengthChanged(int value)
    {
        if (value >= MapOptions.MinTrackLength && value <= MapOptions.MaxTrackLength)
            Store(MapOptions.TrackLengthKey, value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Black: the GPS course over the ground.</summary>
    [ObservableProperty]
    private bool _displayCog;

    /// <summary>Red: where the vehicle points.</summary>
    [ObservableProperty]
    private bool _displayHeading;

    /// <summary>Green: the course the autopilot wants (while navigating).</summary>
    [ObservableProperty]
    private bool _displayNavBearing;

    /// <summary>Orange: the bearing to the waypoint being flown to (while navigating).</summary>
    [ObservableProperty]
    private bool _displayTarget;

    partial void OnDisplayCogChanged(bool value) => Store(MapOptions.DisplayCogKey, value.ToString());

    partial void OnDisplayHeadingChanged(bool value) => Store(MapOptions.DisplayHeadingKey, value.ToString());

    partial void OnDisplayNavBearingChanged(bool value) => Store(MapOptions.DisplayNavBearingKey, value.ToString());

    partial void OnDisplayTargetChanged(bool value) => Store(MapOptions.DisplayTargetKey, value.ToString());

    /// <summary>Medium and large airports with Mission Planner's circle around them, on the DATA and PLAN maps.</summary>
    [ObservableProperty]
    private bool _showAirports;

    partial void OnShowAirportsChanged(bool value) => Store(MapOptions.ShowAirportsKey, value.ToString());

    /// <summary>Length of the vehicle's lines, pixels.</summary>
    [ObservableProperty]
    private decimal? _lineLength;

    partial void OnLineLengthChanged(decimal? value)
    {
        if (value is { } v && v >= MapOptions.MinLineLength && v <= MapOptions.MaxLineLength)
            Store(MapOptions.LineLengthKey, ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture));
    }

    // ---- helpers --------------------------------------------------------------------------------

    /// <summary>Saves a map option and lets the maps apply it.</summary>
    private void Store(string key, string value)
    {
        if (_loading)
            return;
        Settings.Instance[key] = value;
        Save();
        Services.OnMapSettingsChanged();
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
