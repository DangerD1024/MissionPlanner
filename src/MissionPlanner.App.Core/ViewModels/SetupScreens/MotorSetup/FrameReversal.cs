using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.MotorSetup;

/// <summary>
/// Betaflight's "motor direction is reversed" for ArduPilot. ArduPilot has no such switch: the yaw
/// mixing follows FRAME_TYPE, so reversing every propeller means switching to the frame type whose
/// motors sit at the same positions with the same numbers but every rotation flipped. The pairs
/// are found in the layout catalogue (APMotorLayout.json), not hard-coded.
/// </summary>
public static class FrameReversal
{
    private const double Tolerance = 1e-3;

    /// <summary>Same motor numbers at the same positions, every rotation known and flipped.</summary>
    public static bool IsMirror(MotorLayout a, MotorLayout b)
    {
        if (a.Motors.Count == 0 || a.Motors.Count != b.Motors.Count)
            return false;
        var other = b.Motors.GroupBy(m => m.Number).ToDictionary(g => g.Key, g => g.First());
        if (other.Count != b.Motors.Count)
            return false;
        foreach (var m in a.Motors)
        {
            if (!other.TryGetValue(m.Number, out var o) || !m.HasRotation || !o.HasRotation)
                return false;
            if (Math.Abs(m.Roll - o.Roll) > Tolerance || Math.Abs(m.Pitch - o.Pitch) > Tolerance)
                return false;
            if (m.IsClockwise == o.IsClockwise)
                return false;
        }

        return true;
    }

    /// <summary>Every frame type of the class whose layout mirrors this one (specific layouts only).</summary>
    public static IReadOnlyList<int> Candidates(MotorLayoutCatalog layouts, int frameClass, int frameType)
    {
        var own = layouts.Find(frameClass, frameType);
        if (own == null || own.IsDefaultType)
            return [];
        return layouts.ForClass(frameClass)
            .Where(l => !l.IsDefaultType && l.Type != frameType && IsMirror(own, l))
            .Select(l => l.Type)
            .Distinct()
            .Order()
            .ToList();
    }

    /// <summary>
    /// The reversed frame type to switch to, or null. Where several types share a layout (Quad V
    /// draws exactly like X), the lowest-numbered candidate is the partner, and the switch is only
    /// offered when the pairing is mutual: X ↔ H, but not V → H (H goes back to X).
    /// </summary>
    public static int? Partner(MotorLayoutCatalog layouts, int frameClass, int frameType)
    {
        var mine = Candidates(layouts, frameClass, frameType);
        if (mine.Count == 0)
            return null;
        var partner = mine[0];
        var theirs = Candidates(layouts, frameClass, partner);
        return theirs.Count > 0 && theirs[0] == frameType ? partner : null;
    }

    /// <summary>All mutual pairs of a class as (normal, reversed): the lower type number is the normal one.</summary>
    public static IReadOnlyList<(int Normal, int Reversed)> Pairs(MotorLayoutCatalog layouts, int frameClass)
    {
        var pairs = new SortedSet<(int, int)>();
        foreach (var l in layouts.ForClass(frameClass).Where(l => !l.IsDefaultType))
        {
            if (Partner(layouts, frameClass, l.Type) is { } p)
                pairs.Add((Math.Min(l.Type, p), Math.Max(l.Type, p)));
        }

        return pairs.ToList();
    }

    /// <summary>The current type is the reversed member of its pair (Plus Rev, H, BetaFlight X Reversed).</summary>
    public static bool IsReversedMember(MotorLayoutCatalog layouts, int frameClass, int frameType) =>
        Partner(layouts, frameClass, frameType) is { } p && frameType > p;
}
