using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using MissionPlanner.App.Core.Swarm;

namespace MissionPlanner.App.Core.ViewModels.Tools;

/// <summary>A vehicle in a swarm page's list: its status (Mission Planner's swarm Status panel) and its offset.</summary>
public sealed partial class SwarmVehicleRow : ObservableObject
{
    private bool _loading;

    public SwarmVehicleRow(ISwarmVehicle vehicle) => Vehicle = vehicle;

    public ISwarmVehicle Vehicle { get; }

    public string Name => Vehicle.Name;

    /// <summary>Leader, air master...; empty for a follower.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRole), nameof(CanEditOffset))]
    private string _role = "";

    public bool HasRole => Role.Length > 0;

    [ObservableProperty]
    private string _gps = "";

    [ObservableProperty]
    private string _armed = "";

    [ObservableProperty]
    private string _mode = "";

    [ObservableProperty]
    private string _location = "";

    [ObservableProperty]
    private string _guided = "";

    [ObservableProperty]
    private string _speed = "";

    /// <summary>Extra per-page figure (distance along the path on Follow Swarm).</summary>
    [ObservableProperty]
    private string _extra = "";

    [ObservableProperty]
    private decimal _offsetX;

    [ObservableProperty]
    private decimal _offsetY;

    [ObservableProperty]
    private decimal _offsetZ;

    /// <summary>The leader stays at 0,0,0 (Mission Planner: "Can not move Leader").</summary>
    public bool CanEditOffset => Role.Length == 0;

    /// <summary>Raised when the user edits the offset.</summary>
    public event Action<SwarmVehicleRow>? OffsetEdited;

    public void SetOffset(Utilities.Vector3 o)
    {
        _loading = true;
        try
        {
            OffsetX = (decimal)Math.Round(o.x, 2);
            OffsetY = (decimal)Math.Round(o.y, 2);
            OffsetZ = (decimal)Math.Round(o.z, 2);
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnOffsetXChanged(decimal value) => Edited();

    partial void OnOffsetYChanged(decimal value) => Edited();

    partial void OnOffsetZChanged(decimal value) => Edited();

    private void Edited()
    {
        if (!_loading)
            OffsetEdited?.Invoke(this);
    }

    /// <summary>Copies the telemetry into the status texts.</summary>
    public void Refresh()
    {
        var v = Vehicle;
        var inv = CultureInfo.InvariantCulture;
        Gps = v.GpsFix >= 3 ? "OK " + Math.Max(v.GpsFix, v.GpsFix2).ToString(inv) : "Bad";
        Armed = v.Armed ? "Armed" : "Disarmed";
        Mode = v.Mode;
        Location = string.Format(inv, "{0:0.000000}, {1:0.000000}, {2:0.0} m", v.Lat, v.Lng, v.Alt);
        var g = v.GuidedTarget;
        Guided = g == null ? "" : string.Format(inv, "{0:0.000000}, {1:0.000000}, {2:0.0} m", g.Lat, g.Lng, g.Alt);
        Speed = v.GroundSpeed.ToString("0.0 m/s", inv);
    }
}

/// <summary>
/// The shared part of the swarm pages (Mission Planner's Ctrl+F > Swarm, Follow the leader, Sequence Swarm,
/// Follow Swarm): the vehicles on the link with their status, group commands run on a worker thread, and
/// the update loop that sends the guided targets.
/// </summary>
public abstract partial class SwarmPageViewModel : ToolPageViewModel
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(SwarmPageViewModel));

    private int _ticks;
    private CancellationTokenSource? _loop;

    protected SwarmPageViewModel(string title, string description, AppServices services, ISwarmFleet? fleet)
        : base(title, description, services)
    {
        Fleet = fleet ?? new SessionFleet(services.Session);
    }

    public ISwarmFleet Fleet { get; }

