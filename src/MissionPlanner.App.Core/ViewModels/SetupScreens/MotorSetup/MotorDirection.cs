using System.Globalization;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.MotorSetup;

public enum DirectionSupport
{
    /// <summary>DShot with an ESC type: SERVO_BLH_RVMASK reverses motors (after a reboot).</summary>
    Supported,

    /// <summary>DShot output, but SERVO_DSHOT_ESC is None, so no DShot commands are sent.</summary>
    EscTypeNone,

    /// <summary>PWM, OneShot or another protocol without DShot commands.</summary>
    NotDshot,

    /// <summary>The firmware has no SERVO_BLH_RVMASK (or no ESC protocol parameter).</summary>
    NoReverseParam,
}

/// <summary>Whether and how motor direction can be set in software on this vehicle.</summary>
public sealed record MotorDirectionStatus(
    DirectionSupport Support,
    string? PwmTypeParam,
    int? PwmType,
    int EscType,
    uint ReverseMask,
    uint ThreeDMask,
    uint BidirMask,
    string Message,
    string? ThreeDWarning)
{
    public bool CanReverse => Support == DirectionSupport.Supported;

    /// <summary>MOT_PWM_TYPE is DShot already: setting SERVO_DSHOT_ESC is all that is missing.</summary>
    public bool CanOfferEscType => Support == DirectionSupport.EscTypeNone;

    /// <summary>Bidirectional DShot on at least one motor output: RPM telemetry can confirm a spin.</summary>
    public bool HasRpmTelemetry => BidirMask != 0;
}

/// <summary>
/// Motor direction in ArduPilot: SERVO_BLH_RVMASK is a mask of reversed output channels (bit 0 =
/// output 1). ArduPilot sends the DShot spin-direction command to those outputs when it starts, so
/// it needs DShot output (MOT_PWM_TYPE / Q_M_PWM_TYPE 4-7) and an ESC type (SERVO_DSHOT_ESC ≠ 0;
/// "None means that no dshot commands will be executed"), and takes effect after a reboot.
/// </summary>
public static class MotorDirection
{
    public const string ReverseMaskParam = "SERVO_BLH_RVMASK";
    public const string ThreeDMaskParam = "SERVO_BLH_3DMASK";
    public const string BidirMaskParam = "SERVO_BLH_BDMASK";
    public const string EscTypeParam = "SERVO_DSHOT_ESC";

    /// <summary>SERVO_DSHOT_ESC values that send DShot commands.</summary>
    public static IReadOnlyList<(int Value, string Label)> EscTypes { get; } =
    [
        (1, "BLHeli32 / Kiss / AM32"),
        (2, "BLHeli_S / BlueJay"),
        (3, "BLHeli32 / AM32 / Kiss + EDT"),
        (4, "BLHeli_S / BlueJay + EDT"),
    ];

    public static bool IsDshot(int pwmType) => pwmType is >= 4 and <= 7;

