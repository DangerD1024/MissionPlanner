using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>
/// ESC Calibration (ConfigESCCalibration): arms the all-at-once calibration (ESC_CALIBRATION = 3,
/// performed by the autopilot on the next power-up) and edits the motor output range.
/// </summary>
public sealed partial class EscCalibrationViewModel : HardwarePageViewModel
{
    public const string Instructions =
        "Remove Props!\n" +
        "After pushing this button:\n" +
        "- Disconnect USB and battery\n" +
        "- Plug in battery\n" +
        "- When LEDs flash, push Safety Switch (if present)\n" +
        "- ESCs should beep as they are calibrated\n" +
        "- Restart flight controller normally";

    public EscCalibrationViewModel(AppServices services)
        : this(services, null)
    {
    }

    internal EscCalibrationViewModel(AppServices services, IParamSource? source)
        : base("ESC Calibration", services, source)
    {
    }

    public string InstructionsText => Instructions;

    [ObservableProperty]
    private ParamField? _pwmType;

    [ObservableProperty]
    private ParamField? _pwmMin;

    [ObservableProperty]
    private ParamField? _pwmMax;

    [ObservableProperty]
    private ParamField? _spinArm;

    [ObservableProperty]
    private ParamField? _spinMin;

    [ObservableProperty]
    private ParamField? _spinMax;

    [ObservableProperty]
    private IReadOnlyList<ParamField> _outputFields = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CalibrateCommand))]
    private bool _calibrationArmed;

    [ObservableProperty]
    private bool _canCalibrate;

    protected override void Build()
    {
        // QuadPlane motors use the Q_M_ copies of the MOT_ parameters
        PwmType = Field(["MOT_PWM_TYPE", "Q_M_PWM_TYPE"], new ParamFieldSpec { Label = "ESC Type", Kind = ParamEditorKind.Options });
        PwmMin = Field(["MOT_PWM_MIN", "Q_M_PWM_MIN"], new ParamFieldSpec { Kind = ParamEditorKind.Number, Label = "Output PWM Min", Min = 0, Max = 1500, Increment = 1 });
        PwmMax = Field(["MOT_PWM_MAX", "Q_M_PWM_MAX"], new ParamFieldSpec { Kind = ParamEditorKind.Number, Label = "Output PWM Max", Min = 0, Max = 2200, Increment = 1 });
        SpinArm = Field(["MOT_SPIN_ARM", "Q_M_SPIN_ARM"], new ParamFieldSpec { Kind = ParamEditorKind.Number, Label = "Spin when Armed", Min = 0, Max = 1, Increment = 0.01 });
        SpinMin = Field(["MOT_SPIN_MIN", "Q_M_SPIN_MIN"], new ParamFieldSpec { Kind = ParamEditorKind.Number, Label = "Spin minimum", Min = 0, Max = 1, Increment = 0.01 });
        SpinMax = Field(["MOT_SPIN_MAX", "Q_M_SPIN_MAX"], new ParamFieldSpec { Kind = ParamEditorKind.Number, Label = "Spin Maximum", Min = 0, Max = 1, Increment = 0.01 });
        OutputFields = new[] { PwmType, PwmMin, PwmMax, SpinArm, SpinMin, SpinMax }.OfType<ParamField>().ToList();
        CanCalibrate = Source.Has("ESC_CALIBRATION");
        CalibrationArmed = false;
        if (!CanCalibrate)
            Status = "This firmware has no ESC_CALIBRATION parameter (Copter 3.3 or later is needed).";
    }

    private bool CanStart() => !CalibrationArmed;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task CalibrateAsync()
    {
        if (!await Services.Dialogs.ConfirmAsync("ESC calibration",
                "Remove the propellers before continuing.\n\nThe autopilot calibrates all ESCs the next time it powers up with a battery: " +
                "disconnect USB and the battery, plug the battery in, press the safety switch when the LEDs flash, wait for the ESCs to beep, " +
                "then restart the flight controller normally.",
                "Props are off - calibrate"))
            return;

        bool ok;
        try
        {
            ok = await Source.SetAsync("ESC_CALIBRATION", 3);
        }
        catch (Exception)
        {
            ok = false;
        }

        if (!ok)
        {
            await Services.Dialogs.ShowMessageAsync("ESC calibration", "Set param error. Please ensure your version is AC3.3+.", MessageKind.Error);
            return;
        }

        CalibrationArmed = true;
        Status = "ESC calibration is armed. Disconnect USB and the battery, then power up from the battery to calibrate.";
    }
}
