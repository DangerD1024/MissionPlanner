using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using MissionPlanner.ArduPilot;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.FlightData;

/// <summary>Actions tab: vehicle commands. Every command runs on a worker thread and reports its outcome.</summary>
public sealed partial class FlightDataViewModel
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(FlightDataViewModel));

    private Firmwares? _modesFirmware;

    /// <summary>A vehicle command is in progress; the others wait (they would share the link).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCommand))]
    private bool _isCommandRunning;

    /// <summary>Connected and idle: vehicle commands may be sent.</summary>
    public bool CanCommand => IsConnected && !IsCommandRunning;

    partial void OnIsConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCommand));
        RefreshModes(Services.Session.Cs);
    }

    [ObservableProperty]
    private string _actionStatus = "";

    [ObservableProperty]
    private bool _actionStatusIsError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArmText))]
    private bool _isArmed;

    public string ArmText => IsArmed ? "Disarm" : "Arm";

    // ---- modes -------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModes))]
    private IReadOnlyList<string> _modes = [];

    [ObservableProperty]
    private string? _selectedMode;

    public bool HasModes => Modes.Count > 0;

    /// <summary>Re-reads the firmware's mode table (it depends on the parameter metadata, which can arrive late).</summary>
    [RelayCommand]
    private void ReloadModes() => RefreshModes(Services.Session.Cs, force: true);

    /// <summary>Looks the table up off the UI thread (the first lookup parses the parameter metadata file).</summary>
    private void RefreshModes(CurrentState cs, bool force = false)
    {
        var fw = cs.firmware;
        if (!force && _modesFirmware == fw && Modes.Count > 0)
            return;
        _modesFirmware = fw;
        var request = ++_modesRequest;
        _ = Task.Run(() => FlightModes.For(fw)).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully)
                Services.Dispatcher.Post(() =>
                {
                    if (request == _modesRequest)
                        ApplyModes(t.Result);
                });
        }, TaskScheduler.Default);
    }

    private int _modesRequest;

    private void ApplyModes(IReadOnlyList<string> modes)
    {
        if (modes.SequenceEqual(Modes))
            return;
        var keep = SelectedMode;
        Modes = modes;
        SelectedMode = (keep != null ? FlightModes.Find(modes, keep) : null) ?? FlightModes.Find(modes, "Auto") ?? modes.FirstOrDefault();
    }

    [RelayCommand]
    private Task SetModeAsync() => ChangeModeAsync(SelectedMode);

    [RelayCommand]
    private Task AutoAsync() => ChangeModeAsync("Auto");

    [RelayCommand]
    private Task LoiterAsync() => ChangeModeAsync("Loiter");

    [RelayCommand]
    private Task RtlAsync() => ChangeModeAsync("RTL");

    private async Task ChangeModeAsync(string? requested)
    {
        if (!CheckCanCommand() || string.IsNullOrEmpty(requested))
            return;

        var cs = Services.Session.Cs;
        if (Modes.Count == 0 || _modesFirmware != cs.firmware)
        {
            var fw = cs.firmware;
            _modesFirmware = fw;
            _modesRequest++;
            ApplyModes(await Task.Run(() => FlightModes.For(fw)));
        }

        var mode = FlightModes.Find(Modes, requested);
        if (mode == null)
        {
            Report(Modes.Count == 0
                ? "This vehicle's flight modes are not known, so the mode cannot be set."
                : $"{requested} is not a flight mode of this vehicle.", true);
            return;
        }

        if (cs.failsafe && !await Services.Dialogs.ConfirmAsync("Failsafe", "You are in failsafe, are you sure?", "Change mode"))
            return;

        await RunAsync($"Mode {mode}", (mav, sysid, compid, state) =>
        {
            // setMode sends without an acknowledgement: watch the heartbeat for the result
            mav.setMode(sysid, compid, mode);
            return WaitFor(() => string.Equals(state.mode, mode, StringComparison.OrdinalIgnoreCase), 3000);
        }, $"Mode changed to {mode}", $"The vehicle did not change to {mode}");
    }

    // ---- arm / disarm ------------------------------------------------------------------------

    [RelayCommand]
    private async Task ArmDisarmAsync()
    {
        if (!CheckCanCommand())
            return;

        var s = Services.Session;
        var armed = s.Cs.armed;
        var action = armed ? "Disarm" : "Arm";
        if (!await Services.Dialogs.ConfirmAsync(action, L.Format("Are you sure you want to {0} the vehicle?", action.ToLowerInvariant()), action))
            return;

        IsCommandRunning = true;
        try
        {
            Report(action + "...", false);
            var (ok, reasons) = await Task.Run(() => ArmCollectingReasons(s, !armed, force: false));
            if (ok)
            {
                Report(armed ? "Disarmed" : "Armed", false);
                return;
            }

            var why = reasons.Length > 0 ? reasons : "The vehicle gave no reason.";
            var force = await Services.Dialogs.ConfirmAsync("Force " + action,
                $"{action} failed.\n{why}\n\nForce {action} can bypass safety checks, which can lead to the vehicle crashing and causing serious injuries.\n\nDo you wish to Force {action}?",
                "Force " + action);
            if (!force)
            {
                Report($"{action} refused: {FirstLine(why)}", true);
                return;
            }

            Report("Force " + action + "...", false);
            var (forced, reasons2) = await Task.Run(() => ArmCollectingReasons(s, !armed, force: true));
            Report(forced
                ? (armed ? "Disarmed (forced)" : "Armed (forced)")
                : $"Force {action} rejected by the vehicle{(reasons2.Length > 0 ? ": " + FirstLine(reasons2) : "")}", !forced);
        }
        catch (Exception ex)
        {
            Log.Warn("arm", ex);
            Report($"{action}: no response from the vehicle", true);
        }
        finally
        {
            IsCommandRunning = false;
        }
    }

    /// <summary>Arms or disarms and returns the STATUSTEXT the vehicle sent meanwhile (the reason for a refusal).</summary>
    internal static (bool Ok, string Reasons) ArmCollectingReasons(Session.VehicleSession s, bool arm, bool force)
    {
        var mav = s.Mav;
        var sysid = s.Vehicle.sysid;
        var compid = s.Vehicle.compid;
        var sb = new StringBuilder();
        var gate = new object();
        var sub = mav.SubscribeToPacketType(MAVLink.MAVLINK_MSG_ID.STATUSTEXT, m =>
        {
            if (m.data is MAVLink.mavlink_statustext_t st && st.text != null)
            {
                var text = Encoding.ASCII.GetString(st.text).TrimEnd('\0').Trim();
                lock (gate)
                    sb.AppendLine(text);
            }

            return true;
        }, sysid, compid);
        try
        {
            var ok = mav.doARM(sysid, compid, arm, force);
            if (!ok)
            {
                // the reason can trail the refusal by a moment
                for (var i = 0; i < 10; i++)
                {
                    lock (gate)
                    {
                        if (sb.Length > 0)
                            break;
                    }

                    Thread.Sleep(50);
                }
            }

            lock (gate)
                return (ok, sb.ToString().Trim());
        }
        finally
        {
            mav.UnSubscribeToPacketType(sub);
        }
    }

    // ---- waypoints ---------------------------------------------------------------------------

    /// <summary>0 (home) to the mission's item count.</summary>
    [ObservableProperty]
    private IReadOnlyList<int> _waypointNumbers = [0];

    [ObservableProperty]
    private int _selectedWaypoint;

    [RelayCommand]
    private Task SetWaypointAsync()
    {
        var n = (ushort)Math.Max(0, SelectedWaypoint);
        return RunAsync($"Set WP {n}", (mav, sysid, compid, _) => mav.setWPCurrent(sysid, compid, n),
            $"Current waypoint set to {n}", $"The vehicle rejected waypoint {n}");
    }

    [RelayCommand]
    private Task RestartMissionAsync() =>
        RunAsync("Restart mission", (mav, sysid, compid, _) => mav.setWPCurrent(sysid, compid, 0),
            "Mission restarted", "The vehicle rejected the mission restart");

    // ---- altitude / speed --------------------------------------------------------------------

    [ObservableProperty]
    private decimal? _newAltitude = 100;

    [ObservableProperty]
    private decimal? _newSpeed = 5;

    public string AltitudeUnit => Units.Altitude;

    public string SpeedUnit => Units.Speed;

    [RelayCommand]
    private Task ChangeAltitudeAsync()
    {
        if (NewAltitude is not { } v)
            return Task.CompletedTask;
        var metres = (float)((double)v / CurrentState.multiplieralt);
        return RunAsync("Change altitude", (mav, sysid, compid, _) =>
        {
            mav.setNewWPAlt(sysid, compid, new Locationwp { alt = metres });
            return true;
        }, $"Altitude change to {v} {Units.Altitude} sent", "Altitude change failed");
    }

    [RelayCommand]
    private Task ChangeSpeedAsync()
    {
        if (NewSpeed is not { } v)
            return Task.CompletedTask;
        var ms = (float)((double)v / CurrentState.multiplierspeed);
        return RunAsync("Change speed",
            (mav, sysid, compid, _) => mav.doCommand(sysid, compid, MAVLink.MAV_CMD.DO_CHANGE_SPEED, 0, ms, 0, 0, 0, 0, 0),
            $"Speed changed to {v} {Units.Speed}", "The vehicle rejected the speed change");
    }

    // ---- do action ---------------------------------------------------------------------------

    public IReadOnlyList<FlightActionOption> DoActions => FlightActions.Options;

    [ObservableProperty]
    private FlightActionOption? _selectedDoAction = FlightActions.Options[0];

    [RelayCommand]
    private async Task DoActionAsync()
    {
        if (SelectedDoAction is not { } option || !CheckCanCommand())
            return;
        if (!await Services.Dialogs.ConfirmAsync("Action", L.Format("Are you sure you want to do {0}?", option.Name), option.Name))
            return;
        await RunAsync(option.Name, (mav, sysid, compid, cs) => FlightActions.Execute(mav, sysid, compid, option.Action, cs),
            $"{option.Name}: done", $"{option.Name}: the vehicle rejected the command");
    }

    // ---- home altitude -----------------------------------------------------------------------

    /// <summary>Altitudes are shown above sea level instead of above home (Mission Planner's "Set Home Alt").</summary>
    [ObservableProperty]
    private bool _homeAltOffsetActive;

    [RelayCommand]
    private void ToggleHomeAlt()
    {
        var cs = Services.Session.Cs;
        cs.altoffsethome = cs.altoffsethome != 0 ? 0 : (float)(-cs.HomeAlt / CurrentState.multiplieralt);
        HomeAltOffsetActive = cs.altoffsethome != 0;
        Report(HomeAltOffsetActive ? "Altitude now shown above sea level" : "Altitude now shown above home", false);
    }

    private void UpdateActionState(CurrentState cs)
    {
        IsArmed = cs.armed;
        HomeAltOffsetActive = cs.altoffsethome != 0;
        if (_modesFirmware != cs.firmware)
            RefreshModes(cs);
    }

    // ---- plumbing ----------------------------------------------------------------------------

    private bool CheckCanCommand()
    {
        if (!IsConnected)
        {
            Report("Connect to a vehicle first.", true);
            return false;
        }

        if (IsCommandRunning)
        {
            Report("Another command is still running.", true);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Runs a blocking library call on a worker thread with the selected vehicle's ids, then
    /// reports the outcome. A <see cref="TimeoutException"/> (no acknowledgement) is reported as
    /// "no response".
    /// </summary>
    private async Task RunAsync(string what, Func<MAVLinkInterface, uint, byte, CurrentState, bool> action, string okText,
        string failText)
    {
        if (!CheckCanCommand())
            return;

        var s = Services.Session;
        var mav = s.Mav;
        var vehicle = s.Vehicle;
        var sysid = vehicle.sysid;
        var compid = vehicle.compid;
        var cs = vehicle.cs;

        IsCommandRunning = true;
        Report(what + "...", false);
        try
        {
            var ok = await Task.Run(() => action(mav, sysid, compid, cs));
            Report(ok ? okText : failText, !ok);
        }
        catch (TimeoutException)
        {
            Report($"{what}: no response from the vehicle", true);
        }
        catch (Exception ex)
        {
            Log.Warn(what, ex);
            Report($"{what} failed: {ex.Message}", true);
        }
        finally
        {
            IsCommandRunning = false;
        }
    }

    private void Report(string text, bool isError)
    {
        ActionStatus = $"{DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}  {text}";
        ActionStatusIsError = isError;
        ShowToast(text, isError, isError ? 7000 : 3500);
    }

    private static bool WaitFor(Func<bool> condition, int timeoutMs)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until)
        {
            if (condition())
                return true;
            Thread.Sleep(100);
        }

        return condition();
    }

    private static string FirstLine(string text)
    {
        var i = text.IndexOfAny(['\r', '\n']);
        return i < 0 ? text : text[..i];
    }
}
