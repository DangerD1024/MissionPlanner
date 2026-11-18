using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.Session;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Calibration;
using POS = MAVLink.ACCELCAL_VEHICLE_POS;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>One of the six orientations, for the step strip.</summary>
public sealed partial class AccelStepViewModel(POS position, string name) : ObservableObject
{
    public POS Position { get; } = position;

    public string Name { get; } = name;

    [ObservableProperty]
    private bool _isCurrent;

    [ObservableProperty]
    private bool _isDone;
}

/// <summary>
/// Accel Calibration: Mission Planner's ConfigAccelerometerCalibration. "Calibrate Accel" runs the
/// 6-position calibration (<see cref="AccelCalibrationProcess"/>), "Calibrate Level" sets the
/// level trims (PREFLIGHT_CALIBRATION p5 = 2) and "Simple Accel Cal" the one-position
/// calibration (p5 = 4). The vehicle's STATUSTEXT messages are logged while calibrating.
/// </summary>
public sealed partial class AccelCalibrationViewModel : PageViewModel
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(AccelCalibrationViewModel));

    private readonly ICalibrationLink _link;

    public AccelCalibrationViewModel(AppServices services)
        : this(services, new VehicleCalibrationLink(services.Session))
    {
    }

    internal AccelCalibrationViewModel(AppServices services, ICalibrationLink link)
        : base("Accel Calibration")
    {
        Services = services;
        _link = link;
        Process = new AccelCalibrationProcess(link);
        Process.Changed += (_, _) => services.Dispatcher.Post(SyncFromProcess);
        Process.StatusText += (_, text) => services.Dispatcher.Post(() => AddLog(text));
        services.Session.StateChanged += (_, state) => services.Dispatcher.Post(() => OnLinkState(state));

        Steps =
        [
            new(POS.LEVEL, "Level"),
            new(POS.LEFT, "Left"),
            new(POS.RIGHT, "Right"),
            new(POS.NOSEDOWN, "Nose down"),
            new(POS.NOSEUP, "Nose up"),
            new(POS.BACK, "Back"),
        ];
        SyncFromProcess();
    }

    public AppServices Services { get; }

    public AccelCalibrationProcess Process { get; }

    public IReadOnlyList<AccelStepViewModel> Steps { get; }

    /// <summary>The vehicle's messages during the calibration, newest last.</summary>
    public ObservableCollection<string> Messages { get; } = new();

    public StatusLine AccelStatus { get; } = new();

    public StatusLine LevelStatus { get; } = new();

    public StatusLine SimpleStatus { get; } = new();

    /// <summary>Large instruction ("Place vehicle on its LEFT side").</summary>
    [ObservableProperty]
    private string _instruction = "";

    /// <summary>What the vehicle said last, under the instruction.</summary>
    [ObservableProperty]
    private string _detail = "";

    /// <summary>Orientation drawn on the page: LEVEL..BACK, or 0 for none.</summary>
    [ObservableProperty]
    private int _position;

    [ObservableProperty]
    private string _accelButtonText = "Calibrate Accel";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartOther))]
    [NotifyCanExecuteChangedFor(nameof(CalibrateLevelCommand), nameof(SimpleAccelCommand), nameof(CancelCommand))]
    private bool _isCalibrating;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartOther))]
    [NotifyCanExecuteChangedFor(nameof(CalibrateLevelCommand), nameof(SimpleAccelCommand), nameof(AccelCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isFinished;

    [ObservableProperty]
    private bool _succeeded;

    [ObservableProperty]
    private string _levelButtonText = "Calibrate Level";

    [ObservableProperty]
    private string _simpleButtonText = "Simple Accel Cal";

    /// <summary>Level and simple calibration are only offered while the 6-position one is not running.</summary>
    public bool CanStartOther => !IsCalibrating && !IsBusy && Services.Session.IsConnected;

    public bool HasMessages => Messages.Count > 0;

    /// <summary>"Calibrate Accel", then "Click when Done" for each position the vehicle asks for.</summary>
    [RelayCommand(CanExecute = nameof(CanAccel))]
    private async Task AccelAsync()
    {
        if (Process.CanConfirm)
        {
            try
            {
                await Process.ConfirmPositionAsync();
            }
            catch (Exception ex)
            {
                Log.Warn("accel position", ex);
                AccelStatus.Set(StatusKind.Error, "Command failed: " + ex.Message);
            }

            return;
        }

        if (Process.IsActive)
            return;

        if (!await Services.Dialogs.ConfirmAsync("Calibrate Accel",
                "Start the 6-position accelerometer calibration?\n\nThe autopilot will ask you to place the vehicle level, " +
                "on its left and right sides, nose down, nose up and on its back. Click when done after each position, " +
                "and keep the vehicle still while it samples.", "Start"))
            return;

        Messages.Clear();
        OnPropertyChanged(nameof(HasMessages));
        AccelStatus.Clear();
        IsBusy = true;
        try
        {
            if (!await Process.StartAsync())
                AccelStatus.Set(StatusKind.Error, "Command failed: the vehicle did not accept the calibration.");
        }
        catch (Exception ex)
        {
            Log.Warn("accel start", ex);
            AccelStatus.Set(StatusKind.Error, "Failed to start the calibration: " + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // usable to start, and then only while a position waits for confirmation; "Done" stays
    // disabled after a successful run until the page is shown again, as in Mission Planner
    private bool CanAccel() =>
        !IsBusy && Services.Session.IsConnected && Process.State != AccelCalState.Succeeded &&
        (Process.CanConfirm || !Process.IsActive);

    [RelayCommand(CanExecute = nameof(IsCalibrating))]
    private void Cancel()
    {
        Process.Cancel();
        AccelStatus.Set(StatusKind.Warning, "Calibration abandoned. The autopilot stops waiting by itself after its timeout.");
    }

    [RelayCommand(CanExecute = nameof(CanStartOther))]
    private Task CalibrateLevelAsync() => RunSimpleCommandAsync(2, LevelStatus,
        "Calibrate Level",
        "Place the autopilot flat and level and keep it still, then press Start.\n\nThis sets the accelerometer offsets for level (AHRS trims).",
        text => LevelButtonText = text, "Calibrate Level");

    [RelayCommand(CanExecute = nameof(CanStartOther))]
    private Task SimpleAccelAsync() => RunSimpleCommandAsync(4, SimpleStatus,
        "Simple Accel Calibration",
        "Place the vehicle level and keep it still, then press Start.\n\nThis sets the accelerometer scale factors for level flight (1 axis).",
        text => SimpleButtonText = text, "Simple Accel Cal");

    private async Task RunSimpleCommandAsync(float p5, StatusLine status, string title, string question, Action<string> setButton, string idleText)
    {
        if (!await Services.Dialogs.ConfirmAsync(title, question, "Start"))
            return;

        IsBusy = true;
        setButton("Calibrating…");
        status.Set(StatusKind.Info, "Calibrating, keep the vehicle still…");
        try
        {
            // waits for the autopilot's answer (up to 25 s)
            var ok = await _link.CommandAsync(MAVLink.MAV_CMD.PREFLIGHT_CALIBRATION, 0, 0, 0, 0, p5, 0, 0);
            if (ok)
            {
                setButton("Completed");
                status.Set(StatusKind.Success, "Completed.");
            }
            else
            {
                setButton(idleText);
                status.Set(StatusKind.Error, "Command failed: the autopilot rejected the calibration.");
            }
        }
        catch (Exception ex)
        {
            Log.Warn(title, ex);
            setButton(idleText);
            status.Set(StatusKind.Error, $"{title} failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void AddLog(string text)
    {
        Messages.Add($"{DateTime.Now:HH:mm:ss}  {text}");
        while (Messages.Count > 200)
            Messages.RemoveAt(0);
        OnPropertyChanged(nameof(HasMessages));
    }

    private void SyncFromProcess()
    {
        var state = Process.State;
        var pos = Process.Position;
        IsCalibrating = Process.IsActive;
        IsFinished = state is AccelCalState.Succeeded or AccelCalState.Failed;
        Succeeded = state == AccelCalState.Succeeded;
        Position = pos is { } p ? (int)p : state == AccelCalState.Idle || IsFinished ? 0 : Position;

        (Instruction, AccelButtonText) = state switch
        {
            AccelCalState.Starting => ("Starting the calibration…", "Calibrate Accel"),
            AccelCalState.WaitingForVehicle => ("Waiting for the vehicle…", "Click when Done"),
            AccelCalState.PositionRequested when pos is { } requested => (PositionText(requested), "Click when Done"),
            AccelCalState.Succeeded => ("Calibration successful", "Done"),
            AccelCalState.Failed => ("Calibration failed", "Calibrate Accel"),
            _ => ("Place the vehicle level and click Calibrate Accel", "Calibrate Accel"),
        };

        Detail = state switch
        {
            AccelCalState.Idle => "The autopilot will ask for each edge in turn (3-axis min/max).",
            AccelCalState.Succeeded or AccelCalState.Failed => "",
            AccelCalState.PositionRequested when Process.Confirmed == pos =>
                Process.Instruction + "  —  sampling, keep the vehicle still",
            _ => Process.Instruction,
        };

        if (state == AccelCalState.Idle && Process.Instruction.Length > 0)
            AccelStatus.Set(StatusKind.Error, Process.Instruction);
        else if (state == AccelCalState.Succeeded)
            AccelStatus.Set(StatusKind.Success, Process.Instruction);
        else if (state == AccelCalState.Failed)
            AccelStatus.Set(StatusKind.Error, Process.Instruction + " — place the vehicle level and try again.");

        // step strip: everything before the requested position is done
        var currentIndex = pos is { } cp ? Steps.ToList().FindIndex(s => s.Position == cp) : -1;
        for (var i = 0; i < Steps.Count; i++)
        {
            Steps[i].IsCurrent = i == currentIndex;
            Steps[i].IsDone = state == AccelCalState.Succeeded || (currentIndex >= 0 && i < currentIndex);
        }

        AccelCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The large instruction for a position the vehicle asks for.</summary>
    public static string PositionText(POS pos) => pos switch
    {
        POS.LEVEL => "Place vehicle LEVEL",
        POS.LEFT => "Place vehicle on its LEFT side",
        POS.RIGHT => "Place vehicle on its RIGHT side",
        POS.NOSEDOWN => "Place vehicle nose DOWN",
        POS.NOSEUP => "Place vehicle nose UP",
        POS.BACK => "Place vehicle on its BACK",
        _ => "Please place vehicle " + pos,
    };

    private void OnLinkState(LinkState state)
    {
        if (state == LinkState.Disconnected && Process.IsActive)
        {
            Process.Cancel();
            AccelStatus.Set(StatusKind.Error, "The link was lost during the calibration.");
        }

        RefreshCommands();
    }

    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(CanStartOther));
        AccelCommand.NotifyCanExecuteChanged();
        CalibrateLevelCommand.NotifyCanExecuteChanged();
        SimpleAccelCommand.NotifyCanExecuteChanged();
    }

    public override void OnShown()
    {
        RefreshCommands();
        if (!IsCalibrating && !IsBusy)
        {
            // like Mission Planner's Activate: buttons back to their idle text
            LevelButtonText = "Calibrate Level";
            SimpleButtonText = "Simple Accel Cal";
            if (IsFinished)
            {
                Process.Cancel();
                AccelStatus.Clear();
            }
        }
    }
}
