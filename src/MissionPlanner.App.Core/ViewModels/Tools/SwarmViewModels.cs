using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.Swarm;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.Tools;

/// <summary>
/// TOOLS > Swarm: Mission Planner's multi-MAV formation (Ctrl+F > Swarm, <c>FormationControl</c>). Each vehicle
/// keeps an offset from the leader, which the pilot flies; the others get guided targets at 10 Hz.
/// </summary>
public sealed partial class SwarmFormationViewModel : SwarmPageViewModel
{
    public const string OffsetKeyPrefix = "next_swarm_offset_";

    public SwarmFormationViewModel(AppServices services, ISwarmFleet? fleet = null)
        : base("Swarm",
            "Fly every vehicle on the link in formation with a leader: each holds its offset from the leader (metres to the right, ahead, up), " +
            "turned with the leader's heading. Beta, use at your own risk.",
            services, fleet)
    {
    }

    public FormationSwarm Formation { get; } = new();

    public bool HasLeader => Formation.Leader != null;

    public override bool ShowOffsets => true;

    public bool Gimbal
    {
        get => Formation.Gimbal;
        set
        {
            if (Formation.Gimbal == value)
                return;
            Formation.Gimbal = value;
            OnPropertyChanged();
        }
    }

    protected override string RoleOf(ISwarmVehicle v) => v == Formation.Leader ? "Leader" : "";

    protected override void OnRowAdded(SwarmVehicleRow row)
    {
        if (!Formation.HasOffset(row.Vehicle) && ParseOffset(Settings.Instance[OffsetKey(row.Vehicle)]) is { } saved)
            Formation.SetOffset(row.Vehicle, saved.x, saved.y, saved.z);
        row.SetOffset(Formation.GetOffset(row.Vehicle));
        row.OffsetEdited += OnOffsetEdited;
    }

    private void OnOffsetEdited(SwarmVehicleRow row)
    {
        if (row.Vehicle == Formation.Leader)
            return;
        Formation.SetOffset(row.Vehicle, (double)row.OffsetX, (double)row.OffsetY, (double)row.OffsetZ);
        SaveOffset(row.Vehicle);
    }

    private void SyncOffsets()
    {
        foreach (var row in Vehicles)
        {
            row.SetOffset(Formation.GetOffset(row.Vehicle));
            SaveOffset(row.Vehicle);
        }
    }

    private static string OffsetKey(ISwarmVehicle v) => $"{OffsetKeyPrefix}{v.SysId}_{v.CompId}";

    private void SaveOffset(ISwarmVehicle v)
    {
        var o = Formation.GetOffset(v);
        Settings.Instance[OffsetKey(v)] = string.Format(CultureInfo.InvariantCulture, "{0},{1},{2}", o.x, o.y, o.z);
    }

    internal static Vector3? ParseOffset(string? text)
    {
        var parts = text?.Split(',');
        if (parts is not { Length: 3 })
            return null;
        var values = new double[3];
        for (var i = 0; i < 3; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                return null;
        }

        return new Vector3(values[0], values[1], values[2]);
    }

    /// <summary>The selected vehicle leads; the offsets move so the shape stays.</summary>
    [RelayCommand]
    private void SetLeader()
    {
        if (Selected == null)
        {
            Report("Pick the vehicle to lead.", true);
            return;
        }

        Formation.SetLeader(Fleet.Vehicles, Selected.Vehicle);
        SyncOffsets();
        RefreshVehicles();
        OnPropertyChanged(nameof(HasLeader));
        Report($"{Selected.Name} leads.");
    }

    /// <summary>Takes the offsets from where the vehicles are now (Mission Planner's Update Pos).</summary>
    [RelayCommand]
    private void UpdatePositions()
    {
        if (Formation.Leader == null)
        {
            Report("Set the leader first.", true);
            return;
        }

        var n = Formation.CaptureOffsets(Fleet.Vehicles);
        SyncOffsets();
        Report(n == 0 ? "No vehicle with a position within 200 m of the leader." : $"Offsets taken from {n} vehicle(s).", n == 0);
    }

