using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>
/// Optical Flow (ConfigHWOptFlow): sensor type, yaw orientation, scaling factors and position on
/// the frame (older firmware: one enable switch), with the live flow quality and rates.
/// </summary>
public sealed partial class OpticalFlowViewModel : HardwarePageViewModel
{
    private static readonly string[] Shown =
        ["FLOW_ENABLE", "FLOW_TYPE", "FLOW_ORIENT_YAW", "FLOW_FXSCALER", "FLOW_FYSCALER", "FLOW_POS_X", "FLOW_POS_Y", "FLOW_POS_Z", "FLOW_HGT_OVR"];

    public OpticalFlowViewModel(AppServices services)
        : this(services, null)
    {
    }

    internal OpticalFlowViewModel(AppServices services, IParamSource? source)
        : base("Optical Flow", services, source)
    {
    }

    /// <summary>FLOW_ENABLE: firmware before FLOW_TYPE had only this switch.</summary>
    [ObservableProperty]
    private ParamField? _enable;

    [ObservableProperty]
    private ParamField? _type;

    /// <summary>FLOW_ORIENT_YAW in degrees (stored in centidegrees).</summary>
    [ObservableProperty]
    private ScaledField? _yaw;

    [ObservableProperty]
    private ParamField? _fxScaler;

    [ObservableProperty]
    private ParamField? _fyScaler;

    [ObservableProperty]
    private ParamField? _posX;

    [ObservableProperty]
    private ParamField? _posY;

    [ObservableProperty]
    private ParamField? _posZ;

    /// <summary>FLOW_HGT_OVR, Rover only.</summary>
    [ObservableProperty]
    private ParamField? _heightOverride;

    [ObservableProperty]
    private IReadOnlyList<ParamField> _other = [];

    [ObservableProperty]
    private bool _isLegacy;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private int _quality;

    [ObservableProperty]
    private string _qualityText = "";

    [ObservableProperty]
    private string _flowText = "";

    [ObservableProperty]
    private string _bodyText = "";

    [ObservableProperty]
    private string _rangeText = "";

    protected override void Build()
    {
        Enable = Field("FLOW_ENABLE", new ParamFieldSpec { Kind = ParamEditorKind.Toggle, Label = "Enable" });
        IsLegacy = Enable != null && !Source.Has("FLOW_TYPE");
        Type = Field("FLOW_TYPE", new ParamFieldSpec { Kind = ParamEditorKind.Options, Label = "Type" });

        var yaw = Field("FLOW_ORIENT_YAW", new ParamFieldSpec { Kind = ParamEditorKind.Number });
        Yaw = yaw == null ? null : new ScaledField(yaw, 100, "deg", -179, 180, 1, "Yaw orientation");
        FxScaler = Field("FLOW_FXSCALER", new ParamFieldSpec { Kind = ParamEditorKind.Number, Label = "FX scale", Min = -200, Max = 200, Increment = 1 });
        FyScaler = Field("FLOW_FYSCALER", new ParamFieldSpec { Kind = ParamEditorKind.Number, Label = "FY scale", Min = -200, Max = 200, Increment = 1 });
        PosX = Field("FLOW_POS_X", new ParamFieldSpec { Kind = ParamEditorKind.Number, Label = "Position X (forward)", Min = -5, Max = 5, Increment = 0.01, Units = "m" });
        PosY = Field("FLOW_POS_Y", new ParamFieldSpec { Kind = ParamEditorKind.Number, Label = "Position Y (right)", Min = -5, Max = 5, Increment = 0.01, Units = "m" });
        PosZ = Field("FLOW_POS_Z", new ParamFieldSpec { Kind = ParamEditorKind.Number, Label = "Position Z (down)", Min = -5, Max = 5, Increment = 0.01, Units = "m" });
        // Mission Planner shows the height override on Rover only
        HeightOverride = Services.Session.Cs.firmware == ArduPilot.Firmwares.ArduRover
            ? Field("FLOW_HGT_OVR", new ParamFieldSpec { Kind = ParamEditorKind.Number, Label = "Height override", Min = 0, Max = 2, Increment = 0.01, Units = "m" })
            : null;

        Other = Source.Names()
            .Where(n => n.StartsWith("FLOW_", StringComparison.Ordinal) && !Shown.Contains(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => Field(n))
            .OfType<ParamField>()
            .ToList();

        if (Type != null)
            Type.Written += (_, e) =>
            {
                if (e.OldValue != e.NewValue)
                    RebootRequired = true;
                OnValuesRefreshed();
            };

        Status = Enable == null && Type == null ? "This vehicle has no FLOW_TYPE parameter." : null;
        OnValuesRefreshed();
    }

    protected override void OnValuesRefreshed()
    {
        IsEnabled = IsLegacy ? Enable?.Value is > 0 : Type?.Value is > 0;
    }

    protected override void UpdateLive()
    {
        var cs = Services.Session.Cs;
        Quality = cs.opt_qua;
        QualityText = cs.opt_qua.ToString(CultureInfo.InvariantCulture) + " / 255";
        FlowText = string.Create(CultureInfo.InvariantCulture, $"{cs.opt_x}, {cs.opt_y}");
        BodyText = string.Create(CultureInfo.InvariantCulture, $"{cs.opt_m_x:0.00}, {cs.opt_m_y:0.00}");
        var unit = CurrentState.AltUnit is { Length: > 0 } u ? u : "m";
        RangeText = cs.sonarrange.ToString("0.00", CultureInfo.InvariantCulture) + " " + unit;
    }
}