    public ObservableCollection<SwarmVehicleRow> Vehicles { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private SwarmVehicleRow? _selected;

    public bool HasSelection => Selected != null;

    /// <summary>The update loop is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartText))]
    private bool _isRunning;

    public string StartText => IsRunning ? "Stop" : "Start";

    /// <summary>The running loop (tests wait on it).</summary>
    internal Task? LoopTask { get; private set; }

    public bool HasVehicles => Vehicles.Count > 0;

    /// <summary>The list shows each vehicle's formation offset for editing.</summary>
    public virtual bool ShowOffsets => false;

    /// <summary>Role of a vehicle in the list ("Leader", ...), empty for the others.</summary>
    protected abstract string RoleOf(ISwarmVehicle v);

    /// <summary>A row was added to the list.</summary>
    protected virtual void OnRowAdded(SwarmVehicleRow row)
    {
    }

    /// <summary>Called with each row when the list refreshes (twice a second).</summary>
    protected virtual void OnRowRefreshed(SwarmVehicleRow row)
    {
    }

    public override void OnShown()
    {
        base.OnShown();
        RefreshVehicles();
    }

    public override void Tick()
    {
        base.Tick();
        if (++_ticks % 10 == 0)
            RefreshVehicles();
    }

    public override void OnShutdown() => StopLoop();

    /// <summary>Brings the list in line with the vehicles on the link and updates their status.</summary>
    public void RefreshVehicles()
    {
        var now = Fleet.Vehicles;
        for (var i = Vehicles.Count - 1; i >= 0; i--)
        {
            if (!now.Contains(Vehicles[i].Vehicle))
                Vehicles.RemoveAt(i);
        }

        foreach (var v in now)
        {
            if (Vehicles.Any(r => r.Vehicle == v))
                continue;
            var row = new SwarmVehicleRow(v);
            Vehicles.Add(row);
            OnRowAdded(row);
        }

        foreach (var row in Vehicles)
        {
            row.Role = RoleOf(row.Vehicle);
            row.Refresh();
            OnRowRefreshed(row);
        }

        if (Selected == null || !Vehicles.Contains(Selected))
            Selected = Vehicles.FirstOrDefault(r => r.Vehicle.SysId == Services.Session.Vehicle.sysid && r.Vehicle.CompId == Services.Session.Vehicle.compid)
                       ?? Vehicles.FirstOrDefault();
        OnPropertyChanged(nameof(HasVehicles));
    }

    /// <summary>Runs a group command with the vehicles on the link on a worker thread and reports it.</summary>
    protected async Task<bool> RunFleetAsync(string what, Action<IReadOnlyList<ISwarmVehicle>> work, string okText)
    {
        var vehicles = Fleet.Vehicles;
        if (vehicles.Count == 0)
        {
            Report("No vehicles on the link.", true);
            return false;
        }

        if (IsBusy)
            return false;

        IsBusy = true;
        Status = what + "…";
        StatusIsError = false;
        try
        {
            await Task.Run(() => work(vehicles));
            Report(okText);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn(what, ex);
            Report($"{what} failed ({ex.Message})", true);
            return false;
        }
        finally
        {
            IsBusy = false;
            RefreshVehicles();
        }
    }

    /// <summary>Starts the update loop: <paramref name="begin"/> once, then <paramref name="step"/> every period.</summary>
    protected void StartLoop(TimeSpan period, Action begin, Action<IReadOnlyList<ISwarmVehicle>> step)
    {
        StopLoop();
        var cts = _loop = new CancellationTokenSource();
        var token = cts.Token;
        IsRunning = true;
        LoopTask = Task.Run(async () =>
        {
            try
            {
                begin();
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        step(Fleet.Vehicles);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn(Title, ex);
                    }

                    await Task.Delay(period, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Warn(Title, ex);
            }
            finally
            {
                Services.Dispatcher.Post(() =>
                {
                    if (_loop == cts)
                        IsRunning = false;
                });
            }
        });
    }

    protected virtual void StopLoop()
    {
        _loop?.Cancel();
        _loop = null;
        IsRunning = false;
    }

    /// <summary>Asks before starting when a vehicle on the link is already flying (Mission Planner's Follow Swarm check).</summary>
    protected async Task<bool> ConfirmIfFlyingAsync()
    {
        if (!Fleet.Vehicles.Any(v => v.Armed && v.Alt > 1))
            return true;
        return await Services.Dialogs.ConfirmAsync("Continue",
            "There appears to be a drone in the air at the moment. Are you sure you want to continue?", "Continue");
    }

    protected static decimal LoadSetting(string key, decimal fallback) =>
        decimal.TryParse(Utilities.Settings.Instance[key], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    protected static void SaveSetting(string key, decimal value) =>
        Utilities.Settings.Instance[key] = value.ToString(CultureInfo.InvariantCulture);
}