    [RelayCommand]
    private async Task StartStopAsync()
    {
        if (IsRunning)
        {
            StopLoop();
            Report("Formation stopped.");
            return;
        }

        if (Formation.Leader == null)
        {
            Report("Set the leader first.", true);
            return;
        }

        if (!await Services.Dialogs.ConfirmAsync("Swarm",
                "Start sending formation targets to every vehicle but the leader? This is beta, use at your own risk.", "Start"))
            return;

        StartLoop(TimeSpan.FromMilliseconds(100), Formation.Begin, Formation.Step);
        Report("Formation running: targets at 10 Hz.");
    }

    [RelayCommand]
    private async Task ArmAsync()
    {
        if (await Services.Dialogs.ConfirmAsync("Arm", "Arm every vehicle but the leader?", "Arm"))
            await RunFleetAsync("Arm (excluding leader)", Formation.Arm, "Arm sent to every vehicle but the leader.");
    }

    [RelayCommand]
    private Task DisarmAsync() =>
        RunFleetAsync("Disarm (excluding leader)", Formation.Disarm, "Disarm sent to every vehicle but the leader.");

    [RelayCommand]
    private async Task TakeoffAsync()
    {
        if (await Services.Dialogs.ConfirmAsync("Takeoff", "Switch every vehicle but the leader to GUIDED and take off to 5 m?", "Take off"))
            await RunFleetAsync("Takeoff", Formation.Takeoff, "Takeoff sent to every vehicle but the leader.");
    }

    [RelayCommand]
    private Task LandAsync() => RunFleetAsync("Land (all)", Formation.Land, "Land sent to every vehicle.");

    [RelayCommand]
    private Task GuidedAsync() => RunFleetAsync("Guided mode (excluding leader)", Formation.Guided, "GUIDED sent to every vehicle but the leader.");

    [RelayCommand]
    private Task AutoAsync() => RunFleetAsync("Auto mode (excluding leader)", Formation.Auto, "AUTO sent to every vehicle but the leader.");
}

/// <summary>
/// TOOLS > Follow the leader: Mission Planner's follow-the-leader swarm (Ctrl+F > Follow the leader,
/// <c>FollowPathControl</c>). The others fly the leader's track behind it.
/// </summary>
public sealed partial class SwarmFollowLeaderViewModel : SwarmPageViewModel
{
    public const string DistanceKey = "next_swarm_follow_distance";

    public SwarmFollowLeaderViewModel(AppServices services, ISwarmFleet? fleet = null)
        : base("Follow the leader",
            "The other vehicles fly the leader's track behind it, one every few metres, sent as guided waypoints 5 times a second. Beta, use at your own risk.",
            services, fleet)
    {
        _followDistance = LoadSetting(DistanceKey, 2);
        Follow.FollowDistance = (double)_followDistance;
    }

    public FollowPathSwarm Follow { get; } = new();

    public bool HasLeader => Follow.Leader != null;

    /// <summary>Metres between vehicles along the track.</summary>
    [ObservableProperty]
    private decimal _followDistance;

    [ObservableProperty]
    private string _trailText = "";

    partial void OnFollowDistanceChanged(decimal value)
    {
        Follow.FollowDistance = (double)value;
        SaveSetting(DistanceKey, value);
    }

    protected override string RoleOf(ISwarmVehicle v) => v == Follow.Leader ? "Leader" : "";

    protected override void OnRowRefreshed(SwarmVehicleRow row) =>
        TrailText = Follow.TrailCount == 0 ? "" : $"Leader's track: {Follow.TrailCount} points.";

