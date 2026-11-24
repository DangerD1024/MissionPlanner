using System.Text.Json;
using log4net;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.FlightPlan;

/// <summary>A command the user added to the planner's command list, with the names of its seven values.</summary>
public sealed record PlannerCommand(ushort Id, string Name, IReadOnlyList<string?> ParamNames)
{
    /// <summary>The command is a member of the MAVLink library's MAV_CMD.</summary>
    public bool IsKnown => PlannerExtraCommands.IsKnownId(Id);
}

/// <summary>
/// Mission Planner's Manage Command List (MavCommandSelection): extra commands for the PLAN grid,
/// kept as the WinForms app keeps them — a JSON map of name to the seven value names, and a map of
/// name to ID for IDs the MAVLink library does not know — under next_ keys of this app's settings.
/// </summary>
public static class PlannerExtraCommands
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(PlannerExtraCommands));

    public const string CommandsKey = "next_PlannerExtraCommand";
    public const string IdsKey = "next_PlannerExtraCommandIDs";

    /// <summary>Raised after <see cref="Save"/>; the planner reloads its command list (Mission Planner's updateCMDParams).</summary>
    public static event EventHandler? Changed;

    public static bool IsKnownId(ushort id) => Enum.IsDefined(typeof(MAVLink.MAV_CMD), id);

    /// <summary>The MAV_CMD name of an ID, or null when the library does not know it.</summary>
    public static string? KnownName(ushort id) => IsKnownId(id) ? ((MAVLink.MAV_CMD)id).ToString() : null;

    public static IReadOnlyList<PlannerCommand> Load()
    {
        try
        {
            return Parse(Settings.Instance[CommandsKey], Settings.Instance[IdsKey]);
        }
        catch (Exception ex)
        {
            Log.Warn("planner commands", ex);
            return [];
        }
    }

    /// <summary>Reads the two JSON maps; a name that is neither a MAV_CMD nor in the ID map is dropped.</summary>
    public static IReadOnlyList<PlannerCommand> Parse(string? commandsJson, string? idsJson)
    {
        var list = new List<PlannerCommand>();
        if (string.IsNullOrWhiteSpace(commandsJson))
            return list;
        var commands = JsonSerializer.Deserialize<Dictionary<string, string?[]?>>(commandsJson) ?? [];
        var ids = string.IsNullOrWhiteSpace(idsJson) ? [] : JsonSerializer.Deserialize<Dictionary<string, ushort>>(idsJson) ?? [];
        foreach (var (name, names) in commands)
        {
            ushort id;
            if (Enum.TryParse<MAVLink.MAV_CMD>(name, ignoreCase: false, out var cmd) && Enum.IsDefined(cmd) && cmd.ToString() == name)
                id = (ushort)cmd;
            else if (!ids.TryGetValue(name, out id))
                continue;
            list.Add(new PlannerCommand(id, name, Seven(names)));
        }

        return list;
    }

    /// <summary>The two JSON maps for a list (Mission Planner's btnSave_Click).</summary>
    public static (string Commands, string Ids) Serialize(IEnumerable<PlannerCommand> commands)
    {
        var map = new Dictionary<string, string?[]>();
        var ids = new Dictionary<string, ushort>();
        foreach (var c in commands)
        {
            if (string.IsNullOrWhiteSpace(c.Name) || map.ContainsKey(c.Name))
                continue;
            map[c.Name] = Seven(c.ParamNames);
            if (!IsKnownId(c.Id))
                ids[c.Name] = c.Id;
        }

        return (JsonSerializer.Serialize(map), JsonSerializer.Serialize(ids));
    }

    public static void Save(IEnumerable<PlannerCommand> commands)
    {
        var (c, i) = Serialize(commands);
        Settings.Instance[CommandsKey] = c;
        Settings.Instance[IdsKey] = i;
        Settings.Instance.Save();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Why a new entry cannot be added (Mission Planner's "Name or ID exists"), or null.</summary>
    public static string? Conflict(IEnumerable<PlannerCommand> existing, ushort id, string name) =>
        existing.Any(c => c.Id == id || string.Equals(c.Name, name, StringComparison.Ordinal)) ? "Name or ID exists" : null;

    /// <summary>Exactly seven names (missing ones null).</summary>
    public static string?[] Seven(IEnumerable<string?>? names)
    {
        var a = new string?[MavCmdCatalog.ValueCount];
        var i = 0;
        foreach (var n in names ?? [])
        {
            if (i >= a.Length)
                break;
            a[i++] = n;
        }

        return a;
    }
}
