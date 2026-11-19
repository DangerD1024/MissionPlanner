using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.MotorSetup;

public enum MotorMarkKind
{
    Normal,

    /// <summary>This motor is spinning now.</summary>
    Spinning,

    /// <summary>The wizard has placed an output here.</summary>
    Placed,

    /// <summary>Not yet placed: a position the user may still click.</summary>
    Open,
}

/// <summary>How one motor of the diagram is decorated.</summary>
public sealed record MotorMark(int Motor, string? Caption, MotorMarkKind Kind = MotorMarkKind.Normal, bool Reversed = false, string? Badge = null);

/// <summary>Everything the motor diagram draws, replaced as a whole when anything changes.</summary>
public sealed record MotorDiagramState(
    MotorLayout? Layout,
    IReadOnlyDictionary<int, MotorMark> Marks,
    bool Clickable = false,
    string? Banner = null)
{
    public static MotorDiagramState Empty { get; } = new(null, new Dictionary<int, MotorMark>());

    public MotorMark? MarkOf(int motor) => Marks.TryGetValue(motor, out var m) ? m : null;
}