    [RelayCommand]
    private void SetLeader()
    {
        if (Selected == null)
        {
            Report("Pick the vehicle to lead.", true);
            return;
        }

        if (Follow.Leader != Selected.Vehicle)
            Follow.ClearTrail();
        Follow.Leader = Selected.Vehicle;
        RefreshVehicles();
        OnPropertyChanged(nameof(HasLeader));
        Report($"{Selected.Name} leads.");
    }

    [RelayCommand]
    private void ClearTrail()
    {
        Follow.ClearTrail();
        Report("Track cleared: the followers start again from the leader's position.");
    }

    [RelayCommand]
    private async Task StartStopAsync()
    {
        if (IsRunning)
        {
            StopLoop();
            Report("Follow the leader stopped.");
            return;
        }

        if (Follow.Leader == null)
        {
            Report("Set the leader first.", true);
            return;
        }

        if (!await Services.Dialogs.ConfirmAsync("Follow the leader",
                "Start sending the leader's track to every other vehicle? This is beta, use at your own risk.", "Start"))
            return;

        StartLoop(TimeSpan.FromMilliseconds(200), Follow.Begin, Follow.Step);
        Report("Following: targets at 5 Hz.");
    }

    [RelayCommand]
    private async Task ArmAsync()
    {
        if (await Services.Dialogs.ConfirmAsync("Arm", "Arm every vehicle but the leader?", "Arm"))
            await RunFleetAsync("Arm (excluding leader)", Follow.Arm, "Arm sent to every vehicle but the leader.");
    }

    [RelayCommand]
    private Task DisarmAsync() =>
        RunFleetAsync("Disarm (excluding leader)", Follow.Disarm, "Disarm sent to every vehicle but the leader.");

    [RelayCommand]
    private async Task TakeoffAsync()
    {
        if (await Services.Dialogs.ConfirmAsync("Takeoff", "Switch every vehicle but the leader to GUIDED and take off to 5 m?", "Take off"))
            await RunFleetAsync("Takeoff", Follow.Takeoff, "Takeoff sent to every vehicle but the leader.");
    }

    [RelayCommand]
    private Task LandAsync() => RunFleetAsync("Land (all)", Follow.Land, "Land sent to every vehicle.");
}

/// <summary>An offset of one vehicle in the layout being edited.</summary>
public sealed partial class SwarmLayoutRow : ObservableObject
{
    private readonly SwarmLayout _layout;

    public SwarmLayoutRow(SwarmLayout layout, uint sysid)
    {
        _layout = layout;
        SysId = sysid;
        var o = layout.Offset[sysid];
        _east = (decimal)o.x;
        _north = (decimal)o.y;
        _alt = (decimal)o.z;
    }

    public uint SysId { get; }

    public string Name => "MAV " + SysId.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    private decimal _east;

    [ObservableProperty]
    private decimal _north;

    [ObservableProperty]
    private decimal _alt;

    partial void OnEastChanged(decimal value) => Store();

    partial void OnNorthChanged(decimal value) => Store();

    partial void OnAltChanged(decimal value) => Store();

    private void Store() => _layout.AddOffset(SysId, new Vector3((double)East, (double)North, (double)Alt));
}

/// <summary>
/// TOOLS > Sequence Swarm: Mission Planner's Sequence Swarm (Ctrl+F > Sequence Swarm, <c>LayoutEditor</c>). Layouts
/// place each vehicle (by system id) east/north/up of a start point; the steps fly the layouts in order.
/// </summary>
public sealed partial class SwarmSequenceViewModel : SwarmPageViewModel
{
    public const string FileKey = "next_swarm_sequence_file";

    public SwarmSequenceViewModel(AppServices services, ISwarmFleet? fleet = null)
        : base("Sequence Swarm",
            "Design layouts (where each vehicle is, by system id, in metres east and north of the start point and its height) and fly them one step at a time. " +
            "The start point is where the first vehicle is when the first step runs. Files are the same as Mission Planner's.",
            services, fleet)
    {
    }

