using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.ViewModels.FlightData;
using MissionPlanner.App.Core.ViewModels.FlightPlan;
using MissionPlanner.ArduPilot;

namespace MissionPlanner.App.Core.ViewModels.Mobile;

/// <summary>The phone's bottom tabs.</summary>
public enum MobileTab
{
    Fly,
    Plan,
    Setup,
    Messages,
    Link,
}

/// <summary>
/// The phone app (portrait): Fly (HUD over the map, status bar, big action buttons), Plan,
/// Params, Messages and Link. Reuses the desktop's view-models for the vehicle logic; every
/// "are you sure" goes through the dialog service, which the phone shows as slide-to-confirm.
/// </summary>
public sealed partial class MobileShellViewModel : ObservableObject
{
    private FlightPlanViewModel? _plan;
    private int _tick;

    public MobileShellViewModel(AppServices services)
    {
        Services = services;
        Fly = new FlightDataViewModel(services);
        Link = new MobileLinkViewModel(services);
        Params = new MobileParamsViewModel(services);
        Setup = new MobileSetupViewModel(services, Params);
        Fly.OnShown();
        services.Notified += (_, n) => services.Dispatcher.Post(() => ShowToast(n.Text, n.Kind == MessageKind.Error));
        Fly.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(FlightDataViewModel.ToastText) or nameof(FlightDataViewModel.IsToastVisible) && Fly.IsToastVisible)
                ShowToast(Fly.ToastText, Fly.ToastIsError);
            if (e.PropertyName == nameof(FlightDataViewModel.Modes))
                OnPropertyChanged(nameof(Modes));
        };
        services.Session.StateChanged += (_, _) => services.Dispatcher.Post(() =>
        {
            // a fresh link: the vehicle is what matters now
            if (services.Session.IsConnected && Tab == MobileTab.Link)
                Tab = MobileTab.Fly;
            RefreshActions();
        });
    }

    public AppServices Services { get; }

    public FlightDataViewModel Fly { get; }

    /// <summary>The mission editor (the desktop PLAN logic).</summary>
    public FlightPlanViewModel Plan => _plan ??= new FlightPlanViewModel(Services);

    public MobileLinkViewModel Link { get; }

    public MobileParamsViewModel Params { get; }

    /// <summary>Calibrations, sensors, modes, failsafe... and the parameter list.</summary>
    public MobileSetupViewModel Setup { get; }

    public MobileStatus Status { get; } = new();

    public string Version { get; } = AppVersion.Display;

    // ---- tabs ----------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFly), nameof(IsPlan), nameof(IsSetup), nameof(IsMessages), nameof(IsLink))]
    private MobileTab _tab = MobileTab.Fly;

    public bool IsFly => Tab == MobileTab.Fly;
    public bool IsPlan => Tab == MobileTab.Plan;
    public bool IsSetup => Tab == MobileTab.Setup;
    public bool IsMessages => Tab == MobileTab.Messages;
    public bool IsLink => Tab == MobileTab.Link;

    partial void OnTabChanged(MobileTab oldValue, MobileTab newValue)
    {
        if (oldValue == MobileTab.Plan)
            _plan?.OnHidden();
        if (newValue == MobileTab.Plan)
            Plan.OnShown();
        if (newValue == MobileTab.Setup)
            Setup.Refresh();
        if (newValue == MobileTab.Messages)
            UnreadMessages = 0;
    }

    [RelayCommand]
    private void SelectTab(MobileTab tab) => Tab = tab;

    /// <summary>Messages that arrived while another tab was shown (the badge on Messages).</summary>
    [ObservableProperty]
    private int _unreadMessages;

    private int _seenMessages;

    // ---- the 20 Hz tick ------------------------------------------------------------------------

    /// <summary>Driven by a UI timer at about 20 Hz.</summary>
    public void Tick()
    {
        // Fly runs always: it keeps the HUD, map track and messages current for every tab
        Fly.Tick();
        if (Tab == MobileTab.Plan)
            _plan?.Tick();
        else if (Tab == MobileTab.Setup)
            Setup.Tick();
        if (++_tick % 4 == 0)
        {
            Status.Update(Services.Session);
            if (Tab == MobileTab.Setup && _tick % 20 == 0)
                Setup.Refresh();
            RefreshActions();
            var count = Fly.Messages.Count;
            if (count < _seenMessages)
                _seenMessages = 0;
            if (Tab != MobileTab.Messages && count > _seenMessages)
                UnreadMessages += count - _seenMessages;
            _seenMessages = count;
        }
    }

    // ---- toast ---------------------------------------------------------------------------------

    [ObservableProperty]
    private string _toastText = "";

    [ObservableProperty]
    private bool _toastIsError;

    [ObservableProperty]
    private bool _isToastVisible;

    private int _toastId;

    public async void ShowToast(string text, bool isError = false)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        ToastText = text;
        ToastIsError = isError;
        IsToastVisible = true;
        var id = ++_toastId;
        await Task.Delay(isError ? 6000 : 3500);
        if (id == _toastId)
            IsToastVisible = false;
    }

    [RelayCommand]
    private void DismissToast() => IsToastVisible = false;

    // ---- fly actions ---------------------------------------------------------------------------

    public IReadOnlyList<string> Modes => Fly.Modes;

    /// <summary>Which big buttons this vehicle has (a rover cannot take off or land).</summary>
    [ObservableProperty] private bool _canTakeoff = true;
    [ObservableProperty] private bool _canLand = true;
    [ObservableProperty] private string _pauseMode = "Loiter";
    [ObservableProperty] private string _armButtonText = "Arm";

    private void RefreshActions()
    {
        var cs = Services.Session.Cs;
        var rover = cs.firmware is Firmwares.ArduRover or Firmwares.ArduTracker;
        var plane = cs.firmware == Firmwares.ArduPlane;
        CanTakeoff = !rover;
        CanLand = !rover && (!plane || Find("QLand") != null);
        PauseMode = rover ? "Hold" : cs.firmware == Firmwares.ArduCopter2 ? "Brake" : "Loiter";
        if (Find(PauseMode) == null && Modes.Count > 0)
            PauseMode = Find("Loiter") != null ? "Loiter" : Find("Hold") != null ? "Hold" : PauseMode;
        ArmButtonText = cs.armed ? "Disarm" : "Arm";
    }

    private string? Find(string mode) => FlightModes.Find(Modes, mode);

    [RelayCommand]
    private Task ArmDisarm() => Fly.ArmDisarmCommand.ExecuteAsync(null);

    /// <summary>Copter: Guided + takeoff to a typed height; Plane: the TAKEOFF mode.</summary>
    [RelayCommand]
    private async Task Takeoff()
    {
        if (Services.Session.Cs.firmware == Firmwares.ArduPlane)
        {
            if (await Services.Dialogs.ConfirmAsync("Takeoff", "Switch to TAKEOFF mode? The plane climbs out on its current heading.", "Takeoff"))
                await SetModeAsync("Takeoff", confirm: false);
            return;
        }

        await CopterTakeoffAsync();
    }

    /// <summary>
    /// One slide takes off (as QGroundControl and DJI): the height, then Guided, arm if needed,
    /// and MAV_CMD_NAV_TAKEOFF. A refusal to arm shows the vehicle's reasons.
    /// </summary>
    private async Task CopterTakeoffAsync()
    {
        if (!Fly.CanCommand)
        {
            ShowToast("Connect to a vehicle first", true);
            return;
        }

        var settings = MissionPlanner.Utilities.Settings.Instance;
        var initial = settings["takeoff_alt"] is { Length: > 0 } saved ? saved : "5";
        var answer = await Services.Dialogs.PromptAsync("Takeoff", $"Takeoff height ({Units.Altitude})", initial);
        if (answer == null)
            return;
        if (!double.TryParse(answer.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var alt) || alt <= 0 || alt > 1000)
        {
            ShowToast($"\"{answer}\" is not a height", true);
            return;
        }

        settings["takeoff_alt"] = alt.ToString(CultureInfo.InvariantCulture);
        var s = Services.Session;
        var armed = s.Cs.armed;
        var altText = alt.ToString("0.#", CultureInfo.InvariantCulture) + " " + Units.Altitude;
        if (!await Services.Dialogs.ConfirmAsync("Takeoff",
                armed ? $"Take off to {altText} in Guided mode." : $"The vehicle arms, then takes off to {altText} in Guided mode.", "Take off"))
            return;

        var metres = (float)(alt / CurrentState.multiplieralt);
        ShowToast("Taking off…");
        var (ok, error) = await Task.Run(() =>
        {
            var mav = s.Mav;
            var v = s.Vehicle;
            mav.setMode(v.sysid, v.compid, "GUIDED");
            var until = Environment.TickCount64 + 3000;
            while (Environment.TickCount64 < until && !string.Equals(v.cs.mode, "Guided", StringComparison.OrdinalIgnoreCase))
                Thread.Sleep(100);
            if (!v.cs.armed)
            {
                var (armedOk, reasons) = FlightDataViewModel.ArmCollectingReasons(s, arm: true, force: false);
                if (!armedOk)
                    return (false, "Arming refused: " + (reasons.Length > 0 ? reasons.Split('\n')[0] : "no reason given"));
            }

            return mav.doCommand(v.sysid, v.compid, MAVLink.MAV_CMD.TAKEOFF, 0, 0, 0, 0, 0, 0, metres)
                ? (true, "")
                : (false, "The vehicle rejected the takeoff");
        });
        ShowToast(ok ? $"Taking off to {altText}" : error, !ok);
    }

    [RelayCommand]
    private Task Land() =>
        SetModeAsync(Services.Session.Cs.firmware == Firmwares.ArduPlane ? "QLand" : "Land", confirm: true, "Land here?");

    [RelayCommand]
    private Task ReturnToLaunch() => SetModeAsync("RTL", confirm: true, "Return to launch?");

    [RelayCommand]
    private Task Pause() => SetModeAsync(PauseMode, confirm: false);

    /// <summary>A mode in the sheet, marked when it is the current one.</summary>
    public sealed record ModeOption(string Name, bool IsCurrent);

    [ObservableProperty]
    private IReadOnlyList<ModeOption> _modeOptions = [];

    /// <summary>The flight mode sheet (tap the mode in the top bar).</summary>
    [ObservableProperty]
    private bool _isModePickerOpen;

    [RelayCommand]
    private void ToggleModePicker()
    {
        if (!IsModePickerOpen && !Fly.CanCommand)
        {
            ShowToast("Connect to a vehicle to change the mode", true);
            return;
        }

        var current = Services.Session.Cs.mode ?? "";
        ModeOptions = Modes.Select(m => new ModeOption(m, string.Equals(m, current, StringComparison.OrdinalIgnoreCase))).ToList();
        IsModePickerOpen = !IsModePickerOpen;
    }

    [RelayCommand]
    private Task ChooseMode(string? mode)
    {
        IsModePickerOpen = false;
        return mode == null ? Task.CompletedTask : SetModeAsync(mode, confirm: false);
    }

    /// <summary>The map follows the vehicle again (panning by hand stops it).</summary>
    [RelayCommand]
    private void FollowVehicle() => Fly.AutoPan = true;

    /// <summary>Changes mode through the desktop logic (it checks the mode exists and watches the heartbeat).</summary>
    public async Task SetModeAsync(string mode, bool confirm, string? question = null)
    {
        if (!Fly.CanCommand)
        {
            ShowToast("Connect to a vehicle first", true);
            return;
        }

        var name = Find(mode) ?? mode;
        if (confirm && !await Services.Dialogs.ConfirmAsync(name.ToUpperInvariant(), question ?? $"Change to {name}?", name.ToUpperInvariant()))
            return;
        Fly.SelectedMode = name;
        await Fly.SetModeCommand.ExecuteAsync(null);
    }

    // ---- go here (tap on the Fly map) -----------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGoHere), nameof(GoHereText))]
    private (double Lat, double Lng)? _goHere;

    public bool HasGoHere => GoHere != null;

    public string GoHereText => GoHere is { } p
        ? string.Create(CultureInfo.InvariantCulture, $"{p.Lat:0.000000}, {p.Lng:0.000000}")
        : "";

    /// <summary>A tap on the map while connected offers to fly there.</summary>
    public void MapTapped(double lat, double lng)
    {
        if (!Fly.CanCommand)
            return;
        GoHere = (lat, lng);
    }

    [RelayCommand]
    private void CancelGoHere() => GoHere = null;

    [RelayCommand]
    private async Task ConfirmGoHere()
    {
        if (GoHere is not { } p)
            return;
        GoHere = null;
        if (await Services.Dialogs.ConfirmAsync("Go here", $"Fly to {p.Lat.ToString("0.00000", CultureInfo.InvariantCulture)}, {p.Lng.ToString("0.00000", CultureInfo.InvariantCulture)} in Guided mode?", "Go"))
            await Fly.FlyToHereAsync(p.Lat, p.Lng);
    }

    // ---- HUD / map ---------------------------------------------------------------------------

    /// <summary>The big HUD (map below it), or a small HUD and a bigger map.</summary>
    [ObservableProperty]
    private bool _isHudLarge = MissionPlanner.Utilities.Settings.Instance.GetBoolean("next_mobile_hud_large", true);

    partial void OnIsHudLargeChanged(bool value) => MissionPlanner.Utilities.Settings.Instance["next_mobile_hud_large"] = value.ToString();

    [RelayCommand]
    private void ToggleHudSize() => IsHudLarge = !IsHudLarge;
}
