using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Calibration;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;
using MissionPlanner.ArduPilot;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.MotorSetup;

public enum MotorSetupMode
{
    Overview,
    Order,
    Direction,
}

/// <summary>A frame type the selector offers, with the layout it previews.</summary>
public sealed record FrameTypeChoice(int Value, string Label, MotorLayout? Layout)
{
    public override string ToString() => Label;
}

/// <summary>One output in the motor order result.</summary>
public sealed record MappingRow(int Output, int OldMotor, int NewMotor)
{
    public bool Changed => OldMotor != NewMotor;

    public string OutputText => "Output " + Output.ToString(CultureInfo.InvariantCulture);

    public string OldText => "Motor " + OldMotor.ToString(CultureInfo.InvariantCulture);

    public string NewText => "Motor " + NewMotor.ToString(CultureInfo.InvariantCulture);

    public string ChangeText => Changed ? "changes" : "unchanged";
}

/// <summary>One motor in direction mode: its outputs, expected rotation, the reverse bit and a spin button.</summary>
public sealed partial class DirectionRow : ObservableObject
{
    private readonly Func<DirectionRow, bool, Task> _toggle;
    private bool _syncing;

    public DirectionRow(MotorInfo motor, IReadOnlyList<int> outputs, Func<DirectionRow, Task> spin, Func<DirectionRow, bool, Task> toggle)
    {
        Motor = motor;
        Outputs = outputs;
        _toggle = toggle;
        SpinCommand = new AsyncRelayCommand(() => spin(this));
    }

    public MotorInfo Motor { get; }

    public IReadOnlyList<int> Outputs { get; }

    public string Title => "Motor " + Motor.Number.ToString(CultureInfo.InvariantCulture) + " (" + Motor.TestLetter + ")";

    public string OutputsText => Outputs.Count == 0 ? "no output" : "Output " + MotorOutputs.Join(Outputs);

    public string ExpectedText => Motor.IsClockwise ? "Clockwise" : Motor.IsCounterClockwise ? "Counter-clockwise" : "Not given by the frame";

    public IAsyncRelayCommand SpinCommand { get; }

    [ObservableProperty]
    private bool _isReversed;

    [ObservableProperty]
    private bool _canReverse;

    [ObservableProperty]
    private bool _isSpinning;

    [ObservableProperty]
    private string _rpmText = "";

    /// <summary>Sets the box from the vehicle's mask without writing.</summary>
    public void Show(bool reversed)
    {
        _syncing = true;
        IsReversed = reversed;
        _syncing = false;
    }

    partial void OnIsReversedChanged(bool value)
    {
        if (!_syncing)
            _ = _toggle(this, value);
    }
}

/// <summary>
/// The "Motor layout" part of Servo Output for multirotors and QuadPlanes, Betaflight style: the
/// frame drawn from APMotorLayout.json with motor numbers, test letters, rotation and outputs; a
/// frame-type picker and the "motor direction is reversed" switch (the mirror FRAME_TYPE); the
/// motor order wizard (spin each output, click where it is, write SERVOn_FUNCTION) and the motor
/// direction mode (spin a motor, reverse it through SERVO_BLH_RVMASK on DShot ESCs).
/// </summary>
public sealed partial class MotorSetupViewModel : ObservableObject
{
    public const string SafetySwitchNote =
        "If the flight controller has a safety switch, press it until it shows solid (or set BRD_SAFETY_DEFLT = 0): " +
        "ArduPilot refuses motor tests while the safety switch is on.";

    public const string MotorOrderUrl = "https://ardupilot.org/copter/docs/connect-escs-and-motors.html#motor-order-diagrams";

    private readonly AppServices _services;
    private readonly IParamSource _source;
    private readonly Func<string, double, Task<bool>> _write;
    private readonly Action _markReboot;
    private readonly MotorLayoutCatalog _layouts;
    private readonly MotorSpinner _spinner;
    private readonly Func<bool> _isMultirotor;
    private readonly Func<bool> _connected;
    private readonly Func<bool> _armed;
    private IReadOnlyList<MotorOutput> _outputs = [];
    private MotorOrderWizard? _wizard;
    private IReadOnlyList<MotorOutput>? _wizardOutputs;
    private string? _classParam;
    private string? _typeParam;
    private int _frameClass;
    private int _frameType;
    private int _spinningMotor;
    private bool _syncing;
    private bool _leaving;
    private MotorLayout? _preview;
    private string? _previewText;
    private string _lastRpm = "";

    public MotorSetupViewModel(AppServices services, IParamSource source, Func<string, double, Task<bool>> write, Action markReboot)
        : this(services, source, write, markReboot, null, null, null)
    {
    }

    /// <summary>Tests replace the link, the layouts and the vehicle state probes.</summary>
    internal MotorSetupViewModel(AppServices services, IParamSource source, Func<string, double, Task<bool>> write, Action markReboot,
        ICalibrationLink? link, MotorLayoutCatalog? layouts, Func<bool>? isMultirotor, TimeSpan? keepAlive = null,
        Func<bool>? connected = null, Func<bool>? armed = null)
    {
        _connected = connected ?? (() => services.Session.IsConnected);
        _armed = armed ?? (() => services.Session.Cs.armed);
        _services = services;
        _source = source;
        _write = write;
        _markReboot = markReboot;
        _layouts = layouts ?? MotorLayoutCatalog.Default;
        _isMultirotor = isMultirotor ?? (() => services.Session.Cs.firmware == Firmwares.ArduCopter2);
        _spinner = new MotorSpinner(link ?? new VehicleCalibrationLink(services.Session), keepAlive, keepAlive == null ? null : TimeSpan.Zero);
        _spinner.Failed += (_, message) => _services.Dispatcher.Post(() => OnSpinFailed(message));
    }

