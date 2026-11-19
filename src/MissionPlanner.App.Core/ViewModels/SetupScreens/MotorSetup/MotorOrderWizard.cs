using System.Globalization;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.MotorSetup;

/// <summary>One output of the result: the motor it drove and the motor it will drive.</summary>
public sealed record MotorAssignment(int Output, int OldMotor, int NewMotor)
{
    public bool Changed => OldMotor != NewMotor;

    public int NewFunction => MotorFunctions.FunctionOf(NewMotor);
}

/// <summary>
/// The motor order the wizard found: for each output, the frame position (motor number of the
/// layout) where the motor it spins physically sits. Valid only as a permutation of the frame's
/// motors, each on exactly one output.
/// </summary>
public sealed class MotorMapping
{
    public MotorMapping(IReadOnlyList<MotorAssignment> rows, IEnumerable<int> frameMotors)
    {
        Rows = rows.OrderBy(r => r.Output).ToList();
        var motors = frameMotors.ToHashSet();
        Problem = Validate(Rows, motors);
    }

    public IReadOnlyList<MotorAssignment> Rows { get; }

    /// <summary>Why the mapping cannot be written, or null.</summary>
    public string? Problem { get; }

    /// <summary>Every frame motor is on exactly one output and every output drives one frame motor.</summary>
    public bool IsPermutation => Problem == null;

    /// <summary>Nothing would change: the motors are already in the right order.</summary>
    public bool IsIdentity => Rows.All(r => !r.Changed);

    /// <summary>SERVOn_FUNCTION writes for the outputs that change, in output order.</summary>
    public IReadOnlyList<KeyValuePair<string, double>> Writes() =>
        Rows.Where(r => r.Changed)
            .Select(r => new KeyValuePair<string, double>(MotorFunctions.FunctionParam(r.Output), r.NewFunction))
            .ToList();

    private static string? Validate(IReadOnlyList<MotorAssignment> rows, HashSet<int> motors)
    {
        if (rows.Count == 0)
            return "No outputs were identified.";
        var dupOutput = rows.GroupBy(r => r.Output).FirstOrDefault(g => g.Count() > 1);
        if (dupOutput != null)
            return string.Create(CultureInfo.InvariantCulture, $"Output {dupOutput.Key} appears twice.");
        var dupMotor = rows.GroupBy(r => r.NewMotor).FirstOrDefault(g => g.Count() > 1);
        if (dupMotor != null)
            return string.Create(CultureInfo.InvariantCulture,
                $"Position of motor {dupMotor.Key} was chosen for outputs {MotorOutputs.Join(dupMotor.Select(r => r.Output))}.");
        var foreign = rows.FirstOrDefault(r => !motors.Contains(r.NewMotor));
        if (foreign != null)
            return string.Create(CultureInfo.InvariantCulture, $"Motor {foreign.NewMotor} is not part of this frame.");
        var missing = motors.Order().Where(m => rows.All(r => r.NewMotor != m)).ToList();
        if (missing.Count > 0)
            return "No output was given the position of motor " + MotorOutputs.Join(missing) + ".";
        return null;
    }
}

public enum MotorOrderStage
{
    /// <summary>Waiting for the safety confirmation (propellers off, disarmed).</summary>
    Safety,

    /// <summary>One output spins; the user clicks where it is on the frame.</summary>
    Identify,

    /// <summary>All outputs placed: the mapping is shown for confirmation.</summary>
    Review,

    /// <summary>The mapping was written (or nothing needed changing).</summary>
    Done,

    Cancelled,
}

public enum MotorAssignResult
{
    /// <summary>Recorded; the next output is up.</summary>
    Next,

    /// <summary>Recorded; that was the last output.</summary>
    Finished,

    /// <summary>Another output was already placed there.</summary>
    PositionTaken,

    /// <summary>No motor of the frame has that number.</summary>
    UnknownPosition,

    /// <summary>The wizard is not identifying outputs.</summary>
    NotIdentifying,
}

/// <summary>What the safety gate knows before the first motor spins.</summary>
public readonly record struct MotorSafety(bool Connected, bool PropellersRemoved, bool Armed);

/// <summary>
/// Betaflight's motor remapping for ArduPilot: each output with a motor of the frame spins in turn
/// (output order), the user clicks the frame position where it physically is, and the result gives
/// that output the motor function of that position. Pure state: the view-model spins the motors.
/// </summary>
public sealed class MotorOrderWizard
{
    private readonly Dictionary<int, int> _placed = new();

