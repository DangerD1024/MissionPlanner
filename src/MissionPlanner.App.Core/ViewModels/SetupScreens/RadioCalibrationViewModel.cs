using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.Session;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Calibration;
using MissionPlanner.ArduPilot;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>One RC input bar: the live PWM and, while calibrating, the captured min/max.</summary>
public sealed partial class RcChannelViewModel(int number, string label) : ObservableObject
{
    public int Number { get; } = number;

    [ObservableProperty]
    private string _label = label;

    [ObservableProperty]
    private double _value;

    [ObservableProperty]
    private double _minLine;

    [ObservableProperty]
    private double _maxLine;

    /// <summary>Draws the min/max markers (during and after a calibration).</summary>
    [ObservableProperty]
    private bool _showRange;

    /// <summary>The channel's output is reversed (RCn_REVERSED): the bar fills from the other end.</summary>
    [ObservableProperty]
    private bool _reversed;

    public string ValueText => Value.ToString("0", CultureInfo.InvariantCulture);

    partial void OnValueChanged(double value) => OnPropertyChanged(nameof(ValueText));
}

/// <summary>A row of the result table after a calibration.</summary>
public sealed record RadioResultRow(int Channel, string Min, string Max, string Trim, string Outcome, bool Written, bool Failed);

public enum RadioCalStage
{
    Idle,
    Capturing,
    Saving,
    Completed,
}

/// <summary>
/// Radio Calibration: Mission Planner's ConfigRadioInput. Shows the 16 RC inputs and the four
/// primary controls mapped by RCMAP_*; "Calibrate Radio" captures each channel's extremes
/// (<see cref="RadioCalibrator"/>), takes the trims from centred sticks and writes RCn_MIN/MAX/TRIM
/// for the valid channels after showing what it found. RC_CHANNELS is streamed at 10 Hz while the
/// page is on screen.
/// </summary>
public sealed partial class RadioCalibrationViewModel : PageViewModel
{
    private const int FastRcRate = 10;

    private static readonly ILog Log = LogManager.GetLogger(typeof(RadioCalibrationViewModel));

    private readonly RadioCalibrator _cal = new();
    private readonly float[] _live = new float[RadioCalibrator.Channels];
    private bool _rateRaised;
    private bool _visible;
    private int _savedRcRate;
    private (int Att, int Pos, int Status)? _savedCalRates;

    public RadioCalibrationViewModel(AppServices services)
        : base("Radio Calibration")
    {
        Services = services;
        for (var i = 1; i <= RadioCalibrator.Channels; i++)
            Channels.Add(new RcChannelViewModel(i, "Radio " + i));
        Roll = new RcChannelViewModel(1, "Roll");
        Pitch = new RcChannelViewModel(2, "Pitch");
        Throttle = new RcChannelViewModel(3, "Throttle");
        Yaw = new RcChannelViewModel(4, "Yaw");

        ReverseRoll = ReverseToggle(1);
        ReversePitch = ReverseToggle(2);
        ReverseThrottle = ReverseToggle(3);
        ReverseYaw = ReverseToggle(4);

        Elevons =
        [
            new(services, Status, "Elevons", 1, 0, "ELEVON_MIXING"),
            new(services, Status, "Elevons Rev", 1, 0, "ELEVON_REVERSE"),
            new(services, Status, "Elevons CH1 Rev", 1, 0, "ELEVON_CH1_REV"),
            new(services, Status, "Elevons CH2 Rev", 1, 0, "ELEVON_CH2_REV"),
        ];

        services.Params.ParamsChanged += (_, _) => Setup();
        services.Session.StateChanged += (_, state) => services.Dispatcher.Post(() => OnLinkState(state));
    }

    public AppServices Services { get; }

    public ObservableCollection<RcChannelViewModel> Channels { get; } = new();

    public RcChannelViewModel Roll { get; }

    public RcChannelViewModel Pitch { get; }