    public SequenceSwarm Runner { get; } = new();

    public ObservableCollection<SwarmLayout> Layouts { get; } = new();

    public ObservableCollection<string> Steps { get; } = new();

    public ObservableCollection<SwarmLayoutRow> LayoutRows { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLayout))]
    private SwarmLayout? _selectedLayout;

    public bool HasLayout => SelectedLayout != null;

    [ObservableProperty]
    private int _selectedStepIndex = -1;

    [ObservableProperty]
    private decimal _droneCount = 1;

    /// <summary>"layout : step", as Mission Planner shows the last step run.</summary>
    [ObservableProperty]
    private string _stepText = "";

    public string NextStepText => Runner.Step < Runner.Sequence.Steps.Count
        ? $"Next step {Runner.Step + 1} of {Runner.Sequence.Steps.Count}: {Runner.Sequence.Steps[Runner.Step]}"
        : Runner.Sequence.Steps.Count == 0 ? "No steps yet." : "All steps flown.";

    public override bool NeedsVehicle => false;

    protected override string RoleOf(ISwarmVehicle v) =>
        Runner.Drones.Contains(v.SysId) ? "" : "Not in the layouts";

    private bool _syncing;

    private void Reload()
    {
        _syncing = true;
        try
        {
            var keep = SelectedLayout;
            Layouts.Clear();
            foreach (var l in Runner.Sequence.Layouts)
                Layouts.Add(l);
            Steps.Clear();
            foreach (var s in Runner.Sequence.Steps)
                Steps.Add(s);
            SelectedLayout = keep != null && Layouts.Contains(keep) ? keep : Layouts.FirstOrDefault();
            if (Runner.Drones.Count > 0)
                DroneCount = Runner.Drones.Count;
        }
        finally
        {
            _syncing = false;
        }

        LoadRows();
        OnPropertyChanged(nameof(NextStepText));
    }

    private void LoadRows()
    {
        LayoutRows.Clear();
        if (SelectedLayout == null)
            return;
        foreach (var id in SelectedLayout.Offset.Keys.OrderBy(k => k))
            LayoutRows.Add(new SwarmLayoutRow(SelectedLayout, id));
    }

    partial void OnSelectedLayoutChanged(SwarmLayout? value)
    {
        if (!_syncing)
            LoadRows();
    }

    partial void OnDroneCountChanged(decimal value)
    {
        if (_syncing)
            return;
        Runner.SetDroneCount((int)value);
        LoadRows();
    }

    [RelayCommand]
    private async Task NewLayoutAsync()
    {
        var name = await Services.Dialogs.PromptAsync("New layout", "Layout name", "Layout " + (Runner.Sequence.Layouts.Count + 1));
        if (string.IsNullOrWhiteSpace(name))
            return;
        name = name.Trim();
        if (Runner.Sequence.Find(name) != null)
        {
            Report($"There is already a layout called {name}.", true);
            return;
        }

        var layout = Runner.NewLayout(name, SelectedLayout, (int)DroneCount);
        SelectedLayout = layout;
        Reload();
        SelectedLayout = layout;
        Report($"Layout {name} added (a copy of the one shown).");
    }

    [RelayCommand]
    private void AddStep()
    {
        if (SelectedLayout == null)
        {
            Report("Make a layout first.", true);
            return;
        }

        Runner.AddStep(SelectedLayout.Id);
        Reload();
        SelectedStepIndex = Steps.Count - 1;
    }

    [RelayCommand]
    private void RemoveStep()
    {
        if (SelectedStepIndex < 0)
            return;
        var i = SelectedStepIndex;
        Runner.RemoveStep(i);
        Reload();
        SelectedStepIndex = Math.Min(i, Steps.Count - 1);
    }

    [RelayCommand]
    private void MoveStepUp() => MoveStep(-1);

    [RelayCommand]
    private void MoveStepDown() => MoveStep(1);

    private void MoveStep(int by)
    {
        if (SelectedStepIndex < 0)
            return;
        var to = Runner.MoveStep(SelectedStepIndex, by);
        Reload();
        SelectedStepIndex = to;
    }

    /// <summary>Picking a step shows its layout (as clicking a step does in Mission Planner).</summary>
    partial void OnSelectedStepIndexChanged(int value)
    {
        if (_syncing || value < 0 || value >= Steps.Count)
            return;
        if (Runner.Sequence.Find(Steps[value]) is { } layout)
            SelectedLayout = layout;
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        var path = await Services.Dialogs.OpenFileAsync("Load sequence", new FileFilter("Sequence", "txt", "json"), new FileFilter("All files", "*"));
        if (path == null)
            return;
        try
        {
            LoadFile(path);
            Report($"Loaded {Path.GetFileName(path)}: {Runner.Sequence.Layouts.Count} layouts, {Runner.Sequence.Steps.Count} steps.");
        }
        catch (Exception ex)
        {
            Report($"Could not load {Path.GetFileName(path)} ({ex.Message})", true);
        }
    }

    internal void LoadFile(string path)
    {
        Runner.Load(SwarmSequence.Load(path));
        Settings.Instance[FileKey] = path;
        SelectedLayout = null;
        Reload();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var suggested = Settings.Instance[FileKey] is { Length: > 0 } last ? Path.GetFileName(last) : "sequence.txt";
        var path = await Services.Dialogs.SaveFileAsync("Save sequence", suggested, new FileFilter("Sequence", "txt"));
        if (path == null)
            return;
        try
        {
            Runner.Sequence.Save(path);
            Settings.Instance[FileKey] = path;
            Report($"Saved {Path.GetFileName(path)}.");
        }
        catch (Exception ex)
        {
            Report($"Could not save ({ex.Message})", true);
        }
    }

    [RelayCommand]
    private async Task RunStepAsync()
    {
        if (Runner.Step >= Runner.Sequence.Steps.Count)
        {
            Report(Runner.Sequence.Steps.Count == 0 ? "Add steps first." : "All steps flown; Reset to start again.", true);
            return;
        }

        var step = Runner.Step;
        var name = Runner.Sequence.Steps[step];
        SwarmLayout? flown = null;
        if (await RunFleetAsync($"Step {step + 1}: {name}", v => flown = Runner.RunStep(v), $"Step {step + 1}: vehicles sent to {name}."))
        {
            StepText = $"{name} : {step}";
            if (flown == null)
                Report($"Step {step + 1}: there is no layout called {name}.", true);
        }

        OnPropertyChanged(nameof(NextStepText));
    }

    /// <summary>Back to the first step (the start point stays).</summary>
    [RelayCommand]
    private void ResetStep()
    {
        Runner.Step = 0;
        OnPropertyChanged(nameof(NextStepText));
        Report("Back to the first step.");
    }

    /// <summary>A new start point is taken when the next step runs.</summary>
    [RelayCommand]
    private void ResetStart()
    {
        Runner.StartPosition = null;
        Report("The start point is taken again from the first vehicle when the next step runs.");
    }

    [RelayCommand]
    private async Task TakeoffAsync()
    {
        if (await Services.Dialogs.ConfirmAsync("Takeoff",
                $"Switch every vehicle to GUIDED, arm and take off to {Runner.TakeoffAlt.ToString(CultureInfo.InvariantCulture)} m?", "Take off"))
            await RunFleetAsync("Takeoff", Runner.Takeoff, "Takeoff sent to every vehicle.");
    }
}

