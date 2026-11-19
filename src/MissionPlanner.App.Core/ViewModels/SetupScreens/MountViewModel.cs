using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;
using MissionPlanner.App.Core.ViewModels.SetupScreens.OptionalHardware;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>
/// Mount (ConfigMount): gimbal type, angle limits, the servo outputs and RC inputs of each axis,
/// neutral / retract angles and the camera shutter, with the live gimbal angles. Covers the 4.3+
/// MNT1_/MNT2_ parameters and the older single MNT_ set. Edits write at once.
/// </summary>
public sealed partial class MountViewModel : HardwarePageViewModel
{
    /// <summary>MNTn_TYPE values driven by PWM outputs.</summary>
    internal static bool UsesServos(double? type) => type is 1 or 7;

    private static readonly ParamFieldSpec Pwm = new() { Kind = ParamEditorKind.Number, Min = 800, Max = 2200, Increment = 1, Units = "" };

    private bool _rebuilding;

    public MountViewModel(AppServices services)
        : this(services, null)
    {
    }

    internal MountViewModel(AppServices services, IParamSource? source)
        : base("Mount", services, source)
    {
    }

    [ObservableProperty]
    private IReadOnlyList<MountInstance> _mounts = [];

    [ObservableProperty]
    private MountInstance? _selected;

    [ObservableProperty]
    private bool _hasSeveral;

    // camera shutter (Mission Planner shows it on the gimbal page)
    [ObservableProperty]
    private ParamField? _cameraType;

    [ObservableProperty]
    private ChannelPicker? _shutterOutput;

    [ObservableProperty]
    private ParamField? _shutterOn;

    [ObservableProperty]
    private ParamField? _shutterOff;

    [ObservableProperty]
    private ParamField? _shutterDuration;

    [ObservableProperty]
    private bool _hasCamera;

    [ObservableProperty]
    private bool _isServoShutter;

    [ObservableProperty]
    private string _rollText = "";

    [ObservableProperty]
    private string _pitchText = "";

    [ObservableProperty]
    private string _yawText = "";

    private double _cameraServoType;

    protected override void Build()
    {
        _rebuilding = true;
        try
        {
            var mounts = new List<MountInstance>();
            if (Source.Has("MNT1_TYPE"))
            {
                mounts.Add(BuildNew(1));
                if (Source.Has("MNT2_TYPE"))
                    mounts.Add(BuildNew(2));
            }
            else if (Source.Has("MNT_TYPE"))
            {
                mounts.Add(BuildOld());
            }

            var keep = Selected?.Number;
            Mounts = mounts;
            HasSeveral = mounts.Count > 1;
            Selected = mounts.FirstOrDefault(m => m.Number == keep) ?? mounts.FirstOrDefault();
            BuildCamera();
            Status = mounts.Count == 0 ? "This vehicle has no MNT1_TYPE parameter." : null;
        }
        finally
        {
            _rebuilding = false;
        }

        OnValuesRefreshed();
    }

