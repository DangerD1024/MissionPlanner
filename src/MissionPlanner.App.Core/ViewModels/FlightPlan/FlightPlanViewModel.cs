using System.Collections.ObjectModel;
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

public sealed record MissionTypeOption(string Name, MAVLink.MAV_MISSION_TYPE Type)
{
    public override string ToString() => Name;
}

public sealed record PlanMapProvider(string Name, GMapProvider Provider)
{
    public override string ToString() => Name;
}

/// <summary>
/// PLAN screen (Mission Planner's Flight Plan): edits the mission, fence and rally lists of
/// <see cref="MissionStore"/> on a map and in a grid, loads and saves WPL files, and reads and
/// writes the lists to the vehicle.
/// </summary>
public sealed partial class FlightPlanViewModel : PageViewModel
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(FlightPlanViewModel));

    // ArduPilot's default SITL location (CMAC, Canberra), as the Flight Data map
    private const double DefaultLat = -35.363261;
    private const double DefaultLng = 149.165230;

    private const string KeyWpRadius = "TXT_WPRad";
    private const string KeyLoiterRadius = "TXT_loiterrad";
    private const string KeyDefaultAlt = "TXT_DefaultAlt";
    private const string KeyAltMode = "FPaltmode";
    private const string KeyMapProvider = "PlanMapProvider";

    public static IReadOnlyList<AltFrame> AltFrameOptions { get; } = [AltFrame.Relative, AltFrame.Absolute, AltFrame.Terrain];

    private readonly object _terrainGate = new();
    private PointLatLng _terrainWanted;
    private bool _terrainRunning;
    private int _tick;
    private PointLatLng? _lastVehicleHome;
    private (MidpointHandle Handle, MissionItem? Item, int VertexIndex)? _midpointDrag;
    private MissionItem? _watchedSelection;
    private bool _zoomedOnce;
    private IReadOnlyList<PointLatLng>? _pendingZoom;

    public FlightPlanViewModel(AppServices services)
        : base("Flight Plan")
    {
        Services = services;
        MissionTypes =
        [
            new MissionTypeOption("MISSION", MAVLink.MAV_MISSION_TYPE.MISSION),
            new MissionTypeOption("FENCE", MAVLink.MAV_MISSION_TYPE.FENCE),
            new MissionTypeOption("RALLY", MAVLink.MAV_MISSION_TYPE.RALLY),
        ];
        _selectedMissionType = MissionTypes[0];
        _currentList = Store.Mission;

        MapProviders = MapTiles.CommonProviders.Select(p => new PlanMapProvider(p.Name, p)).ToArray();
        var savedProvider = ReadSetting(KeyMapProvider);
        _selectedMapProvider = MapProviders.FirstOrDefault(p => p.Name == savedProvider)
                               ?? MapProviders.FirstOrDefault(p => p.Name == Settings.Instance[ConfigScreens.PlannerSettingsViewModel.MapProviderKey])
                               ?? MapProviders[0];

        _catalog = MavCmdCatalog.Load(Firmwares.ArduCopter2);
        LoadDefaults();

        if (Store.HomeSet)
        {
            _centerLat = Store.HomeLat;
            _centerLng = Store.HomeLng;
        }

        Polygon.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(PolygonInfo));
            OnPropertyChanged(nameof(HasPolygon));
            MapChanged?.Invoke(this, EventArgs.Empty);
        };
        Store.Changed += (_, _) => OnStoreChanged();
        Store.PropertyChanged += OnStorePropertyChanged;
        Services.Params.ParamsChanged += (_, _) => LoadRadiiFromParams();

        RefreshCommandOptions();
        RefreshHeaders();
        RebuildGeometry();
    }

    public AppServices Services { get; }

    private Mission.PoiActions? _poi;

    /// <summary>The map's POI menu.</summary>
    public Mission.PoiActions Poi => _poi ??= new Mission.PoiActions(Services);

    public MissionStore Store => Services.Mission;

    /// <summary>Ground altitude lookup (m AMSL, null when unknown); replaced in tests.</summary>
    public Func<double, double, double?> TerrainLookup { get; set; } = PlanTerrain.Altitude;

    /// <summary>The map needs drawing again (lists, selection, polygon or vehicle changed).</summary>
    public event EventHandler? MapChanged;

    /// <summary>The view should fit these points on the map.</summary>
    public event EventHandler<IReadOnlyList<PointLatLng>>? ZoomRequested;

    /// <summary>A zoom asked for while no view was listening (the page is shown before its view exists).</summary>
    public IReadOnlyList<PointLatLng>? TakePendingZoom()
    {
        var pending = _pendingZoom;
        _pendingZoom = null;
        return pending;
    }

    // ---- list selection ----------------------------------------------------------------------

    public IReadOnlyList<MissionTypeOption> MissionTypes { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MissionType), nameof(IsMissionMode), nameof(ClearListText), nameof(ListName))]
    private MissionTypeOption _selectedMissionType;

    public MAVLink.MAV_MISSION_TYPE MissionType => SelectedMissionType.Type;

    public bool IsMissionMode => MissionType == MAVLink.MAV_MISSION_TYPE.MISSION;

    public string ListName => MissionType switch
    {
        MAVLink.MAV_MISSION_TYPE.FENCE => "fence",
        MAVLink.MAV_MISSION_TYPE.RALLY => "rally points",
        _ => "mission",
    };

    public string ClearListText => MissionType switch
    {
        MAVLink.MAV_MISSION_TYPE.FENCE => "Clear Fence",
        MAVLink.MAV_MISSION_TYPE.RALLY => "Clear Rally Points",
        _ => "Clear Mission",
    };

    /// <summary>The list the grid shows and the map edits.</summary>
    [ObservableProperty]
    private ObservableCollection<MissionItem> _currentList;

    [ObservableProperty]
    private MissionItem? _selectedItem;

    /// <summary>Headers of the P1, P2, P3, P4, Lat, Long and Alt columns for the selected row's command.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _headers = MavCmdCatalog.GenericHeaders;

    /// <summary>Commands offered in the grid's command column.</summary>
    [ObservableProperty]
    private IReadOnlyList<MAVLink.MAV_CMD> _commandOptions = [];

    [ObservableProperty]
    private MavCmdCatalog _catalog;

    // ---- map ---------------------------------------------------------------------------------

    public IReadOnlyList<PlanMapProvider> MapProviders { get; }

    [ObservableProperty]
    private PlanMapProvider _selectedMapProvider;

    [ObservableProperty]
    private double _centerLat = DefaultLat;

    [ObservableProperty]
    private double _centerLng = DefaultLng;

    [ObservableProperty]
    private int _zoom = 17;

    public PlanGeometry MissionGeometry { get; private set; } = PlanGeometry.Empty;

    public PlanGeometry FenceGeometry { get; private set; } = PlanGeometry.Empty;

    public PlanGeometry RallyGeometry { get; private set; } = PlanGeometry.Empty;

    public PlanGeometry CurrentGeometry => MissionType switch
    {
        MAVLink.MAV_MISSION_TYPE.FENCE => FenceGeometry,
        MAVLink.MAV_MISSION_TYPE.RALLY => RallyGeometry,
        _ => MissionGeometry,
    };

    public PointLatLng? Home => Store.HomeSet ? new PointLatLng(Store.HomeLat, Store.HomeLng) : null;

    /// <summary>The connected vehicle's position, for its marker.</summary>
    public PointLatLng? VehiclePosition { get; private set; }

    public double VehicleHeading { get; private set; }

    // ---- readouts (top-left of the map) ------------------------------------------------------

    [ObservableProperty]
    private string _distanceText = "Distance: 0 m";

    [ObservableProperty]
    private string _prevText = "Prev: -";

    [ObservableProperty]
    private string _homeDistanceText = "Home: -";

    [ObservableProperty]
    private string _terrainText = "Terrain: -";

    [ObservableProperty]
    private string _pointerText = "";

    // ---- home --------------------------------------------------------------------------------

    public double HomeLat
    {
        get => Store.HomeLat;
        set => Store.SetHome(value, Store.HomeLng, Store.HomeAlt);
    }

    public double HomeLng
    {
        get => Store.HomeLng;
        set => Store.SetHome(Store.HomeLat, value, Store.HomeAlt);
    }

    /// <summary>Home altitude above mean sea level, metres.</summary>
    public double HomeAlt
    {
        get => Store.HomeAlt;
        set => Store.SetHome(Store.HomeLat, Store.HomeLng, value);
    }

    // ---- defaults (bottom bar) ---------------------------------------------------------------

    public double WaypointRadius
    {
        get => Store.WaypointRadius;
        set
        {
            if (value <= 0 || value == Store.WaypointRadius)
                return;
            Store.WaypointRadius = value;
            WriteSetting(KeyWpRadius, value);
            RebuildGeometry();
        }
    }

    public double LoiterRadius
    {
        get => Store.LoiterRadius;
        set
        {
            if (value == 0 || value == Store.LoiterRadius)
                return;
            Store.LoiterRadius = value;
            WriteSetting(KeyLoiterRadius, value);
            RebuildGeometry();
        }
    }

    public double DefaultAlt
    {
        get => Store.DefaultAlt;
        set
        {
            if (value == Store.DefaultAlt)
                return;
            Store.DefaultAlt = value;
            WriteSetting(KeyDefaultAlt, value);
        }
    }

    public AltFrame DefaultFrame
    {
        get => Store.DefaultFrame;
        set
        {
            if (value == Store.DefaultFrame)
                return;
            Store.DefaultFrame = value;
            WriteSetting(KeyAltMode, (int)value);
        }
    }

    // ---- status ------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(ReadCommand), nameof(WriteCommand), nameof(LoadFileCommand), nameof(SaveFileCommand))]
    private bool _isBusy;

    public bool IsIdle => !IsBusy;

    [ObservableProperty]
    private string _status = "";

    /// <summary>Transfer progress 0-100; negative while unknown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    private int _progress;

    public bool IsProgressIndeterminate => Progress < 0;

    // ---- drawing polygon ---------------------------------------------------------------------

    /// <summary>Mission Planner's "drawn polygon" (open loop): survey area, fence source.</summary>
    public ObservableCollection<PointLatLng> Polygon { get; } = new();

    /// <summary>While on, a click on the map adds a polygon point instead of a waypoint.</summary>
    [ObservableProperty]
    private bool _polygonMode;

    public bool HasPolygon => Polygon.Count > 0;

    public string PolygonInfo =>
        Polygon.Count < 3
            ? $"{Polygon.Count} point{(Polygon.Count == 1 ? "" : "s")}"
            : $"{Polygon.Count} points · {MissionEditing.FormatArea(MissionEditing.PolygonArea(Polygon))}";

    // ---- page lifecycle ----------------------------------------------------------------------

    public override void OnShown()
    {
        SyncFirmware();
        if (!_zoomedOnce)
        {
            _zoomedOnce = true;
            var pts = AllPlanPoints();
            if (pts.Count > 0)
                RequestZoom(pts);
        }
    }

    public override void Tick()
    {
        UpdateVehicle();
        if (++_tick % 20 == 0)
        {
            SyncFirmware();
            SyncVehicleHome();
        }
    }

    // ---- grid commands -----------------------------------------------------------------------

    /// <summary>Mission Planner's "Add Below": a new row after the selected one.</summary>
    [RelayCommand]
    private void AddBelow()
    {
        var item = MissionType switch
        {
            MAVLink.MAV_MISSION_TYPE.RALLY => new MissionItem { Command = MAVLink.MAV_CMD.RALLY_POINT, Alt = (float)DefaultAlt },
            MAVLink.MAV_MISSION_TYPE.FENCE => new MissionItem { Command = MAVLink.MAV_CMD.FENCE_RETURN_POINT, Frame = AltFrame.Absolute },
            _ => new MissionItem { Command = MAVLink.MAV_CMD.WAYPOINT, Alt = (float)DefaultAlt, Frame = DefaultFrame },
        };
        var index = SelectedItem is { } sel && CurrentList.IndexOf(sel) is var i and >= 0 ? i + 1 : CurrentList.Count;
        CurrentList.Insert(index, item);
        SelectedItem = item;
    }

    public void DeleteItem(MissionItem item)
    {
        if (!CurrentList.Contains(item))
            return;
        var index = CurrentList.IndexOf(item);
        MissionEditing.Delete(CurrentList, item);
        if (SelectedItem == item || SelectedItem == null)
            SelectedItem = CurrentList.Count == 0 ? null : CurrentList[Math.Min(index, CurrentList.Count - 1)];
    }

    public void MoveUp(MissionItem item) => MissionEditing.Move(CurrentList, item, -1);

    public void MoveDown(MissionItem item) => MissionEditing.Move(CurrentList, item, +1);

    /// <summary>The Delete key: removes the selected row.</summary>
    public void DeleteSelected()
    {
        if (SelectedItem is { } item)
            DeleteItem(item);
    }

    [RelayCommand]
    private async Task ClearListAsync()
    {
        if (CurrentList.Count == 0)
            return;
        if (!await Services.Dialogs.ConfirmAsync(ClearListText, L.Format("Remove all {0} items of the {1}?", CurrentList.Count, ListName), "Clear"))
            return;
        CurrentList.Clear();
        SelectedItem = null;
        Status = ClearListText.Replace("Clear", "Cleared");
    }

    [RelayCommand]
    private void ZoomToMission()
    {
        var pts = CurrentGeometry.AllPoints().ToList();
        if (pts.Count == 0)
            pts = AllPlanPoints().ToList();
        if (Home is { } h)
            pts.Add(h);
        if (pts.Count > 0)
            RequestZoom(pts);
    }

    // ---- files -------------------------------------------------------------------------------

    private static readonly FileFilter[] WaypointFilters =
    [
        new FileFilter("Waypoint files", "waypoints", "txt"),
        new FileFilter("All files", "*"),
    ];

    private static readonly FileFilter[] PolygonFilters =
    [
        new FileFilter("Polygon", "poly"),
        new FileFilter("All files", "*"),
    ];

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task LoadFileAsync()
    {
        var path = await Services.Dialogs.OpenFileAsync($"Load {ListName}", WaypointFilters);
        if (path == null)
            return;
        await LoadFileFromAsync(path);
    }

    /// <summary>Loads a QGC WPL 110 file into the current list.</summary>
    public async Task<bool> LoadFileFromAsync(string path)
    {
        try
        {
            var first = File.ReadLines(path).FirstOrDefault() ?? "";
            if (!first.Contains("QGC WPL", StringComparison.Ordinal))
            {
                await Services.Dialogs.ShowMessageAsync("Load file", L.Format("{0} is not a QGC WPL waypoint file.", Path.GetFileName(path)),
                    MessageKind.Error);
                return false;
            }

            Store.LoadWaypointFile(path, MissionType);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or FormatException)
        {
            await Services.Dialogs.ShowMessageAsync("Load file", "Could not load the file: " + ex.Message, MessageKind.Error);
            return false;
        }

        SelectedItem = null;
        if (IsMissionMode)
            AdoptFrameFromList();
        Status = $"Loaded {Path.GetFileName(path)} ({CurrentList.Count} items)";
        ZoomToMission();
        return true;
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task SaveFileAsync()
    {
        var name = MissionType switch
        {
            MAVLink.MAV_MISSION_TYPE.FENCE => "fence.waypoints",
            MAVLink.MAV_MISSION_TYPE.RALLY => "rally.waypoints",
            _ => "mission.waypoints",
        };
        var path = await Services.Dialogs.SaveFileAsync($"Save {ListName}", name, WaypointFilters);
        if (path == null)
            return;
        try
        {
            Store.SaveWaypointFile(path, MissionType);
            Status = $"Saved {Path.GetFileName(path)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Services.Dialogs.ShowMessageAsync("Save file", "Could not save the file: " + ex.Message, MessageKind.Error);
        }
    }

    // ---- vehicle -----------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task ReadAsync()
    {
        if (!await RequireConnectionAsync("Read"))
            return;
        var type = MissionType;
        var list = CurrentList;
        if (list.Count > 0 &&
            !await Services.Dialogs.ConfirmAsync(L.Format("Read {0}", ListName), "This will clear your existing points, Continue?", "Continue"))
            return;

        var ok = await RunTransferAsync($"Reading {ListName}…",
            progress => Store.ReadFromVehicleAsync(Services.Session, type, progress));
        if (!ok)
            return;

        SelectedItem = null;
        if (type == MAVLink.MAV_MISSION_TYPE.MISSION)
            AdoptFrameFromList();
        Status = $"Read {list.Count} {ListName} item{(list.Count == 1 ? "" : "s")} from the vehicle";
        ZoomToMission();
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task WriteAsync()
    {
        if (!await RequireConnectionAsync("Write"))
            return;
        var type = MissionType;
        var session = Services.Session;
        if (session.Cs.armed &&
            !await Services.Dialogs.ConfirmAsync(L.Format("Write {0}", ListName),
                "The vehicle is armed. Writing now changes what it does next. Continue?", "Write"))
            return;

        if (type == MAVLink.MAV_MISSION_TYPE.MISSION && !Store.HomeSet)
        {
            var vh = session.Cs.HomeLocation;
            if (vh != null && (vh.Lat != 0 || vh.Lng != 0))
            {
                Store.SetHome(vh.Lat, vh.Lng, vh.Alt);
            }
            else if (!await Services.Dialogs.ConfirmAsync("Write mission",
                         "No home location is set (right click the map > Set Home Here). Write the mission anyway? " +
                         "ArduPilot replaces home when it arms.", "Write"))
            {
                return;
            }
        }

        var count = CurrentList.Count;
        var ok = await RunTransferAsync($"Writing {ListName}…",
            progress => Store.WriteToVehicleAsync(session, type, progress));
        if (!ok)
            return;

        if (type == MAVLink.MAV_MISSION_TYPE.MISSION)
        {
            // the vehicle may have adjusted home; Mission Planner reads it back
            var mav = session.Mav;
            var vehicle = session.Vehicle;
            try
            {
                await Task.Run(() => mav.getHomePositionAsync(vehicle.sysid, vehicle.compid));
            }
            catch (Exception ex)
            {
                Log.Info("home position after write", ex);
            }
        }

        Status = $"Wrote {count} {ListName} item{(count == 1 ? "" : "s")} to the vehicle";
    }

    private async Task<bool> RequireConnectionAsync(string action)
    {
        if (Services.Session.IsConnected)
            return true;
        await Services.Dialogs.ShowMessageAsync(action, "Connect to a vehicle first.", MessageKind.Warning);
        return false;
    }

    private async Task<bool> RunTransferAsync(string what, Func<IProgress<OperationProgress>, Task> transfer)
    {
        IsBusy = true;
        Progress = -1;
        Status = what;
        var progress = new Progress<OperationProgress>(p => Services.Dispatcher.Post(() =>
        {
            if (!IsBusy)
                return;
            Progress = p.Percent;
            if (!string.IsNullOrEmpty(p.Status))
                Status = p.Status;
        }));
        try
        {
            await transfer(progress);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn(what, ex);
            Status = "Failed: " + ex.Message;
            await Services.Dialogs.ShowMessageAsync(SelectedMissionType.Name, ex.Message, MessageKind.Error);
            return false;
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
        }
    }

    // ---- map: clicks and drags ---------------------------------------------------------------

    /// <summary>Left click on the map: select a marker, insert at a "+" handle, or add a point.</summary>
    public void MapClick(PointLatLng position, object? hit)
    {
        switch (hit)
        {
            case MissionItem item when CurrentList.Contains(item):
                SelectedItem = item;
                return;
            case MidpointHandle handle:
                InsertAtMidpoint(handle, position);
                return;
            case HomeHandle or PolygonVertexHandle:
                return;
        }

        if (PolygonMode)
        {
            AddPolygonPoint(position);
            return;
        }

        var added = MissionType switch
        {
            MAVLink.MAV_MISSION_TYPE.RALLY => NewRallyPoint(position),
            // Mission Planner adds a small exclusion circle
            MAVLink.MAV_MISSION_TYPE.FENCE => new MissionItem
            {
                Command = MAVLink.MAV_CMD.FENCE_CIRCLE_EXCLUSION, P1 = 5, Lat = position.Lat, Lng = position.Lng,
                Frame = AltFrame.Absolute,
            },
            _ => Store.NewWaypoint(position.Lat, position.Lng),
        };
        CurrentList.Add(added);
        SelectedItem = added;
    }

    public void ItemDragged(object item, PointLatLng position)
    {
        switch (item)
        {
            case MissionItem mi when CurrentList.Contains(mi) && mi.HasLocation:
                mi.Lat = position.Lat;
                mi.Lng = position.Lng;
                break;
            case HomeHandle:
                Store.SetHome(position.Lat, position.Lng, Store.HomeAlt);
                break;
            case PolygonVertexHandle v when v.Index < Polygon.Count:
                Polygon[v.Index] = position;
                break;
            case MidpointHandle m:
                DragMidpoint(m, position);
                break;
        }
    }

    public async void ItemDragCompleted(object item, PointLatLng position)
    {
        ItemDragged(item, position);
        _midpointDrag = null;
        if (item is HomeHandle)
        {
            // home altitude follows the ground under it once the terrain is known
            var alt = await Task.Run(() => TerrainLookup(position.Lat, position.Lng));
            if (alt is { } a && Store.HomeLat == position.Lat && Store.HomeLng == position.Lng)
                Store.SetHome(position.Lat, position.Lng, Math.Round(a, 2));
        }
    }

    /// <summary>Pointer over the map: distance readouts and the terrain under it.</summary>
    public void PointerMoved(PointLatLng position)
    {
        var inv = CultureInfo.InvariantCulture;
        PointerText = position.Lat.ToString("0.0000000", inv) + "  " + position.Lng.ToString("0.0000000", inv);
        if (MissionGeometry.LastPoint is { } last)
        {
            var d = PlanGeometry.Distance(last, position);
            var az = PlanGeometry.Bearing(last, position);
            PrevText = $"Prev: {MissionEditing.FormatDistance(d)}  AZ: {az.ToString("0", inv)}°";
        }
        else
        {
            PrevText = "Prev: -";
        }

        HomeDistanceText = Home is { } h ? "Home: " + MissionEditing.FormatDistance(PlanGeometry.Distance(h, position)) : "Home: -";
        RequestTerrain(position);
    }

    private void RequestTerrain(PointLatLng position)
    {
        lock (_terrainGate)
        {
            _terrainWanted = position;
            if (_terrainRunning)
                return;
            _terrainRunning = true;
        }

        _ = Task.Run(() =>
        {
            while (true)
            {
                PointLatLng p;
                lock (_terrainGate)
                    p = _terrainWanted;

                var alt = TerrainLookup(p.Lat, p.Lng);
                var text = alt is { } a ? $"Terrain: {a.ToString("0", CultureInfo.InvariantCulture)} m" : "Terrain: loading…";
                Services.Dispatcher.Post(() => TerrainText = text);

                lock (_terrainGate)
                {
                    if (_terrainWanted == p)
                    {
                        _terrainRunning = false;
                        return;
                    }
                }
            }
        });
    }

    // ---- map: context menu actions -----------------------------------------------------------

    /// <summary>Insert WP / Insert Spline WP: asks for the number, suggesting the nearest leg.</summary>
    public async Task InsertWaypointAsync(PointLatLng position, bool spline)
    {
        var list = Store.Mission;
        var suggested = MissionEditing.InsertIndexForPoint(Home, list, position) + 1;
        var answer = await Services.Dialogs.PromptAsync(spline ? "Insert Spline WP" : "Insert WP",
            $"Insert the new waypoint as number (1 to {list.Count + 1}):", suggested.ToString(CultureInfo.InvariantCulture));
        if (answer == null)
            return;
        if (!int.TryParse(answer.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number < 1)
        {
            await Services.Dialogs.ShowMessageAsync("Insert WP", "Invalid insert position.", MessageKind.Error);
            return;
        }

        ShowList(MAVLink.MAV_MISSION_TYPE.MISSION);
        var item = Store.NewWaypoint(position.Lat, position.Lng,
            spline ? MAVLink.MAV_CMD.SPLINE_WAYPOINT : MAVLink.MAV_CMD.WAYPOINT);
        list.Insert(Math.Min(number - 1, list.Count), item);
        SelectedItem = item;
    }

    public void AddLoiterUnlimited(PointLatLng position) =>
        AddMissionItem(Store.NewWaypoint(position.Lat, position.Lng, MAVLink.MAV_CMD.LOITER_UNLIM));

    public async Task AddLoiterTurnsAsync(PointLatLng position)
    {
        if (await PromptNumberAsync("Loiter Turns", "Loiter Turns", "3") is not { } turns)
            return;
        var item = Store.NewWaypoint(position.Lat, position.Lng, MAVLink.MAV_CMD.LOITER_TURNS);
        item.P1 = (float)turns;
        AddMissionItem(item);
    }

    public async Task AddLoiterTimeAsync(PointLatLng position)
    {
        if (await PromptNumberAsync("Loiter Time", "Loiter Time (s)", "5") is not { } time)
            return;
        var item = Store.NewWaypoint(position.Lat, position.Lng, MAVLink.MAV_CMD.LOITER_TIME);
        item.P1 = (float)time;
        AddMissionItem(item);
    }

    /// <summary>Takeoff: asks for the altitude (and the pitch on a plane), added at the end as in Mission Planner.</summary>
    public async Task AddTakeoffAsync()
    {
        if (await PromptNumberAsync("Altitude", "Please enter your takeoff altitude in m", "10") is not { } alt)
            return;
        double pitch = 0;
        if (Catalog.Section == MavCmdSection.Plane && !IsQuadPlaneWithoutPitch())
        {
            if (await PromptNumberAsync("Takeoff Pitch", "Please enter your takeoff pitch", "15") is not { } p)
                return;
            pitch = p;
        }

        AddMissionItem(new MissionItem
        {
            Command = MAVLink.MAV_CMD.TAKEOFF,
            P1 = (float)pitch,
            Alt = (float)alt,
            Frame = DefaultFrame,
        });
    }

    public void AddLand(PointLatLng position)
    {
        var item = Store.NewWaypoint(position.Lat, position.Lng, MAVLink.MAV_CMD.LAND);
        item.Alt = 0;
        AddMissionItem(item);
    }

    public void AddReturnToLaunch() =>
        AddMissionItem(new MissionItem { Command = MAVLink.MAV_CMD.RETURN_TO_LAUNCH, Frame = DefaultFrame });

    /// <summary>Set Home Here: home at the point, altitude from the terrain when known.</summary>
    public async Task SetHomeHereAsync(PointLatLng position)
    {
        var alt = await Task.Run(() => TerrainLookup(position.Lat, position.Lng));
        Store.SetHome(position.Lat, position.Lng, alt is { } a ? Math.Round(a, 2) : Store.HomeAlt);
        Status = alt == null
            ? "Home set; terrain is still loading, so its altitude was kept"
            : $"Home set ({alt.Value.ToString("0", CultureInfo.InvariantCulture)} m ASL)";
    }

    public void AddRallyPointHere(PointLatLng position)
    {
        Store.Rally.Add(NewRallyPoint(position));
        Status = $"Rally point {Store.Rally.Count} added";
    }

    // polygon

    public void AddPolygonPoint(PointLatLng position)
    {
        PolygonMode = true;
        Polygon.Add(position);
    }

    [RelayCommand]
    private void ClearPolygon()
    {
        Polygon.Clear();
        PolygonMode = false;
    }

    [RelayCommand]
    private void FinishPolygon() => PolygonMode = false;

    public void DeletePolygonPoint(int index)
    {
        if (index >= 0 && index < Polygon.Count)
            Polygon.RemoveAt(index);
    }

    [RelayCommand]
    private async Task SavePolygonAsync()
    {
        if (Polygon.Count == 0)
            return;
        var path = await Services.Dialogs.SaveFileAsync("Save polygon", "polygon.poly", PolygonFilters);
        if (path == null)
            return;
        try
        {
            MissionEditing.WritePolygonFile(path, Polygon.ToList());
            Status = $"Saved {Path.GetFileName(path)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Services.Dialogs.ShowMessageAsync("Save polygon", "Failed to write the polygon file: " + ex.Message, MessageKind.Error);
        }
    }

    [RelayCommand]
    private async Task LoadPolygonAsync()
    {
        var path = await Services.Dialogs.OpenFileAsync("Load polygon", PolygonFilters);
        if (path == null)
            return;
        try
        {
            var pts = MissionEditing.ReadPolygonFile(path);
            Polygon.Clear();
            foreach (var p in pts)
                Polygon.Add(p);
            Status = $"Loaded {Path.GetFileName(path)} ({pts.Count} points)";
            if (pts.Count > 0)
                RequestZoom(pts);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Services.Dialogs.ShowMessageAsync("Load polygon", "Could not read the polygon file: " + ex.Message, MessageKind.Error);
        }
    }

    /// <summary>Auto WP > Survey (Grid): the survey dialog over the drawn polygon; its items go at the end of the mission.</summary>
    [RelayCommand]
    private async Task SurveyGridAsync()
    {
        if (Polygon.Count < 3)
        {
            await Services.Dialogs.ShowMessageAsync("Survey (Grid)",
                "Draw a polygon of at least 3 points first: right click the map > Draw Polygon > Add Polygon Point.",
                MessageKind.Warning);
            return;
        }

        var survey = new SurveyGridViewModel(Services);
        survey.Polygon.AddRange(Polygon.Select(p => new PointLatLngAlt(p.Lat, p.Lng)));
        if (!await Services.Windows.ShowDialogAsync(survey))
            return;

        ShowList(MAVLink.MAV_MISSION_TYPE.MISSION);
        foreach (var item in survey.Result)
            Store.Mission.Add(item.Clone());
        PolygonMode = false;
        Status = $"Added {survey.Result.Count} survey items";
        ZoomToMission();
    }

    // fence

    /// <summary>Fence > Inclusion / Exclusion: the drawn polygon becomes fence vertices, as in Mission Planner.</summary>
    public async Task FenceFromPolygonAsync(bool inclusion)
    {
        if (Polygon.Count < 3)
        {
            await Services.Dialogs.ShowMessageAsync("Fence", "Draw a polygon of at least 3 points first.", MessageKind.Warning);
            return;
        }

        ShowList(MAVLink.MAV_MISSION_TYPE.FENCE);
        foreach (var item in MissionEditing.FencePolygon(Polygon.ToList(), inclusion))
            Store.Fence.Add(item);
        Polygon.Clear();
        PolygonMode = false;
        Status = inclusion ? "Inclusion fence added" : "Exclusion fence added";
    }

    public async Task FenceCircleAsync(PointLatLng position, bool inclusion)
    {
        if (await PromptNumberAsync(inclusion ? "Inclusion circle" : "Exclusion circle", "Radius (m)", "100") is not { } radius ||
            radius <= 0)
            return;
        ShowList(MAVLink.MAV_MISSION_TYPE.FENCE);
        var item = new MissionItem
        {
            Command = inclusion ? MAVLink.MAV_CMD.FENCE_CIRCLE_INCLUSION : MAVLink.MAV_CMD.FENCE_CIRCLE_EXCLUSION,
            P1 = (float)radius,
            Lat = position.Lat,
            Lng = position.Lng,
            Frame = AltFrame.Absolute,
        };
        Store.Fence.Add(item);
        SelectedItem = item;
    }

    /// <summary>Fence circle around home; the vehicle's FENCE_RADIUS is the starting radius.</summary>
    public async Task FenceHomeCircleAsync()
    {
        var initial = Services.Params.Has("FENCE_RADIUS") && Services.Params.Get("FENCE_RADIUS") is { } r
            ? r.ToString(CultureInfo.InvariantCulture)
            : "300";
        if (await PromptNumberAsync("Fence Circle Around Home", "Radius (m)", initial) is not { } radius || radius <= 0)
            return;
        ShowList(MAVLink.MAV_MISSION_TYPE.FENCE);
        var item = new MissionItem { Command = MAVLink.MAV_CMD.FENCE_HOME_CIRCLE_INCLUSION, P1 = (float)radius, Frame = AltFrame.Absolute };
        Store.Fence.Add(item);
        SelectedItem = item;
    }

    /// <summary>The fence return point (one per fence): replaces an existing one.</summary>
    public void FenceReturnPointHere(PointLatLng position)
    {
        ShowList(MAVLink.MAV_MISSION_TYPE.FENCE);
        var existing = Store.Fence.FirstOrDefault(i => i.Command == MAVLink.MAV_CMD.FENCE_RETURN_POINT);
        if (existing != null)
        {
            existing.Lat = position.Lat;
            existing.Lng = position.Lng;
            SelectedItem = existing;
            return;
        }

        var item = new MissionItem
        {
            Command = MAVLink.MAV_CMD.FENCE_RETURN_POINT, Lat = position.Lat, Lng = position.Lng, Frame = AltFrame.Absolute,
        };
        Store.Fence.Add(item);
        SelectedItem = item;
    }

    /// <summary>Delete from the map's context menu (a marker or a polygon point).</summary>
    public void DeleteHit(object? hit)
    {
        switch (hit)
        {
            case MissionItem item:
                DeleteItem(item);
                break;
            case PolygonVertexHandle v:
                DeletePolygonPoint(v.Index);
                break;
        }
    }

    /// <summary>Switches the grid to a list (Fence from polygon, Survey...).</summary>
    public void ShowList(MAVLink.MAV_MISSION_TYPE type)
    {
        if (MissionType != type)
            SelectedMissionType = MissionTypes.First(t => t.Type == type);
    }

    // ---- internals ---------------------------------------------------------------------------

    partial void OnSelectedMissionTypeChanged(MissionTypeOption value)
    {
        // the command list first, so the grid never shows a row whose command is not offered
        var list = Store.Get(value.Type);
        RefreshCommandOptions(list);
        CurrentList = list;
        SelectedItem = null;
        RefreshHeaders();
        OnPropertyChanged(nameof(CurrentGeometry));
        MapChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedItemChanged(MissionItem? value)
    {
        if (_watchedSelection != null)
            _watchedSelection.PropertyChanged -= OnSelectedItemPropertyChanged;
        _watchedSelection = value;
        if (value != null)
            value.PropertyChanged += OnSelectedItemPropertyChanged;
        RefreshHeaders();
        MapChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedMapProviderChanged(PlanMapProvider value) => WriteSetting(KeyMapProvider, value.Name);

    partial void OnCatalogChanged(MavCmdCatalog value)
    {
        RefreshCommandOptions();
        RefreshHeaders();
    }

    private void OnSelectedItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MissionItem.Command))
            RefreshHeaders();
    }

    private void OnStoreChanged()
    {
        if (SelectedItem != null && !CurrentList.Contains(SelectedItem))
            SelectedItem = null;
        RefreshCommandOptions();
        RebuildGeometry();
    }

    private void OnStorePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MissionStore.HomeLat):
                OnPropertyChanged(nameof(HomeLat));
                break;
            case nameof(MissionStore.HomeLng):
                OnPropertyChanged(nameof(HomeLng));
                break;
            case nameof(MissionStore.HomeAlt):
                OnPropertyChanged(nameof(HomeAlt));
                break;
            case nameof(MissionStore.WaypointRadius):
                OnPropertyChanged(nameof(WaypointRadius));
                break;
            case nameof(MissionStore.LoiterRadius):
                OnPropertyChanged(nameof(LoiterRadius));
                break;
            case nameof(MissionStore.DefaultAlt):
                OnPropertyChanged(nameof(DefaultAlt));
                break;
            case nameof(MissionStore.DefaultFrame):
                OnPropertyChanged(nameof(DefaultFrame));
                break;
        }
    }

    private void RebuildGeometry()
    {
        MissionGeometry = PlanGeometry.ForMission(Home, Store.Mission, Store.WaypointRadius, Store.LoiterRadius);
        FenceGeometry = PlanGeometry.ForFence(Home, Store.Fence);
        RallyGeometry = PlanGeometry.ForRally(Store.Rally);
        DistanceText = "Distance: " + MissionEditing.FormatDistance(MissionGeometry.TotalDistance);
        OnPropertyChanged(nameof(CurrentGeometry));
        MapChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshCommandOptions() => RefreshCommandOptions(CurrentList);

    private void RefreshCommandOptions(IReadOnlyList<MissionItem> list)
    {
        var options = Catalog.CommandsFor(MissionType).ToList();
        // a row whose command is not in the list (another vehicle type, a file) keeps its command
        foreach (var item in list)
        {
            if (!options.Contains(item.Command))
                options.Add(item.Command);
        }

        if (!options.SequenceEqual(CommandOptions))
            CommandOptions = options;
    }

    private void RefreshHeaders()
    {
        var command = SelectedItem?.Command ?? MissionType switch
        {
            MAVLink.MAV_MISSION_TYPE.FENCE => MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION,
            MAVLink.MAV_MISSION_TYPE.RALLY => MAVLink.MAV_CMD.RALLY_POINT,
            _ => MAVLink.MAV_CMD.WAYPOINT,
        };
        var headers = Catalog.HeadersFor(command, MissionType);
        if (!headers.SequenceEqual(Headers))
            Headers = headers;
    }

    private void AddMissionItem(MissionItem item)
    {
        ShowList(MAVLink.MAV_MISSION_TYPE.MISSION);
        Store.Mission.Add(item);
        SelectedItem = item;
    }

    private MissionItem NewRallyPoint(PointLatLng position) => new()
    {
        Command = MAVLink.MAV_CMD.RALLY_POINT,
        Lat = position.Lat,
        Lng = position.Lng,
        Alt = (float)DefaultAlt,
        Frame = AltFrame.Relative,
    };

    private void InsertAtMidpoint(MidpointHandle handle, PointLatLng position)
    {
        switch (handle.Target)
        {
            case MidpointTarget.Mission when IsMissionMode:
                var wp = Store.NewWaypoint(position.Lat, position.Lng);
                Store.Mission.Insert(Math.Min(handle.InsertIndex, Store.Mission.Count), wp);
                SelectedItem = wp;
                _midpointDrag = (handle, wp, -1);
                break;
            case MidpointTarget.FencePolygon when MissionType == MAVLink.MAV_MISSION_TYPE.FENCE:
                var v = MissionEditing.InsertFenceVertex(Store.Fence, handle.Group, handle.InsertIndex, position.Lat, position.Lng);
                SelectedItem = v;
                _midpointDrag = (handle, v, -1);
                break;
            case MidpointTarget.DrawPolygon:
                var index = Math.Min(handle.InsertIndex, Polygon.Count);
                Polygon.Insert(index, position);
                _midpointDrag = (handle, null, index);
                break;
        }
    }

    /// <summary>Dragging a "+" handle inserts a point on the first move, then moves that point.</summary>
    private void DragMidpoint(MidpointHandle handle, PointLatLng position)
    {
        if (_midpointDrag is not { } drag || !ReferenceEquals(drag.Handle, handle))
        {
            InsertAtMidpoint(handle, position);
            return;
        }

        if (drag.Item is { } item)
        {
            item.Lat = position.Lat;
            item.Lng = position.Lng;
        }
        else if (drag.VertexIndex >= 0 && drag.VertexIndex < Polygon.Count)
        {
            Polygon[drag.VertexIndex] = position;
        }
    }

    private async Task<double?> PromptNumberAsync(string title, string message, string initial)
    {
        var answer = await Services.Dialogs.PromptAsync(title, message, initial);
        if (answer == null)
            return null;
        if (double.TryParse(answer.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ||
            double.TryParse(answer.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out v))
            return v;
        await Services.Dialogs.ShowMessageAsync(title, "Invalid number entered.", MessageKind.Error);
        return null;
    }

    private bool IsQuadPlaneWithoutPitch()
    {
        // a quadplane takes off vertically unless Q_OPTIONS bit 1 is set
        var p = Services.Params;
        return p.Has("Q_OPTIONS") && p.Get("Q_OPTIONS") is { } q && ((int)q & (1 << 1)) == 0;
    }

    /// <summary>The default alt frame follows the navigation rows of a loaded mission, as in Mission Planner.</summary>
    private void AdoptFrameFromList()
    {
        var nav = Store.Mission.FirstOrDefault(i => i.Command is MAVLink.MAV_CMD.WAYPOINT or MAVLink.MAV_CMD.SPLINE_WAYPOINT
            or MAVLink.MAV_CMD.TAKEOFF or MAVLink.MAV_CMD.DO_SET_HOME);
        if (nav != null)
            DefaultFrame = nav.Frame;
    }

    private void RequestZoom(IReadOnlyList<PointLatLng> points)
    {
        if (ZoomRequested is { } handler)
            handler(this, points);
        else
            _pendingZoom = points;
    }

    private List<PointLatLng> AllPlanPoints() =>
        MissionGeometry.AllPoints().Concat(FenceGeometry.AllPoints()).Concat(RallyGeometry.AllPoints()).ToList();

    private void SyncFirmware()
    {
        var s = Services.Session;
        if (!s.IsConnected)
            return;
        var section = MavCmdCatalog.SectionFor(s.Cs.firmware);
        if (section != Catalog.Section)
            Catalog = MavCmdCatalog.Load(section);
    }

    /// <summary>Home follows the vehicle's home position when it reports one, as Mission Planner's planner does.</summary>
    private void SyncVehicleHome()
    {
        var s = Services.Session;
        if (!s.IsConnected)
            return;
        var vh = s.Cs.HomeLocation;
        if (vh == null || (vh.Lat == 0 && vh.Lng == 0))
            return;
        var p = new PointLatLng(vh.Lat, vh.Lng);
        if (_lastVehicleHome == p)
            return;
        _lastVehicleHome = p;
        Store.SetHome(vh.Lat, vh.Lng, vh.Alt);

        // nothing planned yet: show where the vehicle is
        if (AllPlanPoints().Count == 0)
            RequestZoom([p]);
    }

    private void UpdateVehicle()
    {
        var s = Services.Session;
        PointLatLng? pos = null;
        double heading = 0;
        if (s.IsConnected && (s.Cs.lat != 0 || s.Cs.lng != 0))
        {
            pos = new PointLatLng(s.Cs.lat, s.Cs.lng);
            heading = s.Cs.yaw;
        }

        if (pos == VehiclePosition && Math.Abs(heading - VehicleHeading) < 0.5)
            return;
        VehiclePosition = pos;
        VehicleHeading = heading;
        MapChanged?.Invoke(this, EventArgs.Empty);
    }

    private void LoadRadiiFromParams()
    {
        var p = Services.Params;
        try
        {
            if (p.Has("WP_RADIUS") && p.Get("WP_RADIUS") is { } wr && wr > 0)
                WaypointRadius = wr;
            else if (p.Has("WPNAV_RADIUS") && p.Get("WPNAV_RADIUS") is { } wn && wn > 0)
                WaypointRadius = wn / 100.0;
            else if (p.Has("WP_RADIUS_M") && p.Get("WP_RADIUS_M") is { } wm && wm > 0)
                WaypointRadius = wm;

            var loiter = p.FirstExisting("LOITER_RAD", "WP_LOITER_RAD");
            if (loiter != null && p.Get(loiter) is { } lr && lr != 0)
                LoiterRadius = lr;
        }
        catch (Exception ex)
        {
            Log.Warn("radius params", ex);
        }
    }

    private void LoadDefaults()
    {
        if (ReadDouble(KeyWpRadius) is { } wp && wp > 0)
            Store.WaypointRadius = wp;
        if (ReadDouble(KeyLoiterRadius) is { } lr && lr != 0)
            Store.LoiterRadius = lr;
        if (ReadDouble(KeyDefaultAlt) is { } alt)
            Store.DefaultAlt = alt;
        if (ReadDouble(KeyAltMode) is { } mode && Enum.IsDefined((AltFrame)(byte)mode))
            Store.DefaultFrame = (AltFrame)(byte)mode;
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

    private static double? ReadDouble(string key) =>
        ReadSetting(key) is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static void WriteSetting(string key, object value)
    {
        try
        {
            Settings.Instance[key] = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }
        catch (Exception ex)
        {
            Log.Warn("settings " + key, ex);
        }
    }
}
