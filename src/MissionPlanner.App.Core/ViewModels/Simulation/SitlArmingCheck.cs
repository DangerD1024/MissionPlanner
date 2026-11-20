namespace MissionPlanner.App.Core.ViewModels.Simulation;

/// <summary>
/// Notices a simulated vehicle that will never arm. A default simulator is ready to arm within about
/// half a minute; one that still reports pre-arm failures well after its GPS has a fix is held back
/// by its saved parameters (typically a real aircraft's parameter file loaded into the simulator:
/// sensors, relays or outputs the simulator does not have).
/// </summary>
public static class SitlArmingCheck
{
    /// <summary>How long a simulator gets to become ready before it counts as stuck.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(45);

    /// <summary>Failures every simulator shows while it starts up; they clear by themselves.</summary>
    private static readonly string[] Transient =
    [
        "waiting for", "gps", "bad fix", "settling", "initialis", "initializ", "not started", "ekf", "ahrs",
        "calibrating", "home", "no fix", "position estimate", "gyro", "accels inconsistent",
    ];

    /// <summary>Pre-arm failures that keep the vehicle from arming; empty while it is fine or still starting.</summary>
    public static IReadOnlyList<string> Blockers(TimeSpan connectedFor, bool readyToArm, float gpsFix,
        IEnumerable<(DateTime Time, string Message)> messages, DateTime now)
    {
        if (readyToArm || connectedFor < Grace || gpsFix < 3)
            return [];

        return messages
            // everything reported since the connection: the autopilot names each failure only every
            // 30 s and not always all of them at once
            .Where(m => now - m.Time <= connectedFor)
            .Select(m => m.Message.Trim())
            .Where(IsPreArm)
            .Select(Reason)
            .Where(r => r.Length > 0 && !Transient.Any(t => r.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsPreArm(string message) =>
        message.StartsWith("PreArm:", StringComparison.OrdinalIgnoreCase) ||
        message.StartsWith("Arm:", StringComparison.OrdinalIgnoreCase);

    /// <summary>"PreArm: Airspeed: 1 need pre-flight calibration" → "Airspeed: 1 need pre-flight calibration".</summary>
    private static string Reason(string message) => message[(message.IndexOf(':') + 1)..].Trim();

    /// <summary>Copies the simulator's saved state next to it before it is wiped; returns the copy, or null if there was none.</summary>
    public static string? BackUpState(string workingDirectory, DateTime now)
    {
        var eeprom = Path.Combine(workingDirectory, "eeprom.bin");
        if (!File.Exists(eeprom))
            return null;
        var backup = Path.Combine(workingDirectory, $"eeprom-{now:yyyyMMdd-HHmmss}.bin");
        File.Copy(eeprom, backup, overwrite: true);
        return backup;
    }
}