/// <summary>
/// TOOLS > Follow Swarm: Mission Planner's Follow Swarm (Ctrl+F > Follow Swarm, <c>Swarm.WaypointLeader</c>). The air
/// master's mission is the path; the drones take off, line up along it and fly ahead of the ground master as
/// it moves, then return along the path and land one at a time.
/// </summary>
public sealed partial class SwarmFollowSwarmViewModel : SwarmPageViewModel
{
    public const string KeyPrefix = "next_swarm_wpleader_";

    public SwarmFollowSwarmViewModel(AppServices services, ISwarmFleet? fleet = null)
        : base("Follow Swarm",
            "Drones fly in a line along a path ahead of a ground master (a person or vehicle with a GPS link) as it moves along the path. " +
            "Beta, use at your own risk.",
            services, fleet)
    {
        _separation = LoadSetting(KeyPrefix + "separation", 5);
        _lead = LoadSetting(KeyPrefix + "lead", 20);
        _offPathTrigger = LoadSetting(KeyPrefix + "offpath", 10);
        _altSeparation = LoadSetting(KeyPrefix + "altsep", 2);
        _wpNavAccel = LoadSetting(KeyPrefix + "accel", 1);
        _v = Settings.Instance.GetBoolean(KeyPrefix + "v");
        _altInterleave = Settings.Instance.GetBoolean(KeyPrefix + "interleave");
        Apply();
        Group.Problem += text => Services.Dispatcher.Post(() => Report(text, true));
    }

