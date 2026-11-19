using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;

/// <summary>
/// Channel parameters that select what a channel does (SERVOn_FUNCTION, RCn_OPTION): which channel
/// has a given function, and the writes that give it to exactly one channel. This is Mission
/// Planner's "pick an output" combo (ConfigMount / ConfigHWParachute): the chosen channel gets the
/// function, every other channel that had it is set to 0 (disabled).
/// </summary>
public static class ChannelAssignment
{
    // SRV_Channel::Aux_servo_function_t
    public const int MountYaw = 6;
    public const int MountPitch = 7;
    public const int MountRoll = 8;
    public const int MountRetract = 9;
    public const int CameraTrigger = 10;
    public const int Mount2Yaw = 12;
    public const int Mount2Pitch = 13;
    public const int Mount2Roll = 14;
    public const int Mount2Retract = 15;
    public const int Parachute = 27;

    // RC_Channel::AUX_FUNC
    public const int RcMount1Roll = 212;
    public const int RcMount1Pitch = 213;
    public const int RcMount1Yaw = 214;
    public const int RcMount2Roll = 215;
    public const int RcMount2Pitch = 216;
    public const int RcMount2Yaw = 217;

    /// <summary>
    /// The servo outputs of the vehicle as name prefixes ("SERVO1".."SERVO32"; very old firmware
    /// used "RC5".."RC14" with RCn_FUNCTION).
    /// </summary>
    public static IReadOnlyList<string> ServoOutputs(IParamSource src)
    {
        var list = Numbered(src, "SERVO", "_FUNCTION", 1, 32);
        return list.Count > 0 ? list : Numbered(src, "RC", "_FUNCTION", 1, 16);
    }

    /// <summary>RC input channels that take an RCn_OPTION ("RC1".."RC16").</summary>
    public static IReadOnlyList<string> RcInputs(IParamSource src) => Numbered(src, "RC", "_OPTION", 1, 16);

    /// <summary>The prefixes <c>{stem}{n}</c> for which <c>{stem}{n}{suffix}</c> exists, in order.</summary>
    public static IReadOnlyList<string> Numbered(IParamSource src, string stem, string suffix, int first, int last)
    {
        var list = new List<string>();
        for (var n = first; n <= last; n++)
        {
            var prefix = stem + n.ToString(CultureInfo.InvariantCulture);
            if (src.Has(prefix + suffix))
                list.Add(prefix);
        }

        return list;
    }

    /// <summary>The first channel whose <paramref name="suffix"/> parameter equals <paramref name="value"/>, or null.</summary>
    public static string? Find(IParamSource src, IEnumerable<string> channels, string suffix, double value) =>
        channels.FirstOrDefault(c => src.Get(c + suffix) is { } v && Math.Round(v) == value);

    /// <summary>
    /// The writes that leave <paramref name="value"/> on <paramref name="chosen"/> only (null: on no
    /// channel). Other channels holding the value are cleared first; nothing is listed for
    /// parameters that already have the wanted value.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, double>> Plan(IParamSource src, IEnumerable<string> channels, string suffix,
        double value, string? chosen)
    {
        var writes = new List<KeyValuePair<string, double>>();
        foreach (var c in channels)
        {
            if (c == chosen)
                continue;
            if (src.Get(c + suffix) is { } v && Math.Round(v) == value)
                writes.Add(new(c + suffix, 0));
        }

        if (chosen != null && src.Has(chosen + suffix) && (src.Get(chosen + suffix) is not { } cur || Math.Round(cur) != value))
            writes.Add(new(chosen + suffix, value));
        return writes;
    }
}

/// <summary>One entry of a <see cref="ChannelPicker"/>: a channel prefix ("SERVO9") or none.</summary>
public sealed record ChannelOption(string? Channel, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// A drop-down that gives one function to one channel (see <see cref="ChannelAssignment"/>). The
/// page supplies the apply step (it writes the parameters and reports failures) and calls
/// <see cref="Sync"/> after values change.
/// </summary>
public sealed partial class ChannelPicker : ObservableObject
{
    private readonly Func<string?, Task> _apply;
    private bool _syncing;

    public ChannelPicker(string label, string suffix, double value, IReadOnlyList<string> channels, Func<string?, Task> apply,
        string noneLabel = "Disabled")
    {
        Label = label;
        Suffix = suffix;
        Value = value;
        Channels = channels;
        _apply = apply;
        Options = new[] { new ChannelOption(null, noneLabel) }
            .Concat(channels.Select(c => new ChannelOption(c, c)))
            .ToList();
    }

    public string Label { get; }

    /// <summary>"_FUNCTION" or "_OPTION".</summary>
    public string Suffix { get; }

    /// <summary>The function / option number this picker assigns.</summary>
    public double Value { get; }

    public IReadOnlyList<string> Channels { get; }

    public IReadOnlyList<ChannelOption> Options { get; }

    [ObservableProperty]
    private ChannelOption? _selected;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>The selected channel prefix, or null for none.</summary>
    public string? Channel => Selected?.Channel;

    partial void OnSelectedChanged(ChannelOption? value)
    {
        OnPropertyChanged(nameof(Channel));
        if (_syncing || value == null)
            return;
        _ = ApplyAsync(value.Channel);
    }

    private async Task ApplyAsync(string? channel)
    {
        IsBusy = true;
        try
        {
            await _apply(channel);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Shows the channel that currently holds the function (no writes).</summary>
    public void Sync(IParamSource src)
    {
        var current = ChannelAssignment.Find(src, Channels, Suffix, Value);
        _syncing = true;
        try
        {
            Selected = Options.FirstOrDefault(o => o.Channel == current) ?? Options[0];
        }
        finally
        {
            _syncing = false;
        }
    }
}
