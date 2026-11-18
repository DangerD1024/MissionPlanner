using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using log4net;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;

/// <summary>
/// A preset from SerialOptionRules.json, applied when a port's protocol changes: -1 leaves the
/// baud rate or options as they are.
/// </summary>
public sealed record SerialOptionRule(int BaudRate, int Options, string Comment);

/// <summary>What choosing a protocol does to a port.</summary>
public sealed record SerialRuleOutcome(double? Baud, double? Options, string Note);

public static partial class SerialPortLogic
{
    public const string RulesFileName = "SerialOptionRules.json";

    /// <summary>ArduPilot has five MAVLink channels including USB, so four serial ports at most.</summary>
    public const int MavlinkPortLimit = 4;

    public const string MavlinkLimitWarning = "Warning: Maximum number of Mavlink ports are 5 including the USB port!";

    public const string RebootNote = "Changes to the serial port settings will not take effect until the board is rebooted.";

    private static readonly ILog Log = LogManager.GetLogger(typeof(SerialPortLogic));

    [GeneratedRegex(@"^SERIAL(\d+)_BAUD$", RegexOptions.CultureInvariant)]
    private static partial Regex BaudParam();

    /// <summary>SERIALn port numbers that have a SERIALn_BAUD parameter, USB (SERIAL0) excluded, ascending.</summary>
    public static IReadOnlyList<int> PortNumbers(IEnumerable<string> paramNames) =>
        paramNames.Select(n => BaudParam().Match(n))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .Where(n => n > 0)
            .Distinct()
            .Order()
            .ToList();

    /// <summary>Parses SerialOptionRules.json: <c>{ "protocol": { PresetBaudRate, PresetOptionsByte, Comment } }</c>.</summary>
    public static Dictionary<int, SerialOptionRule> ParseRules(string json)
    {
        var rules = new Dictionary<int, SerialOptionRule>();
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return rules;

        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (!int.TryParse(p.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var protocol) || p.Value.ValueKind != JsonValueKind.Object)
                continue;
            rules[protocol] = new SerialOptionRule(
                Int(p.Value, "PresetBaudRate"),
                Int(p.Value, "PresetOptionsByte"),
                p.Value.TryGetProperty("Comment", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "");
        }

        return rules;
    }

    /// <summary>The rules shipped next to the executable; empty when missing or unreadable.</summary>
    public static Dictionary<int, SerialOptionRule> LoadRules(string? path = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, RulesFileName);
        try
        {
            return File.Exists(path) ? ParseRules(File.ReadAllText(path)) : new Dictionary<int, SerialOptionRule>();
        }
        catch (Exception ex)
        {
            Log.Warn("serial rules " + path, ex);
            return new Dictionary<int, SerialOptionRule>();
        }
    }

    /// <summary>Drops preset baud rates the firmware does not offer (Mission Planner checks SERIAL1_BAUD's values).</summary>
    public static Dictionary<int, SerialOptionRule> Sanitize(IReadOnlyDictionary<int, SerialOptionRule> rules, IEnumerable<double> validBauds)
    {
        var valid = validBauds.ToHashSet();
        return rules.ToDictionary(r => r.Key,
            r => r.Value.BaudRate > -1 && !valid.Contains(r.Value.BaudRate) ? r.Value with { BaudRate = -1 } : r.Value);
    }

    /// <summary>The baud rate and options to write after <paramref name="protocol"/> is chosen for SERIALn, and the note to show.</summary>
    public static SerialRuleOutcome Apply(int port, int protocol, IReadOnlyDictionary<int, SerialOptionRule> rules)
    {
        if (!rules.TryGetValue(protocol, out var rule))
            return new SerialRuleOutcome(null, null, "");
        return new SerialRuleOutcome(
            rule.BaudRate > -1 ? rule.BaudRate : null,
            rule.Options > -1 ? rule.Options : null,
            string.IsNullOrEmpty(rule.Comment) ? "" : $"SERIAL{port} : {rule.Comment}");
    }

    /// <summary>MAVLink1 (1) and MAVLink2 (2) ports.</summary>
    public static int CountMavlink(IEnumerable<double> protocols) => protocols.Count(p => p is 1 or 2);

    public static bool TooManyMavlink(IEnumerable<double> protocols) => CountMavlink(protocols) >= MavlinkPortLimit;

    /// <summary>
    /// Parses the autopilot's @SYS/uarts.txt (<c>SERIAL1 UART7 TX* ...</c>) into port number → UART name.
    /// </summary>
    public static Dictionary<int, string> ParseUarts(string text)
    {
        var names = new Dictionary<int, string>();
        foreach (var raw in text.Split('\n'))
        {
            var parts = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0].Length < 7 || !parts[0].StartsWith("SERIAL", StringComparison.Ordinal))
                continue;
            if (int.TryParse(parts[0].AsSpan(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0)
                names.TryAdd(n, parts[1]);
        }

        return names;
    }

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : -1;
}
