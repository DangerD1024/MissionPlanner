using System.Globalization;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.MotorSetup;

/// <summary>SERVOn_FUNCTION values of the motor outputs: Motor1..8 = 33..40, Motor9..12 = 82..85.</summary>
public static class MotorFunctions
{
    public const int MaxMotors = 12;

    /// <summary>The SERVOn_FUNCTION value that drives motor <paramref name="motor"/> (1-12).</summary>
    public static int FunctionOf(int motor) => motor switch
    {
        >= 1 and <= 8 => 32 + motor,
        >= 9 and <= 12 => 73 + motor,
        _ => throw new ArgumentOutOfRangeException(nameof(motor), motor, "ArduPilot has motor functions for motors 1 to 12"),
    };

    /// <summary>The motor a SERVOn_FUNCTION value drives, or null if it is not a motor function.</summary>
    public static int? MotorOf(double function)
    {
        if (!double.IsFinite(function))
            return null;
        var f = (int)Math.Round(function);
        return f switch
        {
            >= 33 and <= 40 => f - 32,
            >= 82 and <= 85 => f - 73,
            _ => null,
        };
    }

    public static string FunctionParam(int output) => "SERVO" + output.ToString(CultureInfo.InvariantCulture) + "_FUNCTION";
}

/// <summary>An output whose SERVOn_FUNCTION is a motor function.</summary>
public sealed record MotorOutput(int Output, int Motor);

/// <summary>What the motor order wizard can work with, and why not when it cannot.</summary>
public sealed record MotorOutputCheck(
    IReadOnlyList<MotorOutput> Outputs,
    IReadOnlyList<MotorOutput> Ignored,
    IReadOnlyList<string> Problems)
{
    /// <summary>Every motor of the frame is on exactly one output.</summary>
    public bool CanRunWizard => Problems.Count == 0 && Outputs.Count > 0;
}

/// <summary>Reads which outputs carry which motor and checks them against the frame's layout.</summary>
public static class MotorOutputs
{
    /// <summary>16 outputs, or 32 with SERVO_32_ENABLE (as the Servo Output page).</summary>
    public static int OutputCount(Func<string, double?> param) => param("SERVO_32_ENABLE") is > 0 ? 32 : 16;

    /// <summary>All outputs with a motor function, in output order.</summary>
    public static IReadOnlyList<MotorOutput> Read(Func<string, double?> param)
    {
        var list = new List<MotorOutput>();
        var count = OutputCount(param);
        for (var n = 1; n <= count; n++)
        {
            if (param(MotorFunctions.FunctionParam(n)) is { } f && MotorFunctions.MotorOf(f) is { } motor)
                list.Add(new MotorOutput(n, motor));
        }

        return list;
    }

    /// <summary>The outputs that drive a motor (normally one).</summary>
    public static IReadOnlyList<int> OutputsOf(IEnumerable<MotorOutput> outputs, int motor) =>
        outputs.Where(o => o.Motor == motor).Select(o => o.Output).OrderBy(o => o).ToList();

    /// <summary>
    /// The outputs the wizard steps through (those driving a motor of the frame) and what stops it:
    /// a motor on two outputs (the test would spin both), or a motor of the frame on none.
    /// Outputs with a motor the frame does not use are ignored: a motor test never drives them.
    /// </summary>
    public static MotorOutputCheck Check(MotorLayout layout, IReadOnlyList<MotorOutput> outputs)
    {
        var frameMotors = layout.Motors.Select(m => m.Number).ToHashSet();
        var used = outputs.Where(o => frameMotors.Contains(o.Motor)).OrderBy(o => o.Output).ToList();
        var ignored = outputs.Where(o => !frameMotors.Contains(o.Motor)).OrderBy(o => o.Output).ToList();
        var problems = new List<string>();

        foreach (var g in used.GroupBy(o => o.Motor).Where(g => g.Count() > 1).OrderBy(g => g.Key))
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture,
                $"Motor {g.Key} is on outputs {Join(g.Select(o => o.Output))}: a motor test would spin them together. Give each output its own motor first."));
        }

        foreach (var m in frameMotors.Order().Where(m => used.All(o => o.Motor != m)))
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture,
                $"Motor {m} of this frame is not assigned to any output. Set one SERVOn_FUNCTION to Motor{m} first."));
        }

        return new MotorOutputCheck(used, ignored, problems);
    }

    public static string Join(IEnumerable<int> values)
    {
        var list = values.Select(v => v.ToString(CultureInfo.InvariantCulture)).ToList();
        return list.Count switch
        {
            0 => "",
            1 => list[0],
            _ => string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1],
        };
    }
}