    public StatusLine Status { get; } = new();

    public string SafetyText => SafetySwitchNote;

    public string OrderUrl => MotorOrderUrl;

    // ------------------------------------------------------------------ frame

    /// <summary>A multirotor or QuadPlane frame with a known layout: the card is shown.</summary>
    [ObservableProperty]
    private bool _isAvailable;

    [ObservableProperty]
    private MotorLayout? _layout;

    [ObservableProperty]
    private string _frameText = "";

    [ObservableProperty]
    private MotorDiagramState _diagram = MotorDiagramState.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverview), nameof(IsOrder), nameof(IsDirection), nameof(ShowSafetyGate), nameof(CanEditFrame))]
    private MotorSetupMode _mode;

    public bool IsOverview => Mode == MotorSetupMode.Overview;

    public bool IsOrder => Mode == MotorSetupMode.Order;

    public bool IsDirection => Mode == MotorSetupMode.Direction;

    /// <summary>The propellers / disarmed confirmation, before anything spins.</summary>
    public bool ShowSafetyGate => Mode != MotorSetupMode.Overview && !SafetyAccepted && (Mode != MotorSetupMode.Order || CanRunWizard);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSafetyGate), nameof(IsWizardRunning), nameof(IsDirectionRunning))]
    private bool _safetyAccepted;

    [ObservableProperty]
    private bool _propsRemoved;

    /// <summary>Test throttle in percent (1-20).</summary>
    [ObservableProperty]
    private decimal _throttle = 6;

    [ObservableProperty]
    private bool _isSpinning;

    [ObservableProperty]
    private string _spinText = "";

    [ObservableProperty]
    private bool _isBusy;

    public bool CanEditFrame => Mode == MotorSetupMode.Overview && !IsBusy;

    /// <summary>The reversed switch works only on the overview, for frames that have a mirror type.</summary>
    public bool CanSwitchReversed => CanReverseFrame && CanEditFrame;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditFrame));
        OnPropertyChanged(nameof(CanSwitchReversed));
    }

    partial void OnCanReverseFrameChanged(bool value) => OnPropertyChanged(nameof(CanSwitchReversed));

    /// <summary>The frame has coaxial motors (drawn side by side).</summary>
    public bool HasCoaxial => Layout != null && Layout.Motors.GroupBy(m => (Math.Round(m.X, 2), Math.Round(m.Y, 2))).Any(g => g.Count() > 1);

    partial void OnLayoutChanged(MotorLayout? value) => OnPropertyChanged(nameof(HasCoaxial));

    // frame type picker

    [ObservableProperty]
    private IReadOnlyList<FrameTypeChoice> _frameTypes = [];

    [ObservableProperty]
    private bool _hasFrameTypes;

    [ObservableProperty]
    private FrameTypeChoice? _selectedFrameType;

    [ObservableProperty]
    private bool _isPreviewing;

    [ObservableProperty]
    private string _frameTypeParamText = "";

    // reversed switch

    [ObservableProperty]
    private bool _canReverseFrame;

    [ObservableProperty]
    private bool _frameReversed;

    [ObservableProperty]
    private string _reverseFrameNote = "";

    // ------------------------------------------------------------------ order wizard

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSafetyGate))]
    private bool _canRunWizard;

    [ObservableProperty]
    private IReadOnlyList<string> _orderProblems = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdentifying), nameof(IsReviewing), nameof(IsOrderDone), nameof(IsWizardRunning))]
    private MotorOrderStage _orderStage = MotorOrderStage.Safety;

    public bool IsIdentifying => OrderStage == MotorOrderStage.Identify;

    public bool IsReviewing => OrderStage == MotorOrderStage.Review;

    public bool IsOrderDone => OrderStage == MotorOrderStage.Done;

    public bool IsWizardRunning => IsOrder && SafetyAccepted && OrderStage is MotorOrderStage.Identify or MotorOrderStage.Review;

    [ObservableProperty]
    private string _stepText = "";

    [ObservableProperty]
    private string _orderInstruction = "";

    [ObservableProperty]
    private string _outputLiveText = "";

    [ObservableProperty]
    private IReadOnlyList<MappingRow> _reviewRows = [];

    [ObservableProperty]
    private string _reviewText = "";

    [ObservableProperty]
    private bool _canWriteMapping;

    // ------------------------------------------------------------------ direction

    public bool IsDirectionRunning => IsDirection && SafetyAccepted;

    [ObservableProperty]
    private IReadOnlyList<DirectionRow> _directionRows = [];

    [ObservableProperty]
    private MotorDirectionStatus? _direction;

    [ObservableProperty]
    private string _directionMessage = "";

    [ObservableProperty]
    private bool _directionSupported;

    [ObservableProperty]
    private bool _offerEscType;

    [ObservableProperty]
    private string? _threeDWarning;

    [ObservableProperty]
    private string _rpmNote = "";

    public IReadOnlyList<FrameTypeChoice> EscTypeChoices { get; } =
        MotorDirection.EscTypes.Select(t => new FrameTypeChoice(t.Value, t.Label, null)).ToList();

    [ObservableProperty]
    private FrameTypeChoice? _selectedEscType;

    // ================================================================== building

    /// <summary>Reads the frame and outputs from the parameters (the page's Build).</summary>
    public void Build()
    {
        var plan = MotorTestLogic.Resolve(_source.Get, _services.Session.Vehicle.aptype, _layouts);
        _classParam = plan.ClassParam;
        _typeParam = plan.TypeParam;
        var quadPlane = _classParam?.StartsWith("Q_", StringComparison.Ordinal) == true;
        _frameClass = _classParam != null && _source.Get(_classParam) is { } c ? (int)c : 0;
        _frameType = _typeParam != null && _source.Get(_typeParam) is { } t ? (int)t : 0;
        var layout = plan.Layout ?? (_classParam != null ? FrameCatalog.Preview(_frameClass, _frameType, _layouts) : null);

        IsAvailable = (quadPlane || (_classParam != null && _isMultirotor())) && layout is { Motors.Count: > 0 };
        Layout = IsAvailable ? layout : null;
        if (!IsAvailable)
        {
            _ = LeaveModeAsync(null);
            Diagram = MotorDiagramState.Empty;
            return;
        }

        FrameText = FrameCatalog.ClassName(_frameClass) + " " + TypeLabel(_frameType) +
                    $"  ({_classParam} = {_frameClass}, {_typeParam} = {_frameType})";
        FrameTypeParamText = _typeParam ?? "";
        BuildFrameTypes();
        Refresh();
    }

    private void BuildFrameTypes()
    {
        var types = _typeParam != null && _source.Has(_typeParam)
            ? FrameCatalog.ValidTypes(_frameClass, _layouts)
            : [];
        FrameTypes = types.Select(v => new FrameTypeChoice(v, TypeLabel(v), FrameCatalog.Preview(_frameClass, v, _layouts))).ToList();
        HasFrameTypes = FrameTypes.Count > 1;
        ShowSelectedType();

        var partner = FrameReversal.Partner(_layouts, _frameClass, _frameType);
        CanReverseFrame = partner != null && _typeParam != null;
        _syncing = true;
        FrameReversed = FrameReversal.IsReversedMember(_layouts, _frameClass, _frameType);
        _syncing = false;
        ReverseFrameNote = partner is { } p
            ? $"Switches {_typeParam} between {TypeLabel(Math.Min(_frameType, p))} and its mirror {TypeLabel(Math.Max(_frameType, p))}: " +
              "the same motors at the same positions, every one turning the other way."
            : $"ArduPilot has no frame type with every motor turning the other way for {FrameCatalog.ClassName(_frameClass)} {TypeLabel(_frameType)}. " +
              "The spin direction can only change together with a matching frame type, because the yaw mixing follows it; " +
              $"this frame would need a scripted matrix frame ({_classParam} = 15, Scripting Matrix).";
    }

    private void ShowSelectedType()
    {
        _syncing = true;
        SelectedFrameType = FrameTypes.FirstOrDefault(f => f.Value == _frameType);
        _syncing = false;
    }

    private string TypeLabel(int type)
    {
        var label = _typeParam != null ? _source.Meta(_typeParam)?.Values.FirstOrDefault(v => (int)v.Key == type).Value : null;
        return string.IsNullOrEmpty(label) ? FrameCatalog.TypeName(type) : label;
    }

    /// <summary>Re-reads outputs, masks and frame after values change (writes, background updates).</summary>
    public void Refresh()
    {
        if (!IsAvailable || Layout == null)
            return;

        // the frame changed behind the card (Frame Type page, parameter list): start over
        var cls = _classParam != null && _source.Get(_classParam) is { } c ? (int)c : 0;
        var typ = _typeParam != null && _source.Get(_typeParam) is { } t ? (int)t : 0;
        if (cls != _frameClass || typ != _frameType)
        {
            if (Mode != MotorSetupMode.Overview)
                _ = LeaveModeAsync("The frame type changed: motors stopped.");
            Build();
            return;
        }

        _outputs = MotorOutputs.Read(_source.Get);
        var check = MotorOutputs.Check(Layout, _outputs);
        CanRunWizard = check.CanRunWizard;
        var problems = check.Problems.Concat(check.Ignored.Select(o =>
                string.Create(CultureInfo.InvariantCulture, $"Output {o.Output} is Motor{o.Motor}, which this frame does not use; it is left as it is.")))
            .ToList();
        if (!problems.SequenceEqual(OrderProblems))
            OrderProblems = problems;

        // outputs edited in the table while the wizard runs: its picture of the vehicle is stale
        if (!IsBusy && _wizard != null && _wizardOutputs != null && OrderStage is MotorOrderStage.Identify or MotorOrderStage.Review
            && !_wizardOutputs.SequenceEqual(_outputs))
        {
            _ = AbortWizardAsync("The output functions changed while the wizard was running. Start it again.");
        }

        RefreshDirection();
        UpdateDiagram();
    }

    private bool IsQuadPlane => _classParam?.StartsWith("Q_", StringComparison.Ordinal) == true;

    private void RefreshDirection()
    {
        var status = MotorDirection.Evaluate(_source.Get, IsQuadPlane, _outputs.Select(o => o.Output).ToList());
        Direction = status;
        DirectionMessage = status.Message;
        DirectionSupported = status.CanReverse;
        OfferEscType = status.CanOfferEscType;
        ThreeDWarning = status.ThreeDWarning;
        RpmNote = status.HasRpmTelemetry
            ? "Bidirectional DShot is on (SERVO_BLH_BDMASK): the spinning motor's RPM is shown when the ESCs report it."
            : "RPM is shown if the ESCs report telemetry (bidirectional DShot, SERVO_BLH_BDMASK); it is not needed to reverse motors.";

        var layout = Layout!;
        if (DirectionRows.Count != layout.Motors.Count || DirectionRows.Zip(layout.Motors).Any(p => p.First.Motor != p.Second
                || !p.First.Outputs.SequenceEqual(MotorOutputs.OutputsOf(_outputs, p.Second.Number))))
        {
            DirectionRows = layout.Motors.OrderBy(m => m.Number)
                .Select(m => new DirectionRow(m, MotorOutputs.OutputsOf(_outputs, m.Number), SpinRowAsync, ToggleReverseAsync))
                .ToList();
        }

        foreach (var row in DirectionRows)
        {
            row.CanReverse = status.CanReverse && row.Outputs.Count > 0;
            row.Show(MotorDirection.IsReversed(status.ReverseMask, row.Outputs));
            row.IsSpinning = row.Motor.Number == _spinningMotor;
        }
    }

    // ================================================================== diagram

    private void UpdateDiagram()
    {
        if (Layout == null)
        {
            Diagram = MotorDiagramState.Empty;
            return;
        }

        if (_preview != null)
        {
            Diagram = new MotorDiagramState(_preview,
                _preview.Motors.ToDictionary(m => m.Number, m => new MotorMark(m.Number, null)), false, _previewText);
            return;
        }

        var marks = new Dictionary<int, MotorMark>();
        var clickable = false;
        string? banner = null;
        if (Mode == MotorSetupMode.Order && _wizard != null && OrderStage is MotorOrderStage.Identify or MotorOrderStage.Review or MotorOrderStage.Done)
        {
            clickable = OrderStage == MotorOrderStage.Identify && SafetyAccepted;
            foreach (var m in Layout.Motors)
            {
                var output = _wizard.OutputAt(m.Number);
                marks[m.Number] = output is { } o
                    ? new MotorMark(m.Number, "OUT " + o.ToString(CultureInfo.InvariantCulture), MotorMarkKind.Placed)
                    : new MotorMark(m.Number, null, clickable ? MotorMarkKind.Open : MotorMarkKind.Normal);
            }

            if (clickable && _wizard.Current is { } cur)
                banner = $"Output {cur.Output} is spinning: click its motor";
        }
        else
        {
            var mask = Direction?.ReverseMask ?? 0;
            var showRev = Mode == MotorSetupMode.Direction && Direction?.CanReverse == true;
            clickable = Mode == MotorSetupMode.Direction && SafetyAccepted;
            foreach (var m in Layout.Motors)
            {
                var outs = MotorOutputs.OutputsOf(_outputs, m.Number);
                var caption = outs.Count == 0 ? "no output" : "OUT " + string.Join(",", outs.Select(o => o.ToString(CultureInfo.InvariantCulture)));
                var spinning = m.Number == _spinningMotor;
                marks[m.Number] = new MotorMark(m.Number, caption, spinning ? MotorMarkKind.Spinning : MotorMarkKind.Normal,
                    showRev && MotorDirection.IsReversed(mask, outs), spinning && _lastRpm.Length > 0 ? _lastRpm : null);
            }

            if (clickable)
                banner = _spinningMotor > 0 ? $"Motor {_spinningMotor} is spinning" : "Click a motor to spin it";
        }

        Diagram = new MotorDiagramState(Layout, marks, clickable, banner);
    }

    /// <summary>A click on the drawing (motor number of the position).</summary>
    [RelayCommand]
    private Task MotorClickedAsync(int motor)
    {
        if (Mode == MotorSetupMode.Order)
            return PlaceAsync(motor);
        if (Mode == MotorSetupMode.Direction && SafetyAccepted)
        {
            var row = DirectionRows.FirstOrDefault(r => r.Motor.Number == motor);
            return row != null ? SpinRowAsync(row) : Task.CompletedTask;
        }

        return Task.CompletedTask;
    }

    // ================================================================== frame type and reversed switch

    partial void OnSelectedFrameTypeChanged(FrameTypeChoice? value)
    {
        if (_syncing || value == null)
            return;
        if (value.Value == _frameType)
        {
            ClearPreview();
            return;
        }

        _preview = value.Layout;
        _previewText = "Preview: " + FrameCatalog.ClassName(_frameClass) + " " + value.Label + " (not written)";
        IsPreviewing = true;
        UpdateDiagram();
    }

    private void ClearPreview()
    {
        _preview = null;
        _previewText = null;
        IsPreviewing = false;
        UpdateDiagram();
    }

    [RelayCommand]
    private void CancelPreview()
    {
        ClearPreview();
        ShowSelectedType();
    }

    [RelayCommand]
    private async Task ApplyFrameTypeAsync()
    {
        if (SelectedFrameType is not { } choice || choice.Value == _frameType || _typeParam == null)
        {
            CancelPreview();
            return;
        }

        var ok = await _services.Dialogs.ConfirmAsync("Frame type",
            $"Set {_typeParam} to {choice.Label} ({choice.Value})?\n\nThe motor mixing follows the frame type: check that the motors " +
            "sit where the drawing shows and turn the way its arrows show (propellers too).\n\n" + RebootSentence(_typeParam),
            "Set frame type");
        if (!ok)
        {
            CancelPreview();
            return;
        }

        await WriteFrameTypeAsync(choice.Value, choice.Label);
    }

    partial void OnFrameReversedChanged(bool value)
    {
        if (_syncing)
            return;
        _ = SwitchReversedAsync(value);
    }

    private async Task SwitchReversedAsync(bool reversed)
    {
        var partner = FrameReversal.Partner(_layouts, _frameClass, _frameType);
        if (partner is not { } p || _typeParam == null || IsBusy || !IsOverview)
        {
            RevertReversedSwitch();
            return;
        }

        var label = TypeLabel(p);
        _preview = _layouts.Find(_frameClass, p);
        _previewText = $"Preview: {FrameCatalog.ClassName(_frameClass)} {label}, every motor reversed (not written)";
        IsPreviewing = true;
        UpdateDiagram();

        var ok = await _services.Dialogs.ConfirmAsync("Motor direction is reversed",
            $"{(reversed ? "Reverse" : "Restore")} the direction of every motor by switching {_typeParam} from {TypeLabel(_frameType)} to {label}?\n\n" +
            "Before flying, all of these must match the new frame:\n" +
            "• swap every propeller for one of the opposite rotation,\n" +
            "• make every motor turn the other way: with DShot ESCs use the Motor direction mode (SERVO_BLH_RVMASK), otherwise swap two motor wires,\n" +
            "• check each motor again with the Motor direction mode after the reboot.\n\n" + RebootSentence(_typeParam),
            "Switch frame type");
        _preview = null;
        _previewText = null;
        IsPreviewing = false;
        if (!ok)
        {
            RevertReversedSwitch();
            UpdateDiagram();
            return;
        }

        await WriteFrameTypeAsync(p, label);
    }

    private void RevertReversedSwitch()
    {
        // after the toggle's own change has finished
        _services.Dispatcher.Post(() =>
        {
            _syncing = true;
            FrameReversed = FrameReversal.IsReversedMember(_layouts, _frameClass, _frameType);
            _syncing = false;
        });
    }

    private string RebootSentence(string param) =>
        _source.Meta(param) is { RebootRequired: false }
            ? param + " is written to the vehicle now."
            : param + " is written to the vehicle now and takes effect after the autopilot reboots.";

    private async Task WriteFrameTypeAsync(int type, string label)
    {
        if (_typeParam == null)
            return;
        IsBusy = true;
        try
        {
            if (await _write(_typeParam, type))
            {
                if (_source.Meta(_typeParam) is not { RebootRequired: false })
                    _markReboot();
                Status.Set(StatusKind.Success, $"{_typeParam} set to {label}. Reboot the autopilot, then check the motors again.");
            }
            else
            {
                Status.Set(StatusKind.Error, $"The vehicle did not accept {_typeParam} = {type}.");
            }
        }
        finally
        {
            IsBusy = false;
            _preview = null;
            _previewText = null;
            IsPreviewing = false;
            Build();
        }
    }

    // ================================================================== modes and safety

    [RelayCommand]
    private void StartOrder()
    {
        if (!IsAvailable || Layout == null)
            return;
        ClearPreview();
        ShowSelectedType();
        Status.Clear();
        Mode = MotorSetupMode.Order;
        SafetyAccepted = false;
        Refresh();
        if (CanRunWizard)
        {
            _wizardOutputs = _outputs;
            _wizard = new MotorOrderWizard(Layout, _outputs);
            OrderStage = _wizard.Stage;
            StepText = "";
            OrderInstruction = "";
            ReviewRows = [];
        }
        else
        {
            _wizard = null;
            OrderStage = MotorOrderStage.Cancelled;
        }

        OnPropertyChanged(nameof(ShowSafetyGate));
        UpdateDiagram();
    }

    [RelayCommand]
    private void StartDirection()
    {
        if (!IsAvailable)
            return;
        ClearPreview();
        ShowSelectedType();
        Status.Clear();
        _wizard = null;
        Mode = MotorSetupMode.Direction;
        SafetyAccepted = false;
        Refresh();
        UpdateDiagram();
    }

    /// <summary>Back to the overview, stopping any motor.</summary>
    [RelayCommand]
    private Task CloseModeAsync() => LeaveModeAsync(null);

    private async Task LeaveModeAsync(string? why)
    {
        if (_leaving)
            return;
        _leaving = true;
        try
        {
            await StopSpinAsync();
        }
        finally
        {
            _leaving = false;
        }

        _wizard?.Cancel();
        _wizard = null;
        _wizardOutputs = null;
        OrderStage = MotorOrderStage.Safety;
        SafetyAccepted = false;
        Mode = MotorSetupMode.Overview;
        if (why != null)
            Status.Set(StatusKind.Warning, why);
        else if (Status.Kind is not StatusKind.Success and not StatusKind.Error)
            Status.Clear();
        UpdateDiagram();
    }

    private MotorSafety CurrentSafety() =>
        new(_connected(), PropsRemoved, _armed());

    /// <summary>The safety gate: propellers off, disarmed, then a confirmation.</summary>
    [RelayCommand]
    private async Task BeginAsync()
    {
        var problem = MotorOrderWizard.SafetyProblem(CurrentSafety());
        if (problem != null)
        {
            Status.Set(StatusKind.Error, problem);
            return;
        }

        var what = Mode == MotorSetupMode.Order
            ? $"Each motor output will spin in turn at {Throttle:0}% until you click where that motor is on the drawing."
            : $"The motor you click will spin at {Throttle:0}% until you stop it or click another.";
        if (!await _services.Dialogs.ConfirmAsync(Mode == MotorSetupMode.Order ? "Motor order" : "Motor direction",
                "REMOVE ALL PROPELLERS.\n\n" + what + " Hold the vehicle down and keep clear of the motors.\n\n" + SafetySwitchNote,
                "Propellers removed - start"))
            return;

        // the state may have changed during the dialog
        problem = MotorOrderWizard.SafetyProblem(CurrentSafety());
        if (problem != null)
        {
            Status.Set(StatusKind.Error, problem);
            return;
        }

        Status.Clear();
        if (Mode == MotorSetupMode.Order)
        {
            // from the vehicle as it is now (a stopped wizard may be out of date)
            Refresh();
            if (!CanRunWizard || Layout == null)
                return;
            _wizardOutputs = _outputs;
            _wizard = new MotorOrderWizard(Layout, _outputs);
            var refused = _wizard.Begin(CurrentSafety());
            if (refused != null)
            {
                Status.Set(StatusKind.Error, refused);
                return;
            }

            SafetyAccepted = true;
            SyncWizard();
            await SpinCurrentOutputAsync();
        }
        else if (Mode == MotorSetupMode.Direction)
        {
            SafetyAccepted = true;
            UpdateDiagram();
        }
    }

    // ================================================================== spinning

    /// <summary>Stops the motor. Always available while a mode is open.</summary>
    [RelayCommand]
    private async Task StopAsync()
    {
        await _spinner.StopAllAsync();
        SetSpinning(0, null);
        Status.Set(StatusKind.Info, "Motors stopped.");
    }

    private async Task StopSpinAsync()
    {
        if (!_spinner.IsSpinning && _spinningMotor == 0)
            return;
        await _spinner.StopAsync();
        SetSpinning(0, null);
    }

    private async Task<bool> SpinMotorAsync(int motor, string what)
    {
        if (Layout?.Motors.FirstOrDefault(m => m.Number == motor) is not { } info)
            return false;
        if (!_connected())
        {
            Status.Set(StatusKind.Error, "Not connected.");
            return false;
        }

        IsBusy = true;
        MotorSpinResult result;
        try
        {
            result = await _spinner.SpinAsync(info.TestOrder, (double)Throttle);
        }
        finally
        {
            IsBusy = false;
        }

        switch (result)
        {
            case MotorSpinResult.Spinning:
                SetSpinning(motor, what);
                return true;
            case MotorSpinResult.Denied:
                SetSpinning(0, null);
                var last = _services.Session.Cs.messageHigh;
                Status.Set(StatusKind.Error, "The autopilot refused the motor test. It must be disarmed and landed, with the safety switch " +
                                             "off and the radio calibrated." + (string.IsNullOrWhiteSpace(last) ? "" : " Last message: " + last));
                return false;
            default:
                SetSpinning(0, null);
                Status.Set(StatusKind.Error, "No answer from the vehicle to the motor test command.");
                return false;
        }
    }

    private void SetSpinning(int motor, string? text)
    {
        _spinningMotor = motor;
        IsSpinning = motor > 0;
        SpinText = text ?? "";
        _lastRpm = "";
        foreach (var row in DirectionRows)
        {
            row.IsSpinning = row.Motor.Number == motor;
            row.RpmText = "";
        }

        if (motor == 0)
            OutputLiveText = "";
        UpdateDiagram();
    }

    private void OnSpinFailed(string message)
    {
        SetSpinning(0, null);
        Status.Set(StatusKind.Error, message);
    }

    // ================================================================== order wizard

    private void SyncWizard()
    {
        if (_wizard == null)
            return;
        OrderStage = _wizard.Stage;
        if (_wizard.Current is { } cur)
        {
            StepText = $"Output {_wizard.Step + 1} of {_wizard.StepCount}";
            OrderInstruction = $"Output {cur.Output} (now Motor{cur.Motor}) is spinning. Click the motor on the drawing that is turning on your vehicle.";
        }
        else if (_wizard.Stage == MotorOrderStage.Review)
        {
            StepText = "Result";
            OrderInstruction = "";
            BuildReview();
        }

        UpdateDiagram();
    }

    private async Task SpinCurrentOutputAsync()
    {
        if (_wizard?.Current is not { } cur)
            return;
        await SpinMotorAsync(cur.Motor, $"Output {cur.Output} spinning (test {MotorInfo.TestLetterFor(_wizard.TestOrderOf(cur.Motor))}, Motor{cur.Motor})");
    }

    /// <summary>Spins the current output again (after a refusal or a stop).</summary>
    [RelayCommand]
    private Task SpinAgainAsync() => IsIdentifying && SafetyAccepted ? SpinCurrentOutputAsync() : Task.CompletedTask;

    private async Task PlaceAsync(int position)
    {
        if (_wizard == null || !SafetyAccepted || _wizard.Stage != MotorOrderStage.Identify || IsBusy)
            return;
        var output = _wizard.Current!.Output;
        switch (_wizard.Place(position))
        {
            case MotorAssignResult.PositionTaken:
                Status.Set(StatusKind.Warning,
                    $"Output {_wizard.OutputAt(position)} is already at the position of motor {position}. Click another motor, or use Back to change it.");
                return;
            case MotorAssignResult.UnknownPosition:
            case MotorAssignResult.NotIdentifying:
                return;
            case MotorAssignResult.Next:
                Status.Set(StatusKind.Info, $"Output {output} is at the position of motor {position}.");
                SyncWizard();
                await SpinCurrentOutputAsync();
                return;
            case MotorAssignResult.Finished:
                await StopSpinAsync();
                Status.Clear();
                SyncWizard();
                return;
        }
    }

    [RelayCommand]
    private async Task BackAsync()
    {
        if (_wizard == null || !SafetyAccepted || !_wizard.Back())
            return;
        Status.Clear();
        SyncWizard();
        await SpinCurrentOutputAsync();
    }

    private void BuildReview()
    {
        if (_wizard == null)
            return;
        var mapping = _wizard.Mapping();
        ReviewRows = mapping.Rows.Select(r => new MappingRow(r.Output, r.OldMotor, r.NewMotor)).ToList();
        if (!mapping.IsPermutation)
        {
            ReviewText = "This result cannot be written: " + mapping.Problem;
            CanWriteMapping = false;
        }
        else if (mapping.IsIdentity)
        {
            ReviewText = "Every output already drives the motor at its position: nothing needs to change.";
            CanWriteMapping = false;
        }
        else
        {
            var n = mapping.Writes().Count;
            ReviewText = $"{n} output{(n == 1 ? "" : "s")} will get a new motor function (SERVOn_FUNCTION). The change takes effect after a reboot.";
            CanWriteMapping = true;
        }
    }

    [RelayCommand]
    private async Task WriteMappingAsync()
    {
        if (_wizard == null || _wizard.Stage != MotorOrderStage.Review)
            return;
        var mapping = _wizard.Mapping();
        if (!mapping.IsPermutation || mapping.IsIdentity)
            return;

        // the vehicle must still be as the wizard saw it
        var now = MotorOutputs.Read(_source.Get);
        if (_wizardOutputs == null || !now.SequenceEqual(_wizardOutputs))
        {
            await AbortWizardAsync("The output functions changed while the wizard was running; nothing was written. Start it again.");
            return;
        }

        var writes = mapping.Writes();
        var lines = string.Join("\n", mapping.Rows.Where(r => r.Changed).Select(r =>
            string.Create(CultureInfo.InvariantCulture, $"SERVO{r.Output}_FUNCTION: Motor{r.OldMotor} ({MotorFunctions.FunctionOf(r.OldMotor)}) → Motor{r.NewMotor} ({r.NewFunction})")));
        if (!await _services.Dialogs.ConfirmAsync("Motor order",
                "Write the new motor order?\n\n" + lines + "\n\nIt takes effect after the autopilot reboots. Test the motors again afterwards.",
                "Write"))
            return;

        IsBusy = true;
        var failed = new List<string>();
        try
        {
            foreach (var w in writes)
            {
                if (!await _write(w.Key, w.Value))
                    failed.Add(w.Key);
            }
        }
        finally
        {
            IsBusy = false;
        }

        if (failed.Count < writes.Count)
            _markReboot();
        if (failed.Count == 0)
        {
            _wizard.Complete();
            OrderStage = _wizard.Stage;
            CanWriteMapping = false;
            Status.Set(StatusKind.Success, "Motor order written. Reboot the autopilot, then check the motors with Motor Test or the Motor direction mode.");
        }
        else
        {
            Status.Set(StatusKind.Error, "Not written: " + string.Join(", ", failed) + ". Check the outputs in the table and run the wizard again.");
        }

        _wizardOutputs = MotorOutputs.Read(_source.Get);
        UpdateDiagram();
    }

    [RelayCommand]
    private Task RestartOrderAsync()
    {
        StartOrder();
        return Task.CompletedTask;
    }

    private async Task AbortWizardAsync(string why)
    {
        await StopSpinAsync();
        _wizard?.Cancel();
        OrderStage = MotorOrderStage.Cancelled;
        SafetyAccepted = false;
        Status.Set(StatusKind.Warning, why);
        UpdateDiagram();
    }

    // ================================================================== direction

    private async Task SpinRowAsync(DirectionRow row)
    {
        if (!IsDirection || !SafetyAccepted || IsBusy)
            return;
        if (_spinningMotor == row.Motor.Number)
        {
            await StopSpinAsync();
            return;
        }

        var outs = row.Outputs.Count == 0 ? "no output" : "output " + MotorOutputs.Join(row.Outputs);
        await SpinMotorAsync(row.Motor.Number,
            $"Motor {row.Motor.Number} ({outs}) is spinning. Seen from above it should turn {row.ExpectedText.ToLowerInvariant()}.");
    }

    private async Task ToggleReverseAsync(DirectionRow row, bool reversed)
    {
        var status = Direction;
        if (status is not { CanReverse: true } || row.Outputs.Count == 0)
        {
            RefreshDirection();
            return;
        }

        var current = MotorDirection.FromParam(_source.Get(MotorDirection.ReverseMaskParam));
        var mask = MotorDirection.WithReversed(current, row.Outputs, reversed);
        if (mask == current)
            return;
        IsBusy = true;
        try
        {
            if (await _write(MotorDirection.ReverseMaskParam, MotorDirection.ToParam(mask)))
            {
                _markReboot();
                Status.Set(StatusKind.Success,
                    $"Motor {row.Motor.Number} {(reversed ? "reversed" : "back to normal")} ({MotorDirection.ReverseMaskParam} = {MotorDirection.ToParam(mask)}). " +
                    "It changes after the autopilot reboots: reboot, then spin it again to check.");
            }
        }
        finally
        {
            IsBusy = false;
            RefreshDirection();
            UpdateDiagram();
        }
    }

    [RelayCommand]
    private async Task SetEscTypeAsync()
    {
        if (SelectedEscType is not { } esc || Direction is not { CanOfferEscType: true } status)
            return;
        if (!await _services.Dialogs.ConfirmAsync("DShot ESC type",
                $"Set {MotorDirection.EscTypeParam} to {esc.Label} ({esc.Value})?\n\n{status.PwmTypeParam} is already DShot, " +
                "so ArduPilot can then send DShot commands such as the spin direction to the ESCs. Choose the firmware your ESCs " +
                "actually run. " + RebootSentence(MotorDirection.EscTypeParam),
                "Set ESC type"))
            return;

        IsBusy = true;
        try
        {
            if (await _write(MotorDirection.EscTypeParam, esc.Value))
            {
                if (_source.Meta(MotorDirection.EscTypeParam) is not { RebootRequired: false })
                    _markReboot();
                Status.Set(StatusKind.Success, $"{MotorDirection.EscTypeParam} set to {esc.Label}.");
            }
        }
        finally
        {
            IsBusy = false;
            RefreshDirection();
        }
    }

    // ================================================================== page life

    /// <summary>Called at about 20 Hz while the page is on screen.</summary>
    public void Tick()
    {
        if (!IsAvailable)
            return;
        var session = _services.Session;
        if (!_connected() && (_spinningMotor > 0 || SafetyAccepted) && !_leaving)
        {
            _ = LeaveModeAsync("The link was lost: motors stopped.");
            return;
        }

        if (_spinningMotor == 0)
            return;

        var cs = session.Cs;
        var outs = MotorOutputs.OutputsOf(_outputs, _spinningMotor);
        OutputLiveText = outs.Count == 0
            ? ""
            : string.Join("   ", outs.Select(o => string.Create(CultureInfo.InvariantCulture, $"Output {o}: {Channels.Out(cs, o):0} µs")));

        var rpm = outs.Select(o => EscRpm(cs, o)).DefaultIfEmpty(0).Max();
        var text = rpm > 0 ? string.Create(CultureInfo.InvariantCulture, $"{rpm:0} rpm") : "";
        if (text != _lastRpm)
        {
            _lastRpm = text;
            foreach (var row in DirectionRows)
                row.RpmText = row.Motor.Number == _spinningMotor ? text : "";
            UpdateDiagram();
        }
    }

    /// <summary>The page was left: stop anything spinning and close the mode.</summary>
    public Task OnHiddenAsync() =>
        Mode == MotorSetupMode.Overview && !_spinner.IsSpinning
            ? Task.CompletedTask
            : LeaveModeAsync(_spinner.IsSpinning || SafetyAccepted ? "Stopped when the page was left." : null);

    /// <summary>ESC telemetry RPM by output (ArduPilot numbers ESC telemetry by servo output).</summary>
    public static float EscRpm(CurrentState cs, int output) => output switch
    {
        1 => cs.esc1_rpm, 2 => cs.esc2_rpm, 3 => cs.esc3_rpm, 4 => cs.esc4_rpm,
        5 => cs.esc5_rpm, 6 => cs.esc6_rpm, 7 => cs.esc7_rpm, 8 => cs.esc8_rpm,
        9 => cs.esc9_rpm, 10 => cs.esc10_rpm, 11 => cs.esc11_rpm, 12 => cs.esc12_rpm,
        13 => cs.esc13_rpm, 14 => cs.esc14_rpm, 15 => cs.esc15_rpm, 16 => cs.esc16_rpm,
        _ => 0,
    };

    partial void OnThrottleChanged(decimal value)
    {
        var clamped = Math.Clamp(value, 1, (decimal)MotorSpinner.MaxThrottle);
        if (clamped != value)
            Throttle = clamped;
    }

    partial void OnModeChanged(MotorSetupMode value)
    {
        OnPropertyChanged(nameof(IsWizardRunning));
        OnPropertyChanged(nameof(CanSwitchReversed));
    }

    internal MotorOrderWizard? Wizard => _wizard;

    internal MotorSpinner Spinner => _spinner;

    internal int SpinningMotor => _spinningMotor;
}
