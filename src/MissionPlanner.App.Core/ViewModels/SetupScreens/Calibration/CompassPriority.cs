namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Calibration;

/// <summary>One compass in the priority table.</summary>
public sealed record CompassDevice(string ParamName, DeviceId Id, bool Missing, bool External, int? Orientation)
{
    public uint DevId => Id.Raw;
}

/// <summary>
/// The compass priority table of Mission Planner's ConfigHWCompass2: rows from every
/// COMPASS*DEV_ID* parameter that is set, with the COMPASS_PRIOn_ID entries first (flagged
/// Missing when no detected compass has that id); reordering writes the first three rows to
/// COMPASS_PRIO1_ID..COMPASS_PRIO3_ID.
/// </summary>
public static class CompassPriority
{
    public static readonly string[] PriorityParams = ["COMPASS_PRIO1_ID", "COMPASS_PRIO2_ID", "COMPASS_PRIO3_ID"];

    // compass instance 1..3: device id, orientation and external names (old and new spellings)
    private static readonly (string[] DevId, string[] Orient, string[] External)[] Instances =
    [
        (["COMPASS_DEV_ID", "COMPASS1_DEV_ID"], ["COMPASS_ORIENT", "COMPASS1_ORIENT"], ["COMPASS_EXTERNAL", "COMPASS1_EXTERN"]),
        (["COMPASS_DEV_ID2", "COMPASS2_DEV_ID"], ["COMPASS_ORIENT2", "COMPASS2_ORIENT"], ["COMPASS_EXTERN2", "COMPASS2_EXTERN"]),
        (["COMPASS_DEV_ID3", "COMPASS3_DEV_ID"], ["COMPASS_ORIENT3", "COMPASS3_ORIENT"], ["COMPASS_EXTERN3", "COMPASS3_EXTERN"]),
    ];

    /// <summary>Whether the vehicle has the priority layout (ArduPilot 4.1 and later).</summary>
    public static bool HasPriorities(IReadOnlyDictionary<string, double> p) => p.ContainsKey("COMPASS_PRIO1_ID");

    /// <summary>Builds the table, priorities first. <paramref name="anyMissing"/> tells whether a prioritised compass is gone.</summary>
    public static List<CompassDevice> Build(IReadOnlyDictionary<string, double> p, out bool anyMissing)
    {
        var detected = p
            .Where(kv => kv.Key.StartsWith("COMPASS", StringComparison.Ordinal) && kv.Key.Contains("DEV_ID", StringComparison.Ordinal) && kv.Value != 0)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => Device(p, kv.Key, ToId(kv.Value), missing: false))
            .ToList();

        var prio = p
            .Where(kv => kv.Key.StartsWith("COMPASS_PRIO", StringComparison.Ordinal) && kv.Value != 0)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv =>
            {
                var id = ToId(kv.Value);
                return Device(p, kv.Key, id, missing: detected.All(d => d.DevId != id));
            })
            .ToList();

        anyMissing = prio.Any(d => d.Missing);

        var rows = new List<CompassDevice>(prio);
        rows.AddRange(detected.Where(d => prio.All(x => x.DevId != d.DevId)));
        return rows;
    }

    /// <summary>
    /// The parameter values that make <paramref name="order"/> the priority order: the first row's
    /// id in COMPASS_PRIO1_ID (only written when there is one), the next two in PRIO2/PRIO3 or 0.
    /// </summary>
    public static Dictionary<string, double> PriorityValues(IReadOnlyList<CompassDevice> order)
    {
        var values = new Dictionary<string, double>();
        if (order.Count >= 1)
            values[PriorityParams[0]] = order[0].DevId;
        values[PriorityParams[1]] = order.Count >= 2 ? order[1].DevId : 0;
        values[PriorityParams[2]] = order.Count >= 3 ? order[2].DevId : 0;
        return values;
    }

    /// <summary>Moves an item one place up; false at the top.</summary>
    public static bool MoveUp<T>(IList<T> list, int index)
    {
        if (index <= 0 || index >= list.Count)
            return false;
        (list[index - 1], list[index]) = (list[index], list[index - 1]);
        return true;
    }

    /// <summary>Moves an item one place down; false at the bottom.</summary>
    public static bool MoveDown<T>(IList<T> list, int index)
    {
        if (index < 0 || index >= list.Count - 1)
            return false;
        (list[index + 1], list[index]) = (list[index], list[index + 1]);
        return true;
    }

    private static uint ToId(double value) => value <= 0 ? 0 : (uint)Math.Round(value);

    private static CompassDevice Device(IReadOnlyDictionary<string, double> p, string name, uint id, bool missing)
    {
        int? orient = null;
        var external = false;
        foreach (var inst in Instances)
        {
            if (First(p, inst.DevId) is not { } devid || ToId(devid) != id)
                continue;
            orient = First(p, inst.Orient) is { } o ? (int)o : null;
            external = First(p, inst.External) > 0;
        }

        return new CompassDevice(name, new DeviceId(id), missing, external, orient);
    }

    private static double? First(IReadOnlyDictionary<string, double> p, string[] names)
    {
        foreach (var n in names)
        {
            if (p.TryGetValue(n, out var v))
                return v;
        }

        return null;
    }
}