    public WaypointLeaderSwarm Group { get; } = new();

    /// <summary>Mission Planner's "How to Use".</summary>
    public string HowTo { get; } =
        "1. Connect all drones including the masters (one link carrying every vehicle).\n" +
        "2. Select the ground master in the list and click Set ground master.\n" +
        "3. Select the air master and click Set air master.\n" +
        "4. Upload the path to the air master (PLAN), then Read path.\n" +
        "5. Set the separation and lead.\n" +
        "6. Let every drone get a GPS lock and calibrate it (otherwise it may fail its pre-arm checks).\n" +
        "7. Make sure the ground master is ready and waiting on the path.\n" +
        "8. Click Start.";

    [ObservableProperty]
    private decimal _separation;

    [ObservableProperty]
    private decimal _lead;

    [ObservableProperty]
    private decimal _offPathTrigger;

    [ObservableProperty]
    private decimal _altSeparation;

    [ObservableProperty]
    private decimal _wpNavAccel;

    [ObservableProperty]
    private bool _v;

    [ObservableProperty]
    private bool _altInterleave;

    [ObservableProperty]
    private string _modeText = "idle";

    [ObservableProperty]
    private string _pathText = "No path read yet.";

    public bool IsReady => Group.IsReady;

    partial void OnSeparationChanged(decimal value) => Save("separation", value);

    partial void OnLeadChanged(decimal value) => Save("lead", value);

    partial void OnOffPathTriggerChanged(decimal value) => Save("offpath", value);

    partial void OnAltSeparationChanged(decimal value) => Save("altsep", value);

    partial void OnWpNavAccelChanged(decimal value) => Save("accel", value);

    partial void OnVChanged(bool value)
    {
        Settings.Instance[KeyPrefix + "v"] = value.ToString();
        Apply();
    }

    partial void OnAltInterleaveChanged(bool value)
    {
        Settings.Instance[KeyPrefix + "interleave"] = value.ToString();
        Apply();
    }

    private void Save(string key, decimal value)
    {
        SaveSetting(KeyPrefix + key, value);
        Apply();
    }

    private void Apply()
    {
        Group.Separation = (double)Separation;
        Group.Lead = (double)Lead;
        Group.OffPathTrigger = (double)OffPathTrigger;
        Group.TakeoffLandAltSep = (double)AltSeparation;
        Group.WpNavAccel = (double)WpNavAccel;
        Group.V = V;
        Group.AltInterleave = AltInterleave;
    }

    protected override string RoleOf(ISwarmVehicle v) =>
        v == Group.AirMaster ? "Air master" : v == Group.GroundMaster ? "Ground master" : "";