    public RcChannelViewModel Throttle { get; }

    public RcChannelViewModel Yaw { get; }

    public ParamToggleViewModel ReverseRoll { get; private set; }

    public ParamToggleViewModel ReversePitch { get; private set; }

    public ParamToggleViewModel ReverseThrottle { get; private set; }

    public ParamToggleViewModel ReverseYaw { get; private set; }

    public IReadOnlyList<ParamToggleViewModel> Elevons { get; }

    public StatusLine Status { get; } = new();

    public StatusLine BindStatus { get; } = new();

    public ObservableCollection<RadioResultRow> Results { get; } = new();

    public RadioMap Map { get; private set; } = RadioMap.Default;

    /// <summary>Copter mixes in software: reversing an input there is done on the transmitter, so the boxes are hidden.</summary>
    [ObservableProperty]
    private bool _showReverse;

    [ObservableProperty]
    private bool _showElevons;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCapturing), nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(CalibrateCommand))]
    private RadioCalStage _stage;

    [ObservableProperty]
    private string _calibrateButtonText = "Calibrate Radio";

    [ObservableProperty]
    private string _instruction = "";

    [ObservableProperty]
    private bool _hasResults;

    public bool IsCapturing => Stage == RadioCalStage.Capturing;

    public bool IsIdle => Stage is RadioCalStage.Idle or RadioCalStage.Completed;

    public bool IsConnected => Services.Session.IsConnected;

    private ParamToggleViewModel ReverseToggle(int channel) =>
        new(Services, Status, "Reverse",
            [($"RC{channel}_REV", -1, 1), ($"RC{channel}_REVERSED", 1, 0)]);

    /// <summary>Reads RCMAP and the reverse parameters (UI thread).</summary>
    private void Setup()
    {
        Map = RadioMap.From(Services.Params.Get);
        Roll.Label = $"Roll (rc{Map.Roll})";
        Pitch.Label = $"Pitch (rc{Map.Pitch})";
        Throttle.Label = $"Throttle (rc{Map.Throttle})";
        Yaw.Label = $"Yaw (rc{Map.Yaw})";
        OnPropertyChanged(nameof(Map));

        // the reverse boxes follow the mapped channels
        ReverseRoll = ReverseToggle(Map.Roll);
        ReversePitch = ReverseToggle(Map.Pitch);
        ReverseThrottle = ReverseToggle(Map.Throttle);
        ReverseYaw = ReverseToggle(Map.Yaw);
        foreach (var t in new[] { ReverseRoll, ReversePitch, ReverseThrottle, ReverseYaw })
        {
            t.Refresh();
            t.Written += (_, _) => _ = DisableDipSwitchesAsync();
        }

        OnPropertyChanged(nameof(ReverseRoll));
        OnPropertyChanged(nameof(ReversePitch));
        OnPropertyChanged(nameof(ReverseThrottle));
        OnPropertyChanged(nameof(ReverseYaw));

        var fw = Services.Session.Cs.firmware;
        ShowReverse = fw != Firmwares.ArduCopter2;
        foreach (var e in Elevons)
            e.Refresh();
        ShowElevons = (fw == Firmwares.ArduPlane || fw == Firmwares.Ateryx) && Elevons.Any(e => e.IsAvailable);
    }

    // APM1-era boards had DIP switches overriding the reverse settings
    private async Task DisableDipSwitchesAsync()
    {
        if (Services.Params.Get("SWITCH_ENABLE") != 1)
            return;
        if (await Services.Params.SetAsync("SWITCH_ENABLE", 0))
            Status.Set(StatusKind.Info, "Disabled Dip Switches");
        else
            Status.Set(StatusKind.Error, "Error Disabling Dip Switch");
    }

    // ---------------------------------------------------------------- live bars

    public override void Tick()
    {
        var cs = Services.Session.Cs;
        ReadChannels(cs, _live);

        if (Stage == RadioCalStage.Capturing)
            _cal.Capture(_live);

        for (var i = 0; i < Channels.Count; i++)
        {
            var c = Channels[i];
            c.Value = _live[i];
            if (c.ShowRange)
            {
                c.MinLine = _cal.Min[i];
                c.MaxLine = _cal.Max[i];
            }
        }

        Mirror(Roll, Map.Roll, ReverseRoll);
        Mirror(Pitch, Map.Pitch, ReversePitch);
        Mirror(Throttle, Map.Throttle, ReverseThrottle);
        Mirror(Yaw, Map.Yaw, ReverseYaw);
    }

    private void Mirror(RcChannelViewModel bar, int channel, ParamToggleViewModel reverse)
    {
        var src = Channels[channel - 1];
        bar.Value = src.Value;
        bar.ShowRange = src.ShowRange;
        bar.MinLine = src.MinLine;
        bar.MaxLine = src.MaxLine;
        bar.Reversed = reverse.IsOn;
    }

    public static void ReadChannels(CurrentState cs, float[] into)
    {
        into[0] = cs.ch1in;
        into[1] = cs.ch2in;
        into[2] = cs.ch3in;
        into[3] = cs.ch4in;
        into[4] = cs.ch5in;
        into[5] = cs.ch6in;
        into[6] = cs.ch7in;
        into[7] = cs.ch8in;
        into[8] = cs.ch9in;
        into[9] = cs.ch10in;
        into[10] = cs.ch11in;
        into[11] = cs.ch12in;
        into[12] = cs.ch13in;
        into[13] = cs.ch14in;
        into[14] = cs.ch15in;
        into[15] = cs.ch16in;
    }

    // ---------------------------------------------------------------- calibration

    /// <summary>"Calibrate Radio" starts capturing; "Click when Done" ends it and saves.</summary>
    [RelayCommand(CanExecute = nameof(CanCalibrate))]
    private async Task CalibrateAsync()
    {
        if (Stage == RadioCalStage.Capturing)
        {
            await FinishCaptureAsync();
            return;
        }

        if (!await Services.Dialogs.ConfirmAsync("Radio Calibration",
                "Ensure your transmitter is on and receiver is powered and connected\n" +
                "Ensure your motor does not have power/no props!!!", "Continue"))
            return;

        RaiseRatesForCalibration();
        _cal.Reset();
        Results.Clear();
        HasResults = false;
        Status.Clear();
        foreach (var c in Channels)
        {
            c.ShowRange = true;
            c.MinLine = 0;
            c.MaxLine = 0;
        }

        Stage = RadioCalStage.Capturing;
        CalibrateButtonText = "Click when Done";
        Instruction = "Move all RC sticks and switches to their extreme positions so the red bars hit the limits.";
    }

    private bool CanCalibrate() => Stage is not RadioCalStage.Saving && IsConnected;

    [RelayCommand]
    private void CancelCalibration()
    {
        if (Stage != RadioCalStage.Capturing)
            return;
        AbandonCapture("Calibration cancelled, nothing was written.");
    }

    private void AbandonCapture(string why)
    {
        Stage = RadioCalStage.Idle;
        CalibrateButtonText = "Calibrate Radio";
        Instruction = "";
        foreach (var c in Channels)
            c.ShowRange = false;
        RestoreCalibrationRates();
        Status.Set(StatusKind.Warning, why);
    }

    private async Task FinishCaptureAsync()
    {
        Stage = RadioCalStage.Saving;
        CalibrateButtonText = "Saving…";
        Instruction = "";

        if (!_cal.Channel1Valid)
        {
            Stage = RadioCalStage.Idle;
            CalibrateButtonText = "Calibrate Radio";
            RestoreCalibrationRates();
            Status.Set(StatusKind.Error, "Bad channel 1 input, canceling");
            return;
        }

        if (!await Services.Dialogs.ConfirmAsync("Radio Calibration",
                "Ensure all your sticks are centered and throttle is down, and click OK to continue", "OK"))
        {
            AbandonCapture("Calibration cancelled, nothing was written.");
            return;
        }

        ReadChannels(Services.Session.Cs, _live);
        _cal.SetTrims(_live);
        var results = _cal.Results();
        var toWrite = _cal.ParamsToWrite();

        if (toWrite.Count == 0)
        {
            AbandonCapture("No channel has a usable range; nothing was written.");
            return;
        }

        var summary =
            "Here are the detected radio options\nNOTE Channels not connected are displayed as 1500 +-2\n" +
            "Normal values are around 1100 | 1900\nChannel:Min | Max \n---------------\n" + _cal.Summary() +
            "\n\nWrite RCn_MIN, RCn_MAX and RCn_TRIM for these channels?";
        if (!await Services.Dialogs.ConfirmAsync("Radio", summary, "Write"))
        {
            AbandonCapture("Calibration not saved.");
            ShowResults(results, new HashSet<int>(), written: false);
            return;
        }

        var failed = await WriteAsync(results.Where(r => r.Valid).ToList());
        RestoreCalibrationRates();
        ShowResults(results, failed, written: true);

        Stage = RadioCalStage.Completed;
        CalibrateButtonText = "Completed";
        var count = results.Count(r => r.Valid);
        if (failed.Count == 0)
            Status.Set(StatusKind.Success, $"Radio calibration saved for {count} channel{(count == 1 ? "" : "s")}.");
        else
            Status.Set(StatusKind.Error, "Failed to set Channel " + string.Join(", ", failed.Order()));
    }

    /// <summary>Writes each valid channel (forced, so the vehicle confirms every value). Returns the channels that failed.</summary>
    private async Task<HashSet<int>> WriteAsync(IReadOnlyList<RadioChannelCal> channels)
    {
        var session = Services.Session;
        var v = session.Vehicle;
        return await Task.Run(() =>
        {
            var failed = new HashSet<int>();
            foreach (var r in channels)
            {
                var n = r.Channel.ToString(CultureInfo.InvariantCulture);
                try
                {
                    var ok = session.Mav.setParam(v.sysid, v.compid, "RC" + n + "_MIN", RadioCalibrator.Pwm(r.Min), true);
                    ok &= session.Mav.setParam(v.sysid, v.compid, "RC" + n + "_MAX", RadioCalibrator.Pwm(r.Max), true);
                    ok &= session.Mav.setParam(v.sysid, v.compid, "RC" + n + "_TRIM", RadioCalibrator.Pwm(r.Trim), true);
                    if (!ok && v.param.ContainsKey("RC" + n + "_MIN"))
                        failed.Add(r.Channel);
                }
                catch (Exception ex)
                {
                    Log.Warn("radio cal ch" + n, ex);
                    if (v.param.ContainsKey("RC" + n + "_MIN"))
                        failed.Add(r.Channel);
                }
            }

            return failed;
        });
    }

    private void ShowResults(IReadOnlyList<RadioChannelCal> results, HashSet<int> failed, bool written)
    {
        Results.Clear();
        foreach (var r in results)
        {
            static string F(float v) => v.ToString("0", CultureInfo.InvariantCulture);
            var outcome = !r.Valid ? "not connected"
                : !written ? "not written"
                : failed.Contains(r.Channel) ? "failed"
                : "written";
            Results.Add(r.Valid
                ? new RadioResultRow(r.Channel, F(r.Min), F(r.Max), F(r.Trim), outcome, written && !failed.Contains(r.Channel), failed.Contains(r.Channel))
                : new RadioResultRow(r.Channel, "—", "—", "—", outcome, false, false));
        }

        HasResults = Results.Count > 0;
    }

    // ---------------------------------------------------------------- stream rates

    /// <summary>RC_CHANNELS at 10 Hz while the page is on screen (cs.raterc keeps it there when streams are re-requested).</summary>
    private void RaisePageRate()
    {
        if (_rateRaised || !IsConnected)
            return;
        var cs = Services.Session.Cs;
        _savedRcRate = cs.raterc;
        cs.raterc = FastRcRate;
        _rateRaised = true;
        RequestRc(FastRcRate);
    }

    private void RestorePageRate()
    {
        if (!_rateRaised)
            return;
        _rateRaised = false;
        Services.Session.Cs.raterc = _savedRcRate;
        if (IsConnected)
            RequestRc(_savedRcRate);
    }

    /// <summary>As Mission Planner while calibrating: other streams off, RC at 10 Hz.</summary>
    private void RaiseRatesForCalibration()
    {
        RaisePageRate();
        var cs = Services.Session.Cs;
        _savedCalRates ??= (cs.rateattitude, cs.rateposition, cs.ratestatus);
        cs.rateattitude = 0;
        cs.rateposition = 0;
        cs.ratestatus = 0;
        cs.raterc = FastRcRate;
        RequestRc(FastRcRate);
    }

    private void RestoreCalibrationRates()
    {
        if (_savedCalRates is not { } r)
            return;
        _savedCalRates = null;
        var cs = Services.Session.Cs;
        cs.rateattitude = r.Att;
        cs.rateposition = r.Pos;
        cs.ratestatus = r.Status;
    }

    private void RequestRc(int rate)
    {
        var session = Services.Session;
        var v = session.Vehicle;
        _ = Task.Run(() =>
        {
            try
            {
                session.Mav.requestDatastream(MAVLink.MAV_DATA_STREAM.RC_CHANNELS, rate, v.sysid, v.compid);
            }
            catch (Exception ex)
            {
                Log.Warn("rc rate", ex);
            }
        });
    }

    // ---------------------------------------------------------------- spektrum bind

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private Task BindDsm2Async() => BindAsync(0);

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private Task BindDsmxAsync() => BindAsync(1);

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private Task BindDsm8Async() => BindAsync(2);

    private async Task BindAsync(float type)
    {
        var session = Services.Session;
        var v = session.Vehicle;
        try
        {
            await Task.Run(() => session.Mav.doCommand(v.sysid, v.compid, MAVLink.MAV_CMD.START_RX_PAIR, 0, type, 0, 0, 0, 0, 0));
            BindStatus.Set(StatusKind.Info, "Put the transmitter in bind mode. Receiver is waiting");
        }
        catch (Exception ex)
        {
            Log.Warn("bind", ex);
            BindStatus.Set(StatusKind.Error, "Error binding");
        }
    }

    // ---------------------------------------------------------------- page

    private void OnLinkState(LinkState state)
    {
        if (state == LinkState.Disconnected)
        {
            if (Stage == RadioCalStage.Capturing)
                AbandonCapture("The link was lost; nothing was written.");
            _rateRaised = false;
            _savedCalRates = null;
        }
        else if (state == LinkState.Connected && _visible)
        {
            RaisePageRate();
        }

        OnPropertyChanged(nameof(IsConnected));
        CalibrateCommand.NotifyCanExecuteChanged();
        BindDsm2Command.NotifyCanExecuteChanged();
        BindDsmxCommand.NotifyCanExecuteChanged();
        BindDsm8Command.NotifyCanExecuteChanged();
    }

    public override void OnShown()
    {
        _visible = true;
        Setup();
        OnLinkState(Services.Session.State);
        RaisePageRate();
        if (Stage == RadioCalStage.Completed)
        {
            // like Mission Planner's Activate: the button starts over
            Stage = RadioCalStage.Idle;
            CalibrateButtonText = "Calibrate Radio";
        }
    }

    public override void OnHidden()
    {
        _visible = false;
        // capturing needs the page's ticks
        if (Stage == RadioCalStage.Capturing)
            AbandonCapture("Calibration abandoned when the page was left; nothing was written.");
        RestoreCalibrationRates();
        RestorePageRate();
    }
}