    public static string PwmTypeName(int pwmType) => pwmType switch
    {
        0 => "Normal PWM",
        1 => "OneShot",
        2 => "OneShot125",
        3 => "Brushed",
        4 => "DShot150",
        5 => "DShot300",
        6 => "DShot600",
        7 => "DShot1200",
        8 => "PWMRange",
        9 => "PWMAngle",
        _ => "type " + pwmType.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>The bit of an output (1-32) in the BLHeli channel masks.</summary>
    public static uint Bit(int output) =>
        output is >= 1 and <= 32 ? 1u << (output - 1) : throw new ArgumentOutOfRangeException(nameof(output), output, "outputs are 1 to 32");

    public static uint MaskOf(IEnumerable<int> outputs) => outputs.Aggregate(0u, (m, o) => m | Bit(o));

    public static bool IsReversed(uint mask, int output) => (mask & Bit(output)) != 0;

    /// <summary>All of a motor's outputs reversed (a motor on two outputs counts as reversed only if both are).</summary>
    public static bool IsReversed(uint mask, IReadOnlyCollection<int> outputs) => outputs.Count > 0 && outputs.All(o => IsReversed(mask, o));

    public static uint WithReversed(uint mask, IEnumerable<int> outputs, bool reversed)
    {
        var bits = MaskOf(outputs);
        return reversed ? mask | bits : mask & ~bits;
    }

    public static IReadOnlyList<int> Outputs(uint mask) =>
        Enumerable.Range(1, 32).Where(o => IsReversed(mask, o)).ToList();

    /// <summary>A mask parameter's value (AP_Int32: bit 31 makes it negative) as bits.</summary>
    public static uint FromParam(double? value) =>
        value is { } v && double.IsFinite(v) ? unchecked((uint)(long)Math.Round(v)) : 0u;

    /// <summary>The value to write for a mask: the signed 32-bit number the autopilot stores.</summary>
    public static double ToParam(uint mask) => unchecked((int)mask);

    /// <summary>
    /// Evaluates the direction settings. <paramref name="quadPlane"/> picks Q_M_PWM_TYPE over
    /// MOT_PWM_TYPE; <paramref name="motorOutputs"/> are the outputs that carry motors (for the 3D
    /// warning and the RPM note).
    /// </summary>
    public static MotorDirectionStatus Evaluate(Func<string, double?> param, bool quadPlane, IReadOnlyCollection<int> motorOutputs)
    {
        var pwmParam = quadPlane
            ? (param("Q_M_PWM_TYPE") != null ? "Q_M_PWM_TYPE" : param("MOT_PWM_TYPE") != null ? "MOT_PWM_TYPE" : null)
            : (param("MOT_PWM_TYPE") != null ? "MOT_PWM_TYPE" : param("Q_M_PWM_TYPE") != null ? "Q_M_PWM_TYPE" : null);
        int? pwmType = pwmParam != null && param(pwmParam) is { } p ? (int)Math.Round(p) : null;
        var escType = param(EscTypeParam) is { } e ? (int)Math.Round(e) : 0;
        var hasRv = param(ReverseMaskParam) != null;
        var reverse = FromParam(param(ReverseMaskParam));
        var threeD = FromParam(param(ThreeDMaskParam));
        var motorMask = MaskOf(motorOutputs.Where(o => o is >= 1 and <= 32));
        var bidir = FromParam(param(BidirMaskParam)) & (motorMask == 0 ? uint.MaxValue : motorMask);

        DirectionSupport support;
        string message;
        const string swap = "Swap any two of the three wires between that motor and its ESC";
        if (!hasRv || pwmType == null)
        {
            support = DirectionSupport.NoReverseParam;
            message = "This firmware has no " + (hasRv ? "ESC protocol parameter" : ReverseMaskParam) +
                      ", so motor direction cannot be set in software. " + swap + " to reverse it.";
        }
        else if (!IsDshot(pwmType.Value))
        {
            support = DirectionSupport.NotDshot;
            message = $"The ESCs use {PwmTypeName(pwmType.Value)} ({pwmParam} = {pwmType.Value}), which has no direction command, " +
                      $"so direction cannot be set in software for this setup. {swap}, or switch to DShot " +
                      $"({pwmParam} = DShot150-1200) if your ESCs support it and then set {EscTypeParam}.";
        }
        else if (escType == 0)
        {
            support = DirectionSupport.EscTypeNone;
            message = $"The ESCs use {PwmTypeName(pwmType.Value)}, but {EscTypeParam} is None: \"no dshot commands will be executed\", " +
                      $"so the reverse setting would be ignored. Choose your ESC firmware below to allow it (applies after a reboot).";
        }
        else
        {
            support = DirectionSupport.Supported;
            message = $"{PwmTypeName(pwmType.Value)} with {EscName(escType)} ESCs: \"Reverse\" sets the output in {ReverseMaskParam}. " +
                      "ArduPilot sends the direction command to the ESCs when it starts, so reboot the autopilot after a change and check again.";
        }

        string? warning = null;
        var threeDMotors = motorMask == 0 ? threeD : threeD & motorMask;
        if (threeDMotors != 0)
        {
            warning = $"{ThreeDMaskParam} puts output{(Outputs(threeDMotors).Count == 1 ? "" : "s")} {MotorOutputs.Join(Outputs(threeDMotors))} in 3D (reversible) mode. " +
                      $"With 3D mode, {ReverseMaskParam} changes which direction is considered forward, and on AM32 enabling 3D mode itself " +
                      "changes the forward direction. Multirotor motors are normally not 3D: check each motor again after a reboot.";
        }

        return new MotorDirectionStatus(support, pwmParam, pwmType, escType, reverse, threeD, bidir, message, warning);
    }

    public static string EscName(int escType) =>
        EscTypes.FirstOrDefault(t => t.Value == escType).Label ?? "type " + escType.ToString(CultureInfo.InvariantCulture);
}
