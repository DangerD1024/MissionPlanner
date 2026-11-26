using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Calibration;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.RadioMapping;

/// <summary>One line of the step list: done, current or still to come.</summary>
public sealed partial class RcStepRow(RcStepInfo info) : ObservableObject
{
    public RcStepInfo Info { get; } = info;

    public string Title => Info.Title;

    [ObservableProperty]
    private bool _isDone;

    [ObservableProperty]
    private bool _isCurrent;

    [ObservableProperty]
    private string _detail = "";
}

/// <summary>One control in the summary.</summary>
public sealed record RcMapSummaryRow(string Function, string Channel, string Direction, string Change);

/// <summary>
/// "Map channels…" on Radio Calibration: QGroundControl's guided sequence that finds which receiver
/// channel carries roll, pitch, throttle and yaw and which way each one moves, then writes RCMAP_*
/// and RCn_REVERSED after a summary. Optional: the normal Mission Planner calibration stays the
/// default and continues after this (min/max/trim capture).
/// </summary>
public sealed partial class RadioMappingViewModel : ObservableObject
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(RadioMappingViewModel));

    private readonly AppServices _services;
    private readonly Action _started;
    private readonly Action<bool> _finished;
    private readonly Func<TimeSpan> _clock;
    private readonly float[] _last = new float[RadioCalibrator.Channels];

    public RadioMappingViewModel(AppServices services, Action started, Action<bool> finished)
        : this(services, started, finished, null, null)
    {
    }

    /// <param name="started">The sequence began (raise the RC rate).</param>
    /// <param name="finished">The sequence ended: true to continue with the min/max/trim capture.</param>
    internal RadioMappingViewModel(AppServices services, Action started, Action<bool> finished, Func<TimeSpan>? clock, RadioChannelMapper? mapper)
    {
        _services = services;
        _started = started;
        _finished = finished;
        var sw = Stopwatch.StartNew();
        _clock = clock ?? (() => sw.Elapsed);
        Mapper = mapper ?? new RadioChannelMapper();
        StepRows = RadioChannelMapper.Steps.Where(s => s.Step != RcMapStep.Review).Select(s => new RcStepRow(s)).ToList();
    }

    internal RadioChannelMapper Mapper { get; }

    public StatusLine Status { get; } = new();

    public IReadOnlyList<RcStepRow> StepRows { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _isActive;

    [ObservableProperty]
    private string _stepTitle = "";

    [ObservableProperty]
    private string _instruction = "";

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    private string _detectText = "";

    /// <summary>0-100 while a stick is being held.</summary>
    [ObservableProperty]
    private double _holdProgress;

    [ObservableProperty]
    private bool _isCenterStep;

    [ObservableProperty]
    private bool _isReview;

    [ObservableProperty]
    private bool _canBack;

    [ObservableProperty]
    private IReadOnlyList<RcMapSummaryRow> _summary = [];

    [ObservableProperty]
    private string _summaryText = "";

    [ObservableProperty]
    private bool _isWriting;

    /// <summary>RCMAP was written: the new mapping applies after a reboot.</summary>
    [ObservableProperty]
    private bool _rebootRequired;

    /// <summary>Starts the sequence (asks first: transmitter on, propellers off).</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (!_services.Session.IsConnected)
            return;
        if (!await _services.Dialogs.ConfirmAsync("Map channels",
                "Find which channel carries roll, pitch, throttle and yaw by moving each stick when asked.\n\n" +
                "Switch the transmitter on with the receiver connected, and remove the propellers. The normal " +
                "radio calibration (minimum, maximum and trim) follows afterwards.", "Start"))
            return;

        Mapper.Restart();
        Status.Clear();
        Summary = [];
        IsActive = true;
        _started();
        Sync();
    }

    public bool CanStart() => !IsActive;

    /// <summary>The centre step: takes the centred stick positions.</summary>
    [RelayCommand]
    private void Next()
    {
        if (!IsActive || Mapper.Step != RcMapStep.CenterSticks)
            return;
        if (!Mapper.CaptureCenter(_last))
        {
            Status.Set(StatusKind.Error, "No signal from the receiver on channel 1: switch the transmitter on and check the receiver.");
            return;
        }

        Status.Clear();
        Sync();
    }

    [RelayCommand]
    private void Back()
    {
        if (!IsActive || !Mapper.Back())
            return;
        Status.Clear();
        Sync();
    }

    [RelayCommand]
    private void Cancel() => Stop("Channel mapping cancelled; nothing was written.", continueCalibration: false);

    /// <summary>Ends the sequence from outside (page left, link lost).</summary>
    public void Abandon(string why)
    {
        if (IsActive)
            Stop(why, continueCalibration: false);
    }

    private void Stop(string why, bool continueCalibration)
    {
        IsActive = false;
        IsReview = false;
        Status.Set(continueCalibration ? StatusKind.Success : StatusKind.Warning, why);
        _finished(continueCalibration);
    }

    /// <summary>One sample of the 16 inputs (index 0 = channel 1), from the page's tick.</summary>
    public void Sample(IReadOnlyList<float> channels)
    {
        for (var i = 0; i < _last.Length; i++)
            _last[i] = i < channels.Count ? channels[i] : 0;
        if (!IsActive || IsReview)
            return;

        var advanced = Mapper.Sample(_last, _clock());
        if (advanced)
        {
            Status.Clear();
            Sync();
            return;
        }

        UpdateDetect();
    }

    private void UpdateDetect()
    {
        var info = Mapper.Info;
        HoldProgress = Mapper.SettleProgress * 100;
        if (info.Function == null)
        {
            DetectText = "";
            return;
        }

        if (Mapper.Candidate is { } ch)
        {
            DetectText = string.Create(CultureInfo.InvariantCulture, $"Channel {ch} at {Mapper.CandidateValue:0} µs: hold it there…");
        }
        else if (info.IsDetect)
        {
            DetectText = "Waiting for a stick to move…";
        }
        else
        {
            var m = Mapper.Mappings[info.Function.Value];
            DetectText = string.Create(CultureInfo.InvariantCulture, $"Waiting for channel {m.Channel} to move to the other end…");
        }
    }

    private void Sync()
    {
        var info = Mapper.Info;
        StepTitle = info.Title;
        Instruction = info.Instruction;
        IsCenterStep = info.Step == RcMapStep.CenterSticks;
        IsReview = Mapper.IsComplete;
        CanBack = Mapper.StepIndex > 0;
        var total = StepRows.Count;
        ProgressText = IsReview ? "Result" : $"Step {Mapper.StepIndex + 1} of {total}";

        for (var i = 0; i < StepRows.Count; i++)
        {
            var row = StepRows[i];
            row.IsDone = i < Mapper.StepIndex;
            row.IsCurrent = i == Mapper.StepIndex;
            row.Detail = row.IsDone && row.Info.Function is { } f && Mapper.Mappings.TryGetValue(f, out var m)
                ? row.Info.IsDetect ? $"channel {m.Channel}" : "confirmed"
                : "";
        }

        if (IsReview)
            BuildSummary();
        UpdateDetect();
    }

    private void BuildSummary()
    {
        var result = Mapper.Result();
        var param = (Func<string, double?>)_services.Params.Get;
        var current = RadioMap.From(param);
        var writes = result.Writes(param);
        var names = writes.Select(w => w.Key).ToHashSet();

        RcMapSummaryRow Row(string label, RcChannelMapping m, int was, string rcmap)
        {
            var n = m.Channel.ToString(CultureInfo.InvariantCulture);
            var revName = names.Contains("RC" + n + "_REVERSED") ? "RC" + n + "_REVERSED" : names.Contains("RC" + n + "_REV") ? "RC" + n + "_REV" : null;
            var changes = new List<string>();
            if (names.Contains(rcmap))
                changes.Add(string.Create(CultureInfo.InvariantCulture, $"{rcmap} {was} → {m.Channel}"));
            if (revName != null)
                changes.Add(revName + (m.Reversed ? " → reversed" : " → normal"));
            return new RcMapSummaryRow(label, "Channel " + n, m.Reversed ? "Reversed" : "Normal",
                changes.Count == 0 ? "unchanged" : string.Join(", ", changes));
        }

        Summary =
        [
            Row("Roll", result.Roll, current.Roll, "RCMAP_ROLL"),
            Row("Pitch", result.Pitch, current.Pitch, "RCMAP_PITCH"),
            Row("Throttle", result.Throttle, current.Throttle, "RCMAP_THROTTLE"),
            Row("Yaw", result.Yaw, current.Yaw, "RCMAP_YAW"),
        ];
        SummaryText = writes.Count == 0
            ? "The vehicle already uses these channels and directions: nothing needs writing."
            : $"{writes.Count} parameter{(writes.Count == 1 ? "" : "s")} will change. RCMAP changes take effect after a reboot.";
    }

    /// <summary>Writes the result (after confirming) and goes on to the min/max/trim capture.</summary>
    [RelayCommand]
    private async Task WriteAsync()
    {
        if (!IsActive || !Mapper.IsComplete || IsWriting)
            return;
        var result = Mapper.Result();
        var writes = result.Writes(_services.Params.Get);
        if (writes.Count == 0)
        {
            Stop("Channel mapping unchanged. Now move every stick and switch to its extremes.", continueCalibration: true);
            return;
        }

        var lines = string.Join("\n", writes.Select(w => string.Create(CultureInfo.InvariantCulture, $"{w.Key} = {w.Value}")));
        if (!await _services.Dialogs.ConfirmAsync("Map channels",
                "Write the channel mapping?\n\n" + lines + "\n\nRCMAP changes take effect after the autopilot reboots. " +
                "The normal radio calibration starts next.", "Write"))
            return;

        IsWriting = true;
        var failed = new List<string>();
        var reboot = false;
        try
        {
            foreach (var w in writes)
            {
                if (await _services.Params.SetAsync(w.Key, w.Value))
                    reboot |= w.Key.StartsWith("RCMAP_", StringComparison.Ordinal) || _services.Params.Meta(w.Key)?.RebootRequired == true;
                else
                    failed.Add(w.Key);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("rc mapping write", ex);
            failed.Add(ex.Message);
        }
        finally
        {
            IsWriting = false;
        }

        RebootRequired |= reboot;
        if (failed.Count > 0)
        {
            Status.Set(StatusKind.Error, "Not written: " + string.Join(", ", failed) + ". Check the link and try again.");
            return;
        }

        Stop("Channel mapping written" + (reboot ? " (reboot to apply RCMAP)" : "") +
             ". Now move every stick and switch to its extremes.", continueCalibration: true);
    }

    [RelayCommand]
    private async Task RebootAsync()
    {
        if (!_services.Session.IsConnected)
            return;
        if (!await _services.Dialogs.ConfirmAsync("Reboot autopilot",
                "Restart the flight controller now? The link drops while it reboots.", "Reboot"))
            return;
        var mav = _services.Session.Mav;
        bool ok;
        try
        {
            ok = await Task.Run(() => Session.VehicleReboot.Send(mav));
        }
        catch (Exception ex)
        {
            Log.Warn("reboot", ex);
            ok = false;
        }

        if (ok)
            RebootRequired = false;
        else
            await _services.Dialogs.ShowMessageAsync("Reboot", "The autopilot did not confirm the reboot command.", MessageKind.Warning);
    }
}