    public MotorOrderWizard(MotorLayout layout, IReadOnlyList<MotorOutput> outputs)
    {
        var check = MotorOutputs.Check(layout, outputs);
        if (!check.CanRunWizard)
            throw new ArgumentException(check.Problems.FirstOrDefault() ?? "No motor outputs.", nameof(outputs));
        Layout = layout;
        Outputs = check.Outputs;
    }

    public MotorLayout Layout { get; }

    /// <summary>The outputs in the order they are tested.</summary>
    public IReadOnlyList<MotorOutput> Outputs { get; }

    public MotorOrderStage Stage { get; private set; } = MotorOrderStage.Safety;

    /// <summary>Index into <see cref="Outputs"/> of the output being identified.</summary>
    public int Step { get; private set; }

    public int StepCount => Outputs.Count;

    public MotorOutput? Current => Stage == MotorOrderStage.Identify ? Outputs[Step] : null;

    /// <summary>
    /// DO_MOTOR_TEST param1 that spins the current output. ArduCopter's param1 is the test
    /// sequence number (AP_MotorsMatrix::output_test_seq spins the motor whose test order matches),
    /// so the output's motor number is translated through the layout.
    /// </summary>
    public int? CurrentTestOrder => Current is { } c ? TestOrderOf(c.Motor) : null;

    /// <summary>Output → frame position (motor number) chosen so far.</summary>
    public IReadOnlyDictionary<int, int> Placed => _placed;

    public int TestOrderOf(int motor) => Layout.Motors.First(m => m.Number == motor).TestOrder;

    /// <summary>The output already placed at a position, if any.</summary>
    public int? OutputAt(int positionMotor)
    {
        foreach (var (output, position) in _placed)
        {
            if (position == positionMotor)
                return output;
        }

        return null;
    }

    /// <summary>The safety gate; null when the wizard may start, otherwise why not.</summary>
    public static string? SafetyProblem(MotorSafety safety)
    {
        if (!safety.Connected)
            return "Connect to the vehicle first.";
        if (!safety.PropellersRemoved)
            return "Remove all propellers and tick the box to confirm.";
        if (safety.Armed)
            return "Disarm the vehicle first.";
        return null;
    }

    /// <summary>Passes the safety gate and starts with the first output. Returns why not, or null.</summary>
    public string? Begin(MotorSafety safety)
    {
        if (Stage != MotorOrderStage.Safety)
            return "The wizard has already started.";
        var problem = SafetyProblem(safety);
        if (problem != null)
            return problem;
        _placed.Clear();
        Step = 0;
        Stage = MotorOrderStage.Identify;
        return null;
    }

    /// <summary>The user clicked the frame position (layout motor number) where the spinning motor is.</summary>
    public MotorAssignResult Place(int positionMotor)
    {
        if (Stage != MotorOrderStage.Identify)
            return MotorAssignResult.NotIdentifying;
        if (Layout.Motors.All(m => m.Number != positionMotor))
            return MotorAssignResult.UnknownPosition;
        if (OutputAt(positionMotor) is not null)
            return MotorAssignResult.PositionTaken;

        _placed[Outputs[Step].Output] = positionMotor;
        if (Step + 1 < Outputs.Count)
        {
            Step++;
            return MotorAssignResult.Next;
        }

        Stage = MotorOrderStage.Review;
        return MotorAssignResult.Finished;
    }

    /// <summary>Goes back one output (from the review, back to the last one). False at the first.</summary>
    public bool Back()
    {
        if (Stage == MotorOrderStage.Review)
        {
            Stage = MotorOrderStage.Identify;
            _placed.Remove(Outputs[Step].Output);
            return true;
        }

        if (Stage != MotorOrderStage.Identify || Step == 0)
            return false;
        Step--;
        _placed.Remove(Outputs[Step].Output);
        return true;
    }

    /// <summary>Starts over at the safety gate.</summary>
    public void Restart()
    {
        _placed.Clear();
        Step = 0;
        Stage = MotorOrderStage.Safety;
    }

    public void Cancel()
    {
        if (Stage != MotorOrderStage.Done)
            Stage = MotorOrderStage.Cancelled;
    }

    /// <summary>The result so far (complete in <see cref="MotorOrderStage.Review"/>).</summary>
    public MotorMapping Mapping() => new(
        Outputs.Where(o => _placed.ContainsKey(o.Output)).Select(o => new MotorAssignment(o.Output, o.Motor, _placed[o.Output])).ToList(),
        Layout.Motors.Select(m => m.Number));

    /// <summary>The mapping was written (or needed no change).</summary>
    public void Complete()
    {
        if (Stage == MotorOrderStage.Review)
            Stage = MotorOrderStage.Done;
    }
}
