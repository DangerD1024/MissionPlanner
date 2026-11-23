using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GMap.NET;
using GMap.NET.MapProviders;
using log4net;
using MissionPlanner.App.Core.Maps;
using MissionPlanner.App.Core.Mission;
using MissionPlanner.ArduPilot;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.FlightPlan;

/// <summary>
/// Survey (Grid): Mission Planner's GridUI. Lays a lawnmower pattern over the polygon drawn on the
/// Flight Plan map, previews it with its statistics, and on Accept turns it into mission items
/// (<see cref="Result"/>) for the Flight Plan screen to append. Shown as a dialog.
/// </summary>
public sealed partial class SurveyGridViewModel : PageViewModel, IDialogPage
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(SurveyGridViewModel));

    /// <summary>Inputs are regenerated this long after the last change.</summary>
    public const int DebounceMs = 250;

    private const int TerrainSamples = 200;

    private static readonly HashSet<string> InputProperties =
    [
        nameof(Altitude), nameof(LaneSpacing), nameof(TriggerDistance), nameof(Angle), nameof(AutoAngle),
        nameof(Overshoot1), nameof(Overshoot2), nameof(LeadIn1), nameof(LeadIn2), nameof(SelectedStartFrom),
        nameof(MinLaneSeparation), nameof(Crossgrid), nameof(AddTakeoff), nameof(TakeoffAltitude), nameof(ChangeSpeed),
        nameof(Speed), nameof(SelectedTrigger), nameof(UseSpline), nameof(SelectedFinish), nameof(Frame),
    ];

    private CancellationTokenSource? _debounce;
    private int _generation;
    private bool _started;
    private bool _applying;
    private List<PointLatLngAlt> _grid = new();
    private SurveyOptions _gridOptions = new();
    private PointLatLngAlt _home = new();
    private int _tick;
    private bool _terrainPending;
    private bool _terrainRunning;

    public SurveyGridViewModel(AppServices services)
        : base("Survey (Grid)")
    {
        Services = services;

        MapProvider = PickMapProvider();

        var plane = services.Session.Cs.firmware == Firmwares.ArduPlane;
        var defaults = new SurveyOptions { Speed = plane ? 12 : 5, Frame = services.Mission.DefaultFrame };
        ApplyOptions(SurveyOptions.Load(ReadSetting, defaults) with { Frame = services.Mission.DefaultFrame });
    }

    public AppServices Services { get; }

    /// <summary>Survey area (closed implicitly), filled by the caller before the dialog opens.</summary>
    public List<PointLatLngAlt> Polygon { get; } = new();

    /// <summary>Items to append to the mission, valid after the dialog closed with true.</summary>
    public IReadOnlyList<MissionItem> Result { get; private set; } = [];

    /// <summary>Raised to close the dialog: true = accept <see cref="Result"/>.</summary>
    public event EventHandler<bool>? CloseRequested;

    /// <summary>Raised on the UI thread when the preview changed (redraw the map).</summary>
    public event EventHandler? PreviewChanged;

    /// <summary>Ground altitude (m AMSL) or null while unknown. Replaceable for tests.</summary>
    public Func<double, double, double?> TerrainLookup { get; set; } = PlanTerrain.Altitude;

    /// <summary>Where remembered inputs are written. Replaceable for tests.</summary>
    public Action<string, string> SettingsWriter { get; set; } = WriteSetting;

    public GMapProvider MapProvider { get; }

    // ---- inputs ------------------------------------------------------------------------------

    public static IReadOnlyList<SurveyChoice<Grid.StartPosition>> StartPositions { get; } =
    [
        new("Home", Grid.StartPosition.Home),
        new("Bottom left", Grid.StartPosition.BottomLeft),
        new("Top left", Grid.StartPosition.TopLeft),
        new("Bottom right", Grid.StartPosition.BottomRight),
        new("Top right", Grid.StartPosition.TopRight),
    ];

    public static IReadOnlyList<SurveyChoice<SurveyTrigger>> TriggerOptions { get; } =
    [
        new("By distance", SurveyTrigger.CameraDistance),
        new("None", SurveyTrigger.None),
    ];

    public static IReadOnlyList<SurveyChoice<SurveyFinish>> FinishOptions { get; } =
    [
        new("Nothing", SurveyFinish.None),
        new("Return to launch", SurveyFinish.ReturnToLaunch),
        new("Land at home", SurveyFinish.LandAtHome),
    ];

    public static IReadOnlyList<AltFrame> FrameOptions { get; } = [AltFrame.Relative, AltFrame.Absolute, AltFrame.Terrain];

    [ObservableProperty] private decimal? _altitude;
    [ObservableProperty] private decimal? _laneSpacing;
    [ObservableProperty] private decimal? _triggerDistance;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsAngleEditable))]
    private bool _autoAngle;

    [ObservableProperty] private decimal? _angle;
    [ObservableProperty] private decimal? _overshoot1;
    [ObservableProperty] private decimal? _overshoot2;
    [ObservableProperty] private decimal? _leadIn1;
    [ObservableProperty] private decimal? _leadIn2;
    [ObservableProperty] private SurveyChoice<Grid.StartPosition> _selectedStartFrom = StartPositions[0];
    [ObservableProperty] private decimal? _minLaneSeparation;
    [ObservableProperty] private bool _crossgrid;
    [ObservableProperty] private bool _addTakeoff;
    [ObservableProperty] private decimal? _takeoffAltitude;
    [ObservableProperty] private bool _changeSpeed;
    [ObservableProperty] private decimal? _speed;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(UsesTriggerDistance))]
    private SurveyChoice<SurveyTrigger> _selectedTrigger = TriggerOptions[0];

    [ObservableProperty] private bool _useSpline;
    [ObservableProperty] private SurveyChoice<SurveyFinish> _selectedFinish = FinishOptions[0];
    [ObservableProperty] private AltFrame _frame = AltFrame.Relative;

    public bool IsAngleEditable => !AutoAngle;

    public bool UsesTriggerDistance => SelectedTrigger.Value == SurveyTrigger.CameraDistance;

    // ---- outputs -----------------------------------------------------------------------------

    /// <summary>What the map draws.</summary>
    [ObservableProperty] private SurveyPreview _preview = SurveyPreview.Empty;

    [ObservableProperty] private SurveyStats _stats = SurveyStats.Empty;

    [ObservableProperty] private bool _isBusy;

    /// <summary>Why there is no grid, when there is none.</summary>
    [ObservableProperty] private string? _error;

    [ObservableProperty] private string _areaText = "-";
    [ObservableProperty] private string _distanceText = "-";
    [ObservableProperty] private string _lanesText = "-";
    [ObservableProperty] private string _imagesText = "-";
    [ObservableProperty] private string _flightTimeText = "-";
    [ObservableProperty] private string _photoEveryText = "-";
    [ObservableProperty] private string _itemsText = "-";
    [ObservableProperty] private string _groundText = "-";
    [ObservableProperty] private string _homeText = "";

    /// <summary>The polygon as map points (taken when the dialog opens).</summary>
    public IReadOnlyList<PointLatLng> PolygonPoints { get; private set; } = [];

    /// <summary>Home used for "Start from Home", the route statistics and Land; null until resolved.</summary>
    public PointLatLng? HomePoint { get; private set; }

    /// <summary>The generated grid points (S/SM/M/ME/E tagged).</summary>
    public IReadOnlyList<PointLatLngAlt> GridPoints => _grid;

    /// <summary>The current inputs as one value.</summary>
    public SurveyOptions Options
    {
        get
        {
            var d = new SurveyOptions();
            return new SurveyOptions
            {
                Altitude = V(Altitude, d.Altitude),
                LaneSpacing = V(LaneSpacing, d.LaneSpacing),
                TriggerDistance = V(TriggerDistance, 0),
                AutoAngle = AutoAngle,
                Angle = V(Angle, 0),
                Overshoot1 = V(Overshoot1, 0),
                Overshoot2 = V(Overshoot2, 0),
                LeadIn1 = V(LeadIn1, 0),
                LeadIn2 = V(LeadIn2, 0),
                StartFrom = SelectedStartFrom.Value,
                MinLaneSeparation = V(MinLaneSeparation, 0),
                Crossgrid = Crossgrid,
                AddTakeoff = AddTakeoff,
                TakeoffAltitude = V(TakeoffAltitude, d.TakeoffAltitude),
                ChangeSpeed = ChangeSpeed,
                Speed = V(Speed, d.Speed),
                Trigger = SelectedTrigger.Value,
                UseSpline = UseSpline,
                Finish = SelectedFinish.Value,
                Frame = Frame,
            };
        }
    }

    /// <summary>Sets every input at once (one regeneration).</summary>
    public void ApplyOptions(SurveyOptions o)
    {
        _applying = true;
        try
        {
            Altitude = (decimal)o.Altitude;
            LaneSpacing = (decimal)o.LaneSpacing;
            TriggerDistance = (decimal)o.TriggerDistance;
            AutoAngle = o.AutoAngle;
            Angle = (decimal)Math.Round(o.Angle, 2);
            Overshoot1 = (decimal)o.Overshoot1;
            Overshoot2 = (decimal)o.Overshoot2;
            LeadIn1 = (decimal)o.LeadIn1;
            LeadIn2 = (decimal)o.LeadIn2;
            SelectedStartFrom = StartPositions.FirstOrDefault(c => c.Value == o.StartFrom) ?? StartPositions[0];
            MinLaneSeparation = (decimal)o.MinLaneSeparation;
            Crossgrid = o.Crossgrid;
            AddTakeoff = o.AddTakeoff;
            TakeoffAltitude = (decimal)o.TakeoffAltitude;
            ChangeSpeed = o.ChangeSpeed;
            Speed = (decimal)o.Speed;
            SelectedTrigger = TriggerOptions.FirstOrDefault(c => c.Value == o.Trigger) ?? TriggerOptions[0];
            UseSpline = o.UseSpline;
            SelectedFinish = FinishOptions.FirstOrDefault(c => c.Value == o.Finish) ?? FinishOptions[0];
            Frame = o.Frame;
        }
        finally
        {
            _applying = false;
        }

        ScheduleRegenerate();
    }

    // ---- lifecycle ---------------------------------------------------------------------------

    public override void OnShown()
    {
        LoadCameraSettings();
        if (_started)
            return;
        _ = RegenerateAsync();
    }

    public override void OnHidden()
    {
        _debounce?.Cancel();
        SaveSettings();
    }

    public override void Tick()
    {
        // terrain tiles download in the background: look again every 2 s until they are in
        if (++_tick % 40 == 0 && _terrainPending && !_terrainRunning)
            _ = UpdateGroundAsync();
    }

    // ---- generation --------------------------------------------------------------------------

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_applying || e.PropertyName == null || !InputProperties.Contains(e.PropertyName))
            return;

        if (e.PropertyName == nameof(AutoAngle) && AutoAngle)
            ShowAutoAngle();
        ScheduleRegenerate();
    }

    /// <summary>Regenerates <see cref="DebounceMs"/> after the last input change.</summary>
    private async void ScheduleRegenerate()
    {
        if (!_started)
            return;
        _debounce?.Cancel();
        var cts = new CancellationTokenSource();
        _debounce = cts;
        try
        {
            await Task.Delay(DebounceMs, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await RegenerateAsync();
    }

    /// <summary>Generates the grid from the current inputs now (off the UI thread) and updates the preview and statistics.</summary>
    public async Task RegenerateAsync()
    {
        if (!_started)
        {
            _started = true;
            PolygonPoints = Polygon.Select(p => new PointLatLng(p.Lat, p.Lng)).ToList();
            ResolveHome();
            if (AutoAngle)
                ShowAutoAngle();
        }

        _debounce?.Cancel();
        var seq = ++_generation;
        var options = Options;
        var polygon = Polygon.ToList();
        var home = _home;
        var restoreSpeed = RestoreSpeed();
        IsBusy = true;
        try
        {
            var (grid, stats, preview, itemCount) = await Task.Run(() =>
            {
                var g = SurveyPlanner.Generate(polygon, options, home);
                return (g,
                    SurveyPlanner.ComputeStats(polygon, g, home, options),
                    SurveyPlanner.BuildPreview(polygon, g, home),
                    SurveyPlanner.ToMissionItems(g, options, home, restoreSpeed).Count);
            });
            if (seq != _generation)
                return;

            _grid = grid;
            _gridOptions = options;
            Error = grid.Count > 0 ? null
                : polygon.Count < 3 ? "The survey area needs a polygon of at least 3 points."
                : "No lanes fit in this polygon with these settings.";
            Show(stats, preview, options, grid.Count == 0 ? 0 : itemCount);
        }
        catch (Exception ex)
        {
            if (seq != _generation)
                return;
            if (ex is not SurveyTooLargeException)
                Log.Warn("survey grid", ex);
            _grid = new List<PointLatLngAlt>();
            _gridOptions = options;
            Error = ex is SurveyTooLargeException ? ex.Message : "Could not generate the grid: " + ex.Message;
            Show(SurveyPlanner.ComputeStats(polygon, _grid, home, options), SurveyPlanner.BuildPreview(polygon, _grid, home),
                options, 0);
        }
        finally
        {
            if (seq == _generation)
                IsBusy = false;
        }

        await UpdateGroundAsync();
    }

    private void Show(SurveyStats stats, SurveyPreview preview, SurveyOptions o, int itemCount)
    {
        var inv = CultureInfo.InvariantCulture;
        Stats = stats;
        Preview = preview;
        AreaText = FormatArea(stats.AreaM2);
        var empty = _grid.Count == 0;
        DistanceText = empty ? "-" : MissionEditing.FormatDistance(stats.DistanceM);
        LanesText = empty ? "-" : stats.Lanes.ToString(inv);
        ImagesText = empty || !o.UsesCamera ? "-" : stats.Images.ToString(inv);
        FlightTimeText = empty || o.Speed <= 0 ? "-" : $"{FormatDuration(stats.FlightTime)} at {o.Speed.ToString("0.#", inv)} m/s";
        PhotoEveryText = empty || !o.UsesCamera || o.Speed <= 0
            ? "-"
            : (o.TriggerDistance / o.Speed).ToString("0.0", inv) + " s";
        ItemsText = empty ? "-" : itemCount.ToString(inv);
        PreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Lowest and highest ground under the lanes, from SRTM (Mission Planner's "Ground elevation").</summary>
    private async Task UpdateGroundAsync()
    {
        var grid = _grid;
        if (grid.Count == 0)
        {
            GroundText = "-";
            _terrainPending = false;
            return;
        }

        var step = Math.Max(1, grid.Count / TerrainSamples);
        var samples = grid.Where((_, i) => i % step == 0).Concat(Polygon).ToList();
        var lookup = TerrainLookup;
        _terrainRunning = true;
        try
        {
            var range = await Task.Run(() =>
            {
                double min = double.MaxValue, max = double.MinValue;
                foreach (var p in samples)
                {
                    if (lookup(p.Lat, p.Lng) is not { } alt)
                        return ((double, double)?)null;
                    min = Math.Min(min, alt);
                    max = Math.Max(max, alt);
                }

                return (min, max);
            });
            if (!ReferenceEquals(grid, _grid))
                return;
            _terrainPending = range == null;
            GroundText = range is var (lo, hi)
                ? $"{lo.ToString("0", CultureInfo.InvariantCulture)} – {hi.ToString("0", CultureInfo.InvariantCulture)} m ASL"
                : "loading…";
        }
        catch (Exception ex)
        {
            Log.Warn("survey terrain", ex);
            _terrainPending = false;
            GroundText = "-";
        }
        finally
        {
            _terrainRunning = false;
        }
    }

    private void ShowAutoAngle()
    {
        var was = _applying;
        _applying = true;
        try
        {
            Angle = (decimal)SurveyPlanner.AutoAngle(Polygon);
        }
        finally
        {
            _applying = was;
        }
    }

    /// <summary>Planned home, else the vehicle's home, else the polygon's centre.</summary>
    private void ResolveHome()
    {
        var m = Services.Mission;
        var vehicleHome = Services.Session.Cs.HomeLocation;
        if (m.HomeSet && (m.HomeLat != 0 || m.HomeLng != 0))
        {
            _home = new PointLatLngAlt(m.HomeLat, m.HomeLng, m.HomeAlt);
            HomeText = "Planned home";
        }
        else if (vehicleHome != null && (vehicleHome.Lat != 0 || vehicleHome.Lng != 0))
        {
            _home = new PointLatLngAlt(vehicleHome.Lat, vehicleHome.Lng, vehicleHome.Alt);
            HomeText = "Vehicle home";
        }
        else
        {
            _home = SurveyPlanner.Centroid(Polygon);
            HomeText = "Polygon centre (no home set)";
        }

        HomePoint = new PointLatLng(_home.Lat, _home.Lng);
    }

    /// <summary>The cruise speed DO_CHANGE_SPEED goes back to after the survey (WPNAV_SPEED or WP_SPD).</summary>
    private double? RestoreSpeed()
    {
        try
        {
            if (Services.Params.Get("WPNAV_SPEED") is { } cms && cms > 0)
                return cms / 100;
            if (Services.Params.Get("WP_SPD") is { } ms && ms > 0)
                return ms;
        }
        catch (Exception)
        {
            // no parameters
        }

        return null;
    }

    // ---- buttons -----------------------------------------------------------------------------

    /// <summary>Builds <see cref="Result"/> from the current inputs and closes with true.</summary>
    [RelayCommand]
    private async Task AcceptAsync()
    {
        if (_grid.Count == 0 || _gridOptions != Options || IsBusy)
            await RegenerateAsync();
        if (_grid.Count == 0)
        {
            Error ??= "The grid is empty.";
            return;
        }

        Result = SurveyPlanner.ToMissionItems(_grid, _gridOptions, _home, RestoreSpeed());
        SaveSettings();
        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel()
    {
        _debounce?.Cancel();
        Result = [];
        SaveSettings();
        CloseRequested?.Invoke(this, false);
    }

    // ---- helpers -----------------------------------------------------------------------------

    private void SaveSettings()
    {
        try
        {
            Options.Save(SettingsWriter);
        }
        catch (Exception ex)
        {
            Log.Warn("survey settings", ex);
        }
    }

    private static double V(decimal? value, double fallback) => value is { } d ? (double)d : fallback;

    public static string FormatArea(double m2)
    {
        var inv = CultureInfo.InvariantCulture;
        return m2 <= 0
            ? "-"
            : $"{m2.ToString("#,0", inv)} m² · {(m2 / 10_000).ToString(m2 < 100_000 ? "0.00" : "#,0.0", inv)} ha";
    }

    public static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00} h"
            : $"{t.Minutes}:{t.Seconds:00} min";

    private static GMapProvider PickMapProvider()
    {
        var name = ReadSetting("PlanMapProvider") ?? ReadSetting(ConfigScreens.PlannerSettingsViewModel.MapProviderKey);
        return MapTiles.CommonProviders.FirstOrDefault(p => p.Name == name) ?? MapTiles.CommonProviders[0];
    }

    private static string? ReadSetting(string key)
    {
        try
        {
            return Settings.Instance.ContainsKey(key) ? Settings.Instance[key] : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void WriteSetting(string key, string value)
    {
        try
        {
            Settings.Instance[key] = value;
        }
        catch (Exception ex)
        {
            Log.Warn("settings " + key, ex);
        }
    }
}