    protected override void OnRowRefreshed(SwarmVehicleRow row)
    {
        ModeText = Group.CurrentMode.ToString();
        var drone = Group.Drones.FirstOrDefault(d => d.Vehicle == row.Vehicle);
        row.Extra = drone == null || Group.Path.Count == 0
            ? ""
            : (drone.PathIndex * 0.1).ToString("0.0 m along the path", CultureInfo.InvariantCulture);
    }

    private void UpdatePathText()
    {
        var path = Group.Path;
        PathText = path.Count == 0
            ? "The air master's mission gives no path (it needs waypoints)."
            : string.Format(CultureInfo.InvariantCulture, "Path: {0} waypoints, {1:0} m.", Group.Mission.Count(w => w.id is (ushort)MAVLink.MAV_CMD.WAYPOINT or (ushort)MAVLink.MAV_CMD.SPLINE_WAYPOINT),
                path.Count * 0.1);
    }

    [RelayCommand]
    private void SetGroundMaster()
    {
        if (Selected == null)
            return;
        Group.GroundMaster = Selected.Vehicle;
        Group.SetDrones(Fleet.Vehicles);
        RefreshVehicles();
        OnPropertyChanged(nameof(IsReady));
        Report($"{Selected.Name} is the ground master.");
    }

    [RelayCommand]
    private async Task SetAirMasterAsync()
    {
        if (Selected == null)
            return;
        Group.AirMaster = Selected.Vehicle;
        Group.SetDrones(Fleet.Vehicles);
        RefreshVehicles();
        OnPropertyChanged(nameof(IsReady));
        Report($"{Selected.Name} is the air master.");
        await ReadPathAsync();
    }

    /// <summary>Downloads the air master's mission, the path to fly.</summary>
    [RelayCommand]
    private async Task ReadPathAsync()
    {
        var air = Group.AirMaster;
        if (air == null)
        {
            Report("Set the air master first.", true);
            return;
        }

        IReadOnlyList<Locationwp>? mission = null;
        if (await RunFleetAsync("Reading the air master's mission", _ => mission = air.DownloadMission(), "Path read from the air master."))
        {
            Group.SetMission(mission ?? []);
            UpdatePathText();
        }
    }

    [RelayCommand]
    private async Task StartStopAsync()
    {
        if (IsRunning)
        {
            StopLoop();
            Report("Follow Swarm stopped.");
            return;
        }

        if (!Group.IsReady)
        {
            Report("Set the ground master and the air master first.", true);
            return;
        }

        if (Group.Path.Count == 0)
        {
            await ReadPathAsync();
            if (Group.Path.Count == 0)
            {
                Report("The air master has no path: upload a mission with waypoints to it first.", true);
                return;
            }
        }

        if (!await ConfirmIfFlyingAsync())
            return;

        Group.Stopping = false;
        StartLoop(TimeSpan.FromMilliseconds(100), () => { }, _ => Group.Update());
        Report("Follow Swarm running.");
    }

    protected override void StopLoop()
    {
        Group.Stopping = true;
        base.StopLoop();
    }

    /// <summary>Back to idle: the next start takes off again.</summary>
    [RelayCommand]
    private async Task ResetModeAsync()
    {
        if (!await ConfirmIfFlyingAsync())
            return;
        Group.CurrentMode = WaypointLeaderSwarm.Mode.idle;
        ModeText = Group.CurrentMode.ToString();
    }

    /// <summary>Return along the path.</summary>
    [RelayCommand]
    private void ReturnHome()
    {
        Group.CurrentMode = WaypointLeaderSwarm.Mode.RTH;
        ModeText = Group.CurrentMode.ToString();
    }

    /// <summary>Climb to the landing heights and RTL now (abandon the mission).</summary>
    [RelayCommand]
    private void Rtl()
    {
        Group.CurrentMode = WaypointLeaderSwarm.Mode.LandAlt;
        ModeText = Group.CurrentMode.ToString();
    }
}