    private MountInstance BuildNew(int n)
    {
        var p = "MNT" + n.ToString(CultureInfo.InvariantCulture) + "_";
        var outputs = ChannelAssignment.ServoOutputs(Source);
        var inputs = ChannelAssignment.RcInputs(Source);
        int[] servoFn = n == 1
            ? [ChannelAssignment.MountRoll, ChannelAssignment.MountPitch, ChannelAssignment.MountYaw, ChannelAssignment.MountRetract]
            : [ChannelAssignment.Mount2Roll, ChannelAssignment.Mount2Pitch, ChannelAssignment.Mount2Yaw, ChannelAssignment.Mount2Retract];
        int[] rcOpt = n == 1
            ? [ChannelAssignment.RcMount1Roll, ChannelAssignment.RcMount1Pitch, ChannelAssignment.RcMount1Yaw]
            : [ChannelAssignment.RcMount2Roll, ChannelAssignment.RcMount2Pitch, ChannelAssignment.RcMount2Yaw];
        string[] titles = ["Roll", "Pitch (tilt)", "Yaw (pan)"];
        string[] keys = ["ROLL", "PITCH", "YAW"];

        var axes = new List<MountAxis>();
        for (var i = 0; i < 3; i++)
        {
            var output = outputs.Count > 0 ? Picker("Servo output", "_FUNCTION", servoFn[i], outputs) : null;
            var servo = output == null ? null : ChannelAssignment.Find(Source, outputs, "_FUNCTION", servoFn[i]);
            axes.Add(new MountAxis(titles[i])
            {
                Min = Degrees(p + keys[i] + "_MIN", 1, "Min angle"),
                Max = Degrees(p + keys[i] + "_MAX", 1, "Max angle"),
                Output = output,
                ServoMin = servo == null ? null : Field(servo + "_MIN", Pwm with { Label = "Servo min" }),
                ServoMax = servo == null ? null : Field(servo + "_MAX", Pwm with { Label = "Servo max" }),
                ServoReversed = servo == null ? null : Reverse(servo),
                Input = inputs.Count > 0 ? Picker("RC input", "_OPTION", rcOpt[i], inputs) : null,
            });
        }

        var shown = new HashSet<string>(StringComparer.Ordinal)
        {
            p + "TYPE", p + "DEFLT_MODE", p + "RC_RATE", p + "OPTIONS",
        };
        foreach (var k in keys)
        {
            shown.Add(p + k + "_MIN");
            shown.Add(p + k + "_MAX");
        }

        foreach (var k in new[] { "NEUTRAL_X", "NEUTRAL_Y", "NEUTRAL_Z", "RETRACT_X", "RETRACT_Y", "RETRACT_Z" })
            shown.Add(p + k);

        var mount = new MountInstance(n, p)
        {
            Type = Field(p + "TYPE", new ParamFieldSpec { Kind = ParamEditorKind.Options, Label = "Type" }),
            DefaultMode = Field(p + "DEFLT_MODE", new ParamFieldSpec { Label = "Default mode" }),
            RcRate = Field(p + "RC_RATE", new ParamFieldSpec { Label = "RC rate" }),
            Options = Field(p + "OPTIONS"),
            Axes = axes,
            Neutral = Xyz(p + "NEUTRAL_"),
            Retract = Xyz(p + "RETRACT_"),
            RetractOutput = outputs.Count > 0 ? Picker("Retract output", "_FUNCTION", servoFn[3], outputs) : null,
            Other = Source.Names()
                .Where(x => x.StartsWith(p, StringComparison.Ordinal) && !shown.Contains(x))
                .OrderBy(x => x, StringComparer.Ordinal)
                .Select(x => Field(x))
                .OfType<ParamField>()
                .ToList(),
        };
        HookType(mount);
        return mount;
    }

    private MountInstance BuildOld()
    {
        const string p = "MNT_";
        var outputs = ChannelAssignment.ServoOutputs(Source);
        int[] servoFn = [ChannelAssignment.MountRoll, ChannelAssignment.MountPitch, ChannelAssignment.MountYaw];
        string[] titles = ["Roll", "Pitch (tilt)", "Yaw (pan)"];
        string[] keys = ["ROL", "TIL", "PAN"];
        string[] inputKeys = ["ROLL", "TILT", "PAN"];

        var axes = new List<MountAxis>();
        for (var i = 0; i < 3; i++)
        {
            var output = outputs.Count > 0 ? Picker("Servo output", "_FUNCTION", servoFn[i], outputs) : null;
            var servo = output == null ? null : ChannelAssignment.Find(Source, outputs, "_FUNCTION", servoFn[i]);
            axes.Add(new MountAxis(titles[i])
            {
                // the old limits are centidegrees
                Min = Degrees(p + "ANGMIN_" + keys[i], 100, "Min angle"),
                Max = Degrees(p + "ANGMAX_" + keys[i], 100, "Max angle"),
                Output = output,
                ServoMin = servo == null ? null : Field(servo + "_MIN", Pwm with { Label = "Servo min" }),
                ServoMax = servo == null ? null : Field(servo + "_MAX", Pwm with { Label = "Servo max" }),
                ServoReversed = servo == null ? null : Reverse(servo),
                InputField = Field(p + "RC_IN_" + inputKeys[i], new ParamFieldSpec { Label = "RC input" }),
                Stabilise = Field(p + "STAB_" + inputKeys[i], new ParamFieldSpec { Kind = ParamEditorKind.Toggle, Label = "Stabilise" }),
            });
        }

        var mount = new MountInstance(1, p)
        {
            Type = Field(p + "TYPE", new ParamFieldSpec { Kind = ParamEditorKind.Options, Label = "Type" }),
            DefaultMode = Field(p + "DEFLT_MODE", new ParamFieldSpec { Label = "Default mode" }),
            RcRate = Field(new[] { p + "RC_RATE", p + "JSTICK_SPD" }, new ParamFieldSpec { Label = "RC rate" }),
            Axes = axes,
            Neutral = Xyz(p + "NEUTRAL_"),
            Retract = Xyz(p + "RETRACT_"),
            RetractOutput = outputs.Count > 0 ? Picker("Retract output", "_FUNCTION", ChannelAssignment.MountRetract, outputs) : null,
        };
        HookType(mount);
        return mount;
    }

    private void HookType(MountInstance mount)
    {
        if (mount.Type != null)
            mount.Type.Written += (_, e) =>
            {
                if (e.OldValue != e.NewValue)
                    RebootRequired = true;
                OnValuesRefreshed();
            };
    }

