using CommunityToolkit.Mvvm.ComponentModel;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;
using MissionPlanner.App.Core.ViewModels.SetupScreens.MotorSetup;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>One SERVOn output: live PWM and its parameters.</summary>
public sealed class ServoRow(int number, ParamField? reverse, ParamField? function, ParamField? min, ParamField? trim, ParamField? max)
{
    public int Number { get; } = number;

    public PwmChannel Output { get; } = new(number, number.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public ParamField? Reverse { get; } = reverse;

    public ParamField? Function { get; } = function;

    public ParamField? Min { get; } = min;

    public ParamField? Trim { get; } = trim;

    public ParamField? Max { get; } = max;
}

/// <summary>
/// Servo Output (ConfigRadioOutput): SERVO1..16, or 1..32 with SERVO_32_ENABLE, with the live
/// output, reverse, function and min/trim/max. Edits write at once (numbers after a short pause).
/// </summary>
public sealed partial class ServoOutputViewModel : HardwarePageViewModel
{
    private static readonly ParamFieldSpec Toggle = new() { Kind = ParamEditorKind.Toggle };
    private static readonly ParamFieldSpec Pwm = new() { Kind = ParamEditorKind.Number, Min = 800, Max = 2200, Increment = 1, Units = "" };

    public ServoOutputViewModel(AppServices services)
        : this(services, null)
    {
    }

    internal ServoOutputViewModel(AppServices services, IParamSource? source, MotorLayoutCatalog? layouts = null)
        : base("Servo Output", services, source)
    {
        MotorSetup = new MotorSetupViewModel(services, Source, (name, value) => WriteAsync(name, value), () => RebootRequired = true,
            null, layouts, null);
    }

    /// <summary>The motor layout, order and direction card (multirotors and QuadPlanes).</summary>
    public MotorSetupViewModel MotorSetup { get; }

    [ObservableProperty]
    private IReadOnlyList<ServoRow> _rows = [];

    protected override void Build()
    {
        var count = Source.Get("SERVO_32_ENABLE") is > 0 ? 32 : 16;
        var rows = new List<ServoRow>();
        for (var n = 1; n <= count; n++)
        {
            var p = "SERVO" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!Source.Has(p + "_FUNCTION"))
                continue;
            rows.Add(new ServoRow(n,
                Field(p + "_REVERSED", Toggle),
                Field(p + "_FUNCTION", new ParamFieldSpec { Kind = ParamEditorKind.Options }),
                Field(p + "_MIN", Pwm),
                Field(p + "_TRIM", Pwm),
                Field(p + "_MAX", Pwm)));
        }

        Rows = rows;
        Status = rows.Count == 0 ? "This vehicle has no SERVOn_FUNCTION parameters." : null;
        MotorSetup.Build();
    }

    protected override void OnValuesRefreshed() => MotorSetup.Refresh();

    public override void OnHidden()
    {
        base.OnHidden();
        _ = MotorSetup.OnHiddenAsync();
    }

    protected override void UpdateLive()
    {
        var cs = Services.Session.Cs;
        foreach (var r in Rows)
            r.Output.Set(Channels.Out(cs, r.Number));
        MotorSetup.Tick();
    }
}
