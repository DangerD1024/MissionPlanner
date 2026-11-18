namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>A queued value more than twice the vehicle's.</summary>
public sealed record TuningLargeChange(string Name, double Old, double New);

/// <summary>
/// Writing from the tuning pages: Mission Planner's tuning pages first ask about any value that
/// "has more than doubled the last input" and stop if the user says no; then the usual
/// confirm-and-write of the parameter pages.
/// </summary>
public static class TuningWriteFlow
{
    /// <summary>
    /// The doubling rule of ConfigArducopter/ConfigArduplane (new &gt; 2 × old). Only increases to a
    /// positive value count, so a value growing more negative is not reported.
    /// </summary>
    public static bool MoreThanDoubled(double old, double value) => value > 0 && value > old * 2 && !ParamValues.Same(value, old * 2);

    /// <summary>
    /// The pending values that more than doubled, in write order. <paramref name="skip"/> leaves out
    /// parameters where doubling means nothing (enumerations, bitmasks).
    /// </summary>
    public static IReadOnlyList<TuningLargeChange> LargeChanges(IReadOnlyDictionary<string, double> pending, Func<string, double?> current,
        Func<string, bool>? skip = null) =>
        ParamEditBuffer.WriteOrder(pending.Keys)
            .Where(n => skip?.Invoke(n) != true)
            .Select(n => (Name: n, Old: current(n), New: pending[n]))
            .Where(x => x.Old is { } o && MoreThanDoubled(o, x.New))
            .Select(x => new TuningLargeChange(x.Name, x.Old!.Value, x.New))
            .ToList();

    public static string LargeChangeText(IReadOnlyList<TuningLargeChange> changes)
    {
        if (changes.Count == 1)
        {
            var c = changes[0];
            return $"{c.Name} has more than doubled the last input ({ParamValues.Format(c.Old)} -> {ParamValues.Format(c.New)}). Are you sure?\n\n" +
                   "Cancel puts it back to the vehicle's value and writes nothing.";
        }

        return "These values have more than doubled the last input:\n\n" +
               string.Join("\n", changes.Select(c => $"{c.Name}: {ParamValues.Format(c.Old)} -> {ParamValues.Format(c.New)}")) +
               "\n\nAre you sure? Cancel puts them back to the vehicle's values and writes nothing.";
    }

    /// <summary>
    /// Asks about large changes (declining drops those edits and writes nothing), then confirms and
    /// writes every pending edit through <see cref="ParamWriteFlow"/>. Returns the names written.
    /// </summary>
    public static async Task<IReadOnlyCollection<string>> WriteAsync(AppServices s, ParamEditBuffer edits, Func<string, bool>? skip = null,
        Action<string>? status = null)
    {
        if (edits.Count > 0)
        {
            var large = LargeChanges(edits.Pending, s.Params.Get, skip);
            if (large.Count > 0 && !await s.Dialogs.ConfirmAsync("Large Value", LargeChangeText(large), "Continue"))
            {
                edits.RemoveAll(large.Select(l => l.Name).ToList());
                status?.Invoke(large.Count == 1 ? $"{large[0].Name} put back; nothing written." : $"{large.Count} values put back; nothing written.");
                return [];
            }
        }

        return await ParamWriteFlow.WriteAsync(s, edits, status);
    }
}
