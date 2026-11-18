using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>A "Test motor X" button: test order (what DO_MOTOR_TEST takes) and the motor it drives.</summary>
public sealed class MotorButton(int testOrder, MotorInfo? motor, Func<int, Task> test)
{
    public int TestOrder { get; } = testOrder;

    public string Letter { get; } = MotorInfo.TestLetterFor(testOrder);

    public MotorInfo? Motor { get; } = motor;

    public string Title => "Test motor " + Letter;

    public string Detail => Motor == null
        ? ""
        : "Motor " + Motor.Number.ToString(CultureInfo.InvariantCulture) + (Motor.HasRotation ? " · " + Motor.Rotation : "");

    public IAsyncRelayCommand TestCommand { get; } = new AsyncRelayCommand(() => test(testOrder));
}

/// <summary>
/// Motor Test (ConfigMotorTest): spins motors with DO_MOTOR_TEST, one at a time in test order
/// (A = front right, clockwise), all together or in sequence. Confirms before the first spin.
/// </summary>
public sealed partial class MotorTestViewModel : HardwarePageViewModel
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(MotorTestViewModel));

    private readonly MotorLayoutCatalog _layouts;
    private bool _confirmed;

    public MotorTestViewModel(AppServices services)
        : this(services, null, null)
    {
    }

    internal MotorTestViewModel(AppServices services, IParamSource? source, MotorLayoutCatalog? layouts)
        : base("Motor Test", services, source)
    {
        _layouts = layouts ?? MotorLayoutCatalog.Default;
    }

    public const string Warning = "Remove all propellers before testing motors!";

    public const string Note =
        "NOTE: PLEASE HOLD DOWN YOUR UAV\nThis will test your motors are working.\n" +
        "Motors are tested in a clockwise rotation starting at the front right.";

    public string NoteText => Note;

    public string WarningText => Warning;

    public string MotorOrderUrl => "https://ardupilot.org/copter/docs/connect-escs-and-motors.html#motor-order-diagrams";

    [ObservableProperty]
    private IReadOnlyList<MotorButton> _motors = [];

    [ObservableProperty]
    private MotorLayout? _layout;

    public bool HasLayout => Layout != null;

    partial void OnLayoutChanged(MotorLayout? value) => OnPropertyChanged(nameof(HasLayout));

    [ObservableProperty]
    private bool _canTest;

    [ObservableProperty]
    private string _classText = "Class: unknown";

    [ObservableProperty]
    private string _typeText = "Type: unknown";

    /// <summary>Throttle in percent (DO_MOTOR_TEST, MOTOR_TEST_THROTTLE_PERCENT).</summary>
    [ObservableProperty]
    private decimal _throttle = 5;

    [ObservableProperty]
    private decimal _duration = 2;

    [ObservableProperty]
    private ParamField? _spinArm;

    [ObservableProperty]
    private ParamField? _spinMin;

    [ObservableProperty]
    private bool _isTesting;

    public override void OnShown()
    {
        // ask again each time the page is opened
        _confirmed = false;
        base.OnShown();
    }

    protected override void Build()
    {
        var plan = MotorTestLogic.Resolve(Source.Get, Services.Session.Vehicle.aptype, _layouts);
        CanTest = plan.Enabled && plan.MotorCount > 0;
        Layout = plan.Layout;
        Motors = Enumerable.Range(1, plan.MotorCount)
            .Select(order => new MotorButton(order, plan.Layout?.Motors.FirstOrDefault(m => m.TestOrder == order), TestOneAsync))
            .ToList();

        ClassText = "Class: " + Describe(plan.ClassParam);
        TypeText = "Type: " + Describe(plan.TypeParam);
        SpinArm = Field(["MOT_SPIN_ARM", "Q_M_SPIN_ARM"], new ParamFieldSpec { Kind = ParamEditorKind.Number, Label = "Spin when armed", Min = 0, Max = 1, Increment = 0.01 });
        SpinMin = Field(["MOT_SPIN_MIN", "Q_M_SPIN_MIN"], new ParamFieldSpec { Kind = ParamEditorKind.Number, Label = "Spin minimum", Min = 0, Max = 1, Increment = 0.01 });

        Status = !plan.Enabled
            ? "Motor test needs a FRAME, FRAME_TYPE or Q_FRAME_TYPE parameter."
            : plan.MotorCount == 0 ? "This frame has no motors to test (helicopter)." : null;
    }

    private string Describe(string? param)
    {
        if (param == null || Source.Get(param) is not { } v)
            return "unknown";
        var label = Source.Meta(param)?.Values.FirstOrDefault(o => o.Key == v).Value;
        return string.IsNullOrEmpty(label) ? v.ToString(CultureInfo.InvariantCulture) : label;
    }

    private async Task<bool> ConfirmSpinAsync()
    {
        if (_confirmed)
            return true;
        _confirmed = await Services.Dialogs.ConfirmAsync("Motor test",
            $"{Warning}\n\nMotors will spin at {Throttle:0}% throttle for {Duration:0.#} s. Hold the vehicle down and keep clear of the motors.",
            "Propellers removed - spin");
        return _confirmed;
    }

    private Task TestOneAsync(int order) => RunAsync(async () =>
    {
        if (await ConfirmSpinAsync())
            await TestMotorAsync(order, (float)Throttle, (float)Duration);
    });

    [RelayCommand]
    private Task TestAllAsync() => RunAsync(async () =>
    {
        if (!await ConfirmSpinAsync())
            return;
        foreach (var m in Motors)
        {
            if (!await TestMotorAsync(m.TestOrder, (float)Throttle, (float)Duration))
                break;
        }
    });

    [RelayCommand]
    private Task TestSequenceAsync() => RunAsync(async () =>
    {
        if (await ConfirmSpinAsync())
            await TestMotorAsync(1, (float)Throttle, (float)Duration, Motors.Count);
    });

    /// <summary>Throttle 0 for 0 s to every motor; never asks.</summary>
    [RelayCommand]
    private async Task StopAllAsync()
    {
        foreach (var m in Motors)
            await TestMotorAsync(m.TestOrder, 0, 0, 0, quiet: true);
        Status = "Stop sent to all motors.";
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (IsTesting)
            return;
        IsTesting = true;
        try
        {
            await action();
        }
        finally
        {
            IsTesting = false;
        }
    }

    /// <summary>DO_MOTOR_TEST: p1 test order, p2 throttle type (percent), p3 throttle, p4 seconds, p5 motor count (0 = one, N = sequence).</summary>
    private async Task<bool> TestMotorAsync(int order, float throttle, float seconds, int count = 0, bool quiet = false)
    {
        var session = Services.Session;
        if (!session.IsConnected)
            return false;
        var mav = session.Mav;
        var sysid = session.Vehicle.sysid;
        var compid = session.Vehicle.compid;
        try
        {
            var ok = await Task.Run(() => mav.doCommand(sysid, compid, MAVLink.MAV_CMD.DO_MOTOR_TEST,
                order, (float)MAVLink.MOTOR_TEST_THROTTLE_TYPE.MOTOR_TEST_THROTTLE_PERCENT, throttle, seconds, count, 0, 0));
            if (!ok)
            {
                Status = "Command was denied by the autopilot.";
                if (!quiet)
                    await Services.Dialogs.ShowMessageAsync("Motor test", "Command was denied by the autopilot.", MessageKind.Warning);
                return false;
            }

            Status = count > 0
                ? $"Testing all motors in sequence at {throttle:0}%."
                : throttle > 0 ? $"Testing motor {MotorInfo.TestLetterFor(order)} at {throttle:0}% for {seconds:0.#} s." : Status;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("motor test", ex);
            Status = "Error communicating with the vehicle.";
            if (!quiet)
                await Services.Dialogs.ShowMessageAsync("Motor test", "Error communicating with the vehicle.\nMotor: " + MotorInfo.TestLetterFor(order), MessageKind.Error);
            return false;
        }
    }

    [RelayCommand]
    private Task SetSpinArmAsync() => SetSpinAsync(SpinArm, "Set Motor Spin Arm", "Enter arm throttle % (deadzone + 2%)", (int)Throttle + 2);

    [RelayCommand]
    private Task SetSpinMinAsync() => SetSpinAsync(SpinMin, "Set Motor Spin Min", "Enter min spin throttle % (arm min + 3%)",
        (int)Math.Round((SpinArm?.Value ?? 0) * 100) + 3);

    /// <summary>Mission Planner's spin helpers: a percentage written as a 0-1 fraction, refused above 20% test throttle.</summary>
    private async Task SetSpinAsync(ParamField? field, string title, string prompt, int suggested)
    {
        if (field == null)
        {
            await Services.Dialogs.ShowMessageAsync(title, "This vehicle has no such parameter.", MessageKind.Error);
            return;
        }

        if (Throttle >= 20)
        {
            await Services.Dialogs.ShowMessageAsync(title, "Throttle percent above 20, too high", MessageKind.Error);
            return;
        }

        var answer = await Services.Dialogs.PromptAsync(title, prompt, suggested.ToString(CultureInfo.InvariantCulture));
        if (answer == null)
            return;
        if (!double.TryParse(answer.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var percent) || percent < 0 || percent > 100)
        {
            await Services.Dialogs.ShowMessageAsync(title, "Enter a percentage between 0 and 100.", MessageKind.Error);
            return;
        }

        await field.WriteAsync(percent / 100.0);
    }
}