    private void BuildCamera()
    {
        string? prefix = Source.Has("CAM1_TYPE") ? "CAM1_" : Source.Has("CAM_TRIGG_TYPE") ? "CAM_" : null;
        HasCamera = prefix != null;
        if (prefix == null)
        {
            CameraType = ShutterOn = ShutterOff = ShutterDuration = null;
            ShutterOutput = null;
            return;
        }

        // 4.5+ CAM1_TYPE: 1 = servo; older CAM_TRIGG_TYPE: 0 = servo
        _cameraServoType = prefix == "CAM1_" ? 1 : 0;
        CameraType = Field(prefix == "CAM1_" ? "CAM1_TYPE" : "CAM_TRIGG_TYPE", new ParamFieldSpec { Kind = ParamEditorKind.Options, Label = "Shutter type" });
        if (CameraType != null)
            CameraType.Written += (_, e) =>
            {
                if (e.OldValue != e.NewValue && CameraType.RebootRequired)
                    RebootRequired = true;
                OnValuesRefreshed();
            };
        var outputs = ChannelAssignment.ServoOutputs(Source);
        ShutterOutput = outputs.Count > 0 ? Picker("Shutter output", "_FUNCTION", ChannelAssignment.CameraTrigger, outputs) : null;
        ShutterOn = Field(prefix + "SERVO_ON", Pwm with { Label = "Pushed PWM" });
        ShutterOff = Field(prefix + "SERVO_OFF", Pwm with { Label = "Not pushed PWM" });
        ShutterDuration = Field(prefix + "DURATION", new ParamFieldSpec { Label = "Duration" });
    }

    private ChannelPicker Picker(string label, string suffix, int value, IReadOnlyList<string> channels) =>
        new(label, suffix, value, channels, ch => ApplyAsync(suffix, value, channels, ch), suffix == "_OPTION" ? "None" : "Disabled");

    private ScaledField? Degrees(string name, double scale, string label)
    {
        var f = Field(name, new ParamFieldSpec { Kind = ParamEditorKind.Number });
        return f == null ? null : new ScaledField(f, scale, "deg", -180, 180, 1, label);
    }

    private IReadOnlyList<ScaledField> Xyz(string prefix) =>
        new[] { "X", "Y", "Z" }
            .Select(a => Degrees(prefix + a, 1, a switch { "X" => "Roll (X)", "Y" => "Pitch (Y)", _ => "Yaw (Z)" }))
            .OfType<ScaledField>()
            .ToList();

    // SERVOn_REVERSED is 0/1; the older RCn_REV was 1/-1
    private ParamField? Reverse(string servo) =>
        Source.Has(servo + "_REVERSED")
            ? Field(servo + "_REVERSED", new ParamFieldSpec { Kind = ParamEditorKind.Toggle, Label = "Reverse" })
            : Field(servo + "_REV", new ParamFieldSpec { Kind = ParamEditorKind.Toggle, Label = "Reverse", ToggleOn = -1, ToggleOff = 1 });

    private async Task ApplyAsync(string suffix, int value, IReadOnlyList<string> channels, string? chosen)
    {
        var ok = true;
        foreach (var (name, v) in ChannelAssignment.Plan(Source, channels, suffix, value, chosen))
        {
            if (!await WriteAsync(name, v))
            {
                ok = false;
                break;
            }
        }

        // as Mission Planner: giving the gimbal an output switches 3-axis stabilisation on (old MNT_MODE)
        if (ok && chosen != null && suffix == "_FUNCTION" && Source.Has("MNT_MODE") && Source.Get("MNT_MODE") != 3)
            await WriteAsync("MNT_MODE", 3);

        // the servo's min / max / reverse rows follow the chosen output
        if (!_rebuilding && suffix == "_FUNCTION")
            Rebuild();
    }

    protected override void OnValuesRefreshed()
    {
        foreach (var m in Mounts)
        {
            m.IsFitted = m.Type?.Value is > 0;
            m.UsesServos = UsesServos(m.Type?.Value);
            foreach (var picker in m.Pickers)
                picker.Sync(Source);
        }

        ShutterOutput?.Sync(Source);
        IsServoShutter = CameraType?.Value == _cameraServoType;
    }

    protected override void UpdateLive()
    {
        var cs = Services.Session.Cs;
        RollText = cs.campointb.ToString("0.0", CultureInfo.InvariantCulture) + "°";
        PitchText = cs.campointa.ToString("0.0", CultureInfo.InvariantCulture) + "°";
        YawText = cs.campointc.ToString("0.0", CultureInfo.InvariantCulture) + "°";
    }
}
