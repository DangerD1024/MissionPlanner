using CommunityToolkit.Mvvm.ComponentModel;
using log4net;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>
/// Parachute (ConfigHWParachute): enable, release type (relay or servo), the servo output and its
/// resting / deploy PWM, and the minimum altitude. Edits write at once.
/// </summary>
public sealed partial class ParachuteViewModel : HardwarePageViewModel
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(ParachuteViewModel));

    /// <summary>CHUTE_TYPE value for a servo release.</summary>
    public const double ServoType = 10;

    /// <summary>Mission Planner's CHUTE_TYPE choices, used when the documentation lists none.</summary>
    internal static readonly IReadOnlyList<ParamOption> LegacyTypes =
    [
        new(0, "First Relay"), new(1, "Second Relay"), new(2, "Third Relay"), new(3, "Fourth Relay"), new(10, "Servo"),
    ];

    private static readonly string[] Shown =
        ["CHUTE_ENABLED", "CHUTE_TYPE", "CHUTE_SERVO_OFF", "CHUTE_SERVO_ON", "CHUTE_ALT_MIN"];

    public ParachuteViewModel(AppServices services)
        : this(services, null)
    {
    }

    internal ParachuteViewModel(AppServices services, IParamSource? source)
        : base("Parachute", services, source)
    {
    }

    [ObservableProperty]
    private ParamField? _enabled;

    [ObservableProperty]
    private ParamField? _type;

    [ObservableProperty]
    private ParamField? _servoOff;

    [ObservableProperty]
    private ParamField? _servoOn;

    [ObservableProperty]
    private ParamField? _altMin;

    [ObservableProperty]
    private IReadOnlyList<ParamField> _other = [];

    /// <summary>Which servo output releases the chute (SERVOn_FUNCTION = 27).</summary>
    [ObservableProperty]
    private ChannelPicker? _output;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private bool _isServo;

    protected override void Build()
    {
        Enabled = Field("CHUTE_ENABLED", new ParamFieldSpec { Kind = ParamEditorKind.Toggle, Label = "Enable" });
        Type = Field("CHUTE_TYPE", new ParamFieldSpec
        {
            Kind = ParamEditorKind.Options,
            Label = "Type",
            Options = Source.Meta("CHUTE_TYPE") is { Values.Count: > 0 } ? null : LegacyTypes,
        });
        ServoOff = Field("CHUTE_SERVO_OFF", new ParamFieldSpec { Kind = ParamEditorKind.Number, Label = "Resting PWM", Min = 1000, Max = 2000, Increment = 1 });
        ServoOn = Field("CHUTE_SERVO_ON", new ParamFieldSpec { Kind = ParamEditorKind.Number, Label = "Deploy PWM", Min = 1000, Max = 2000, Increment = 1 });
        AltMin = Field("CHUTE_ALT_MIN", new ParamFieldSpec { Kind = ParamEditorKind.Number, Label = "Min altitude", Min = 0, Max = 32000, Increment = 1, Units = "m" });
        Other = Source.Names()
            .Where(n => n.StartsWith("CHUTE_", StringComparison.Ordinal) && !Shown.Contains(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => Field(n))
            .OfType<ParamField>()
            .ToList();

        var outputs = ChannelAssignment.ServoOutputs(Source);
        Output = outputs.Count == 0
            ? null
            : new ChannelPicker("Servo output", "_FUNCTION", ChannelAssignment.Parachute, outputs, ApplyOutputAsync, "None");

        if (Enabled != null)
            Enabled.Written += OnEnabledWritten;
        if (Type != null)
            Type.Written += (_, _) => OnValuesRefreshed();

        Status = Enabled == null ? "This vehicle has no CHUTE_ENABLED parameter." : null;
        OnValuesRefreshed();
    }

    protected override void OnValuesRefreshed()
    {
        IsEnabled = Enabled?.Value is > 0;
        IsServo = Type?.Value == ServoType;
        Output?.Sync(Source);
    }

    private async Task ApplyOutputAsync(string? output)
    {
        if (Output == null)
            return;
        foreach (var (name, value) in ChannelAssignment.Plan(Source, Output.Channels, "_FUNCTION", ChannelAssignment.Parachute, output))
        {
            if (!await WriteAsync(name, value))
                break;
        }

        OnValuesRefreshed();
    }

    // enabling the parachute adds its other CHUTE_ parameters
    private async void OnEnabledWritten(object? sender, ParamWrittenEventArgs e)
    {
        OnValuesRefreshed();
        if (e.OldValue is not 0 || e.NewValue == 0 || Source.Has("CHUTE_TYPE") || !Services.Session.IsConnected)
            return;
        try
        {
            IsBusy = true;
            Status = "Getting parameters…";
            await Services.Params.RefreshAsync();
            Status = null;
        }
        catch (Exception ex)
        {
            Log.Warn("parachute refresh", ex);
            Status = "Could not refresh parameters: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
