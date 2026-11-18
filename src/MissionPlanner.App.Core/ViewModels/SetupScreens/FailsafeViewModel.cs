using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>
/// FailSafe (ConfigFailSafe): live RC inputs and servo outputs, mode, armed state and GPS, with the
/// vehicle's radio, battery and GCS failsafe parameters. Edits write at once.
/// </summary>
public sealed partial class FailsafeViewModel : HardwarePageViewModel
{
    public FailsafeViewModel(AppServices services)
        : this(services, null)
    {
    }

    internal FailsafeViewModel(AppServices services, IParamSource? source)
        : base("FailSafe", services, source)
    {
        RcIn = Enumerable.Range(1, 8).Select(n => new PwmChannel(n, "Radio " + n.ToString(CultureInfo.InvariantCulture))).ToList();
        ServoOut = Enumerable.Range(1, 8).Select(n => new PwmChannel(n, "Servo " + n.ToString(CultureInfo.InvariantCulture))).ToList();
    }

    public IReadOnlyList<PwmChannel> RcIn { get; }

    public IReadOnlyList<PwmChannel> ServoOut { get; }

    [ObservableProperty]
    private IReadOnlyList<ParamGroup> _groups = [];

    [ObservableProperty]
    private string _currentMode = "";

    [ObservableProperty]
    private bool _armed;

    [ObservableProperty]
    private string _armedText = "Disarmed";

    [ObservableProperty]
    private string _gpsText = "";

    /// <summary>Throttle is below the failsafe PWM (Mission Planner shows the mode in red).</summary>
    [ObservableProperty]
    private bool _throttleFailsafe;

    protected override void Build()
    {
        var groups = new List<ParamGroup>();

        void Group(string title, params ParamField?[] fields)
        {
            var present = fields.OfType<ParamField>().ToList();
            if (present.Count > 0)
                groups.Add(new ParamGroup(title, present));
        }

        Group("Radio",
            Choice("FS_THR_ENABLE"),
            Number("FS_THR_VALUE", 800, 1200),
            Choice("THR_FAILSAFE"),
            Number("THR_FS_VALUE", 800, 1200),
            Choice("THR_FS_ACTION"),
            Choice("FS_ACTION"),
            Number("FS_TIMEOUT", 0, 100),
            Number("RC_FS_TIMEOUT", 0, 10, 0.1));

        Group("Battery",
            Choice(["BATT_FS_LOW_ACT", "FS_BATT_ENABLE"]),
            Number(["LOW_VOLT", "FS_BATT_VOLTAGE", "BATT_LOW_VOLT"], 6, 99, 0.1),
            Number(["FS_BATT_MAH", "BATT_LOW_MAH"], 0, 99999),
            Number("BATT_LOW_TIMER", 0, 120),
            Choice("BATT_FS_CRT_ACT"),
            Number("BATT_CRT_VOLT", 0, 99, 0.1),
            Number("BATT_CRT_MAH", 0, 99999),
            Choice("BATT_FS_VOLTSRC"));

        Group("GCS",
            Choice("FS_GCS_ENABLE"),
            Choice("FS_GCS_ENABL"),
            Number("FS_GCS_TIMEOUT", 0, 120),
            Choice("FS_SHORT_ACTN"),
            Number("FS_SHORT_TIMEOUT", 0, 100),
            Choice("FS_LONG_ACTN"),
            Number("FS_LONG_TIMEOUT", 0, 300));

        Group("Other",
            Choice("FS_EKF_ACTION"),
            Number("FS_EKF_THRESH", 0, 1, 0.01),
            Choice("FS_CRASH_CHECK"),
            Choice("FS_VIBE_ENABLE"),
            Choice("FS_DR_ENABLE"),
            Field("FS_OPTIONS"));

        Groups = groups;
        Status = groups.Count == 0 ? "This vehicle has no failsafe parameters." : null;
    }

    /// <summary>Documented choices as a drop-down; an undocumented on/off as a check box (as Mission Planner's MavlinkCheckBox).</summary>
    private ParamField? Choice(string name) => Choice([name]);

    private ParamField? Choice(string[] aliases)
    {
        var name = FirstExisting(aliases);
        if (name == null)
            return null;
        var documented = Source.Meta(name)?.Values.Count > 0;
        return Field(name, documented ? null : new ParamFieldSpec { Kind = ParamEditorKind.Toggle });
    }

    /// <summary>A number with the documented range, or Mission Planner's fixed range when undocumented.</summary>
    private ParamField? Number(string name, double min, double max, double? increment = null) => Number([name], min, max, increment);

    private ParamField? Number(string[] aliases, double min, double max, double? increment = null)
    {
        var name = FirstExisting(aliases);
        if (name == null)
            return null;
        var meta = Source.Meta(name);
        var documented = meta?.Min != null && meta.Max != null;
        return Field(name, new ParamFieldSpec
        {
            Kind = ParamEditorKind.Number,
            Min = documented ? null : min,
            Max = documented ? null : max,
            Increment = meta?.Increment == null ? increment : null,
        });
    }

    protected override void UpdateLive()
    {
        var cs = Services.Session.Cs;
        foreach (var c in RcIn)
            c.Set(Channels.In(cs, c.Number));
        foreach (var c in ServoOut)
            c.Set(Channels.Out(cs, c.Number));

        CurrentMode = cs.mode ?? "";
        Armed = cs.armed;
        ArmedText = cs.armed ? "Armed" : "Disarmed";
        GpsText = "GPS: " + Channels.GpsText(cs.gpsstatus);
        ThrottleFailsafe = Source.Get("FS_THR_VALUE") is { } fs && cs.ch3in < fs;
    }
}
