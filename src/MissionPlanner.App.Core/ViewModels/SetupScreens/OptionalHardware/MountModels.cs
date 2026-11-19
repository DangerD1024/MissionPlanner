using CommunityToolkit.Mvvm.ComponentModel;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.OptionalHardware;

/// <summary>One gimbal axis: angle limits, the servo output that drives it and the RC input that steers it.</summary>
public sealed class MountAxis(string title)
{
    public string Title { get; } = title;

    /// <summary>Lowest angle, in degrees.</summary>
    public ScaledField? Min { get; init; }

    /// <summary>Highest angle, in degrees.</summary>
    public ScaledField? Max { get; init; }

    /// <summary>Servo output carrying this axis (servo-driven gimbals).</summary>
    public ChannelPicker? Output { get; init; }

    public ParamField? ServoMin { get; init; }

    public ParamField? ServoMax { get; init; }

    public ParamField? ServoReversed { get; init; }

    /// <summary>RC channel steering the axis (RCn_OPTION on 4.3+).</summary>
    public ChannelPicker? Input { get; init; }

    /// <summary>Old firmware: MNT_RC_IN_TILT... channel number.</summary>
    public ParamField? InputField { get; init; }

    /// <summary>Old firmware: MNT_STAB_TILT... stabilise this axis.</summary>
    public ParamField? Stabilise { get; init; }

    public bool HasServo => ServoMin != null || ServoMax != null || ServoReversed != null;

    public bool HasInputPicker => Input != null;
}

/// <summary>One mount (gimbal) instance: MNT1_ / MNT2_ on 4.3+, MNT_ before.</summary>
public sealed partial class MountInstance(int number, string prefix) : ObservableObject
{
    public int Number { get; } = number;

    public string Prefix { get; } = prefix;

    public string Title => "Mount " + Number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public ParamField? Type { get; init; }

    public ParamField? DefaultMode { get; init; }

    public ParamField? RcRate { get; init; }

    public ParamField? Options { get; init; }

    public IReadOnlyList<MountAxis> Axes { get; init; } = [];

    public IReadOnlyList<ScaledField> Neutral { get; init; } = [];

    public IReadOnlyList<ScaledField> Retract { get; init; } = [];

    /// <summary>Output that retracts the gimbal (servo function Mount Retract).</summary>
    public ChannelPicker? RetractOutput { get; init; }

    public IReadOnlyList<ParamField> Other { get; init; } = [];

    public bool HasOther => Other.Count > 0;

    /// <summary>The gimbal is driven by PWM outputs (Servo or Brushless PWM type).</summary>
    [ObservableProperty]
    private bool _usesServos;

    [ObservableProperty]
    private bool _isFitted;

    public IEnumerable<ChannelPicker> Pickers =>
        Axes.SelectMany(a => new[] { a.Output, a.Input }).Append(RetractOutput).OfType<ChannelPicker>();

    public override string ToString() => Title;
}
