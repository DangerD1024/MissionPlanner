using System.Globalization;
using System.Xml.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using log4net;
using MissionPlanner.App.Core.ViewModels.FlightData;
using MissionPlanner.Utilities;
using Conditional = MissionPlanner.Warnings.CustomWarning.Conditional;
using WarningColors = MissionPlanner.Warnings.CustomWarning.WarningColors;
using WarningType = MissionPlanner.Warnings.CustomWarning.WarningType;

namespace MissionPlanner.App.Core.ViewModels.Tools;

/// <summary>
/// One of Mission Planner's custom warnings (Warnings/CustomWarning): a CurrentState value compared
/// with a number, spoken (and shown on the HUD) at most every <see cref="RepeatSeconds"/>, or a
/// Quick tile coloured while it holds. <see cref="Child"/> chains further conditions that must hold
/// as well.
/// </summary>
public sealed partial class WarningRule : ObservableObject
{
    public const string DefaultText = "WARNING: {name} is {value}";

    [ObservableProperty]
    private string _field = "battery_voltage";

    [ObservableProperty]
    private Conditional _condition = Conditional.NONE;

    [ObservableProperty]
    private double _value;

    [ObservableProperty]
    private int _repeatSeconds = 10;

    [ObservableProperty]
    private string _text = DefaultText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsColoring), nameof(IsSpeech))]
    private WarningType _kind = WarningType.SpeakAndText;

    [ObservableProperty]
    private string _color = nameof(WarningColors.NoColor);

    [ObservableProperty]
    private WarningRule? _child;

    /// <summary>When it was last spoken (the repeat time counts from here).</summary>
    internal DateTime LastFired { get; set; } = DateTime.MinValue;

    public bool IsColoring
    {
        get => Kind == WarningType.Coloring;
        set
        {
            if (value == IsColoring)
                return;
            // as Mission Planner's "QuickPanel Coloring" box: a colouring rule has no text and no repeat
            Kind = value ? WarningType.Coloring : WarningType.SpeakAndText;
            if (value)
            {
                RepeatSeconds = 0;
                if (Color == nameof(WarningColors.NoColor))
                    Color = nameof(WarningColors.Red);
            }
            else
            {
                if (RepeatSeconds <= 0)
                    RepeatSeconds = 10;
                if (string.IsNullOrWhiteSpace(Text))
                    Text = DefaultText;
            }
        }
    }

    public bool IsSpeech => !IsColoring;

    /// <summary>This rule and its chained conditions, in order.</summary>
    public IEnumerable<WarningRule> Chain()
    {
        for (var r = this; r != null; r = r.Child)
            yield return r;
    }
}

/// <summary>What one check of the warnings produced.</summary>
public sealed record WarningCheck(IReadOnlyList<string> Spoken, IReadOnlyDictionary<string, string> Colors);

/// <summary>The checks of Mission Planner's WarningEngine, on plain values so they can be tested.</summary>
public static class WarningLogic
{
    public static IReadOnlyList<Conditional> Conditions { get; } = Enum.GetValues<Conditional>();

    public static IReadOnlyList<string> ColorNames { get; } = Enum.GetNames<WarningColors>();

    /// <summary>CustomWarning.CheckValue's comparison; NONE never holds.</summary>
    public static bool Holds(Conditional condition, double value, double warning) => condition switch
    {
        Conditional.LT => value < warning,
        Conditional.LTEQ => value <= warning,
        Conditional.EQ => value == warning,
        Conditional.GT => value > warning,
        Conditional.GTEQ => value >= warning,
        Conditional.NEQ => value != warning,
        _ => false,
    };

    /// <summary>Every condition of the chain holds (a value that cannot be read fails it).</summary>
    public static bool ChainHolds(WarningRule rule, Func<string, double?> read)
    {
        foreach (var r in rule.Chain())
        {
            if (read(r.Field) is not { } v || double.IsNaN(v) || !Holds(r.Condition, v, r.Value))
                return false;
        }

        return true;
    }

    /// <summary>CustomWarning.SayText: {warning}, {value} and {name} filled in.</summary>
    public static string SayText(WarningRule rule, double value) =>
        (rule.Text ?? "")
        .Replace("{warning}", rule.Value.ToString("0.##", CultureInfo.CurrentCulture))
        .Replace("{value}", value.ToString("0.##", CultureInfo.CurrentCulture))
        .Replace("{name}", rule.Field);

    /// <summary>
    /// One pass of the warning loop: speech rules whose chain holds and whose repeat time has passed
    /// fire (and restart their repeat time); colouring rules colour their field while they hold and
    /// clear it ("NoColor") otherwise.
    /// </summary>
    public static WarningCheck Check(IEnumerable<WarningRule> rules, Func<string, double?> read, DateTime now)
    {
        var spoken = new List<string>();
        var colors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            var holds = ChainHolds(rule, read);
            if (rule.Kind == WarningType.Coloring)
            {
                if (holds)
                    colors[rule.Field] = rule.Color;
                else
                    colors.TryAdd(rule.Field, nameof(WarningColors.NoColor));
                continue;
            }

            if (!holds || now < rule.LastFired.AddSeconds(Math.Max(0, rule.RepeatSeconds)))
                continue;
            rule.LastFired = now;
            spoken.Add(SayText(rule, read(rule.Field) ?? 0));
        }

        return new WarningCheck(spoken, colors);
    }

    /// <summary>The colour to paint a Quick tile with, or null (NoColor).</summary>
    public static string? TileColor(string? colorName) =>
        string.IsNullOrEmpty(colorName) || colorName == nameof(WarningColors.NoColor) ? null : colorName;
}

/// <summary>
/// The warnings file: Mission Planner's <c>warnings.xml</c> in the user data folder, the XML that
/// WarningEngine writes (a serialised List&lt;CustomWarning&gt;), so both apps read each other's.
/// </summary>
public static class WarningStore
{
    private static readonly XmlSerializer Serializer = new(typeof(List<CustomWarningXml>), new XmlRootAttribute("ArrayOfCustomWarning"));

    public static string DefaultPath => Settings.GetUserDataDirectory() + "warnings.xml";

    public static List<WarningRule> Load(string path)
    {
        if (!File.Exists(path))
            return [];
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    public static List<WarningRule> Read(Stream stream)
    {
        var list = (List<CustomWarningXml>?)Serializer.Deserialize(stream) ?? [];
        return list.Select(FromXml).ToList();
    }

    public static void Save(string path, IEnumerable<WarningRule> rules)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        using (var stream = File.Create(temp))
            Write(stream, rules);
        File.Move(temp, path, overwrite: true);
    }

    public static void Write(Stream stream, IEnumerable<WarningRule> rules) =>
        Serializer.Serialize(stream, rules.Select(ToXml).ToList());

    private static WarningRule FromXml(CustomWarningXml x)
    {
        var rule = new WarningRule
        {
            Field = x.Name ?? "",
            Condition = x.ConditionType,
            Value = x.Warning,
            RepeatSeconds = x.RepeatTime,
            Text = x.Text ?? "",
            Kind = x.type,
            Color = string.IsNullOrEmpty(x.color) ? nameof(WarningColors.NoColor) : x.color,
        };
        if (x.Child != null)
            rule.Child = FromXml(x.Child);
        return rule;
    }

    private static CustomWarningXml ToXml(WarningRule r) => new()
    {
        Child = r.Child == null ? null : ToXml(r.Child),
        Name = r.Field,
        Warning = r.Value,
        type = r.Kind,
        color = r.Color,
        RepeatTime = r.RepeatSeconds,
        ConditionType = r.Condition,
        Text = r.Text,
    };
}

/// <summary>The serialised shape of MissionPlanner.Warnings.CustomWarning (member names are the file's element names).</summary>
[XmlType("CustomWarning")]
public sealed class CustomWarningXml
{
    public CustomWarningXml? Child;

    public string? Name { get; set; }

    public double Warning { get; set; }

#pragma warning disable IDE1006 // the file's element names
    public WarningType type { get; set; }

    public string? color { get; set; }
#pragma warning restore IDE1006

    public int RepeatTime { get; set; } = 10;

    public Conditional ConditionType { get; set; }

    public string? Text { get; set; } = WarningRule.DefaultText;
}

/// <summary>
/// The warnings the app checks while a link is open (WarningEngine's loop): loaded from the file on
/// first use, replaced by the Warning Manager page. Driven from <see cref="AppSpeech"/>'s 2 Hz step.
/// </summary>
public static class AppWarnings
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(AppWarnings));
    private static readonly object Gate = new();
    private static WarningRule[]? _rules;
    private static volatile IReadOnlyDictionary<string, string> _colors = new Dictionary<string, string>();

    /// <summary>The rules being checked (loaded from <see cref="WarningStore.DefaultPath"/> the first time).</summary>
    public static IReadOnlyList<WarningRule> Rules
    {
        get
        {
            lock (Gate)
            {
                if (_rules == null)
                {
                    try
                    {
                        _rules = WarningStore.Load(WarningStore.DefaultPath).ToArray();
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("read " + WarningStore.DefaultPath, ex);
                        _rules = [];
                    }
                }

                return _rules;
            }
        }
    }

    /// <summary>Checks these rules from now on (the Warning Manager's edits apply at once, as in Mission Planner).</summary>
    public static void Use(IEnumerable<WarningRule> rules)
    {
        lock (Gate)
            _rules = rules.ToArray();
    }

    /// <summary>Quick tile colours by CurrentState field, from the colouring rules.</summary>
    public static string? ColorFor(string field) =>
        _colors.TryGetValue(field, out var c) ? WarningLogic.TileColor(c) : null;

    /// <summary>One check against the vehicle's state: speaks what fired and returns it for the HUD (or null).</summary>
    public static string? Step(CurrentState cs, ISpeech speech, DateTime now)
    {
        var rules = Rules;
        if (rules.Count == 0)
        {
            if (_colors.Count > 0)
                _colors = new Dictionary<string, string>();
            return null;
        }

        var result = WarningLogic.Check(rules, name => QuickValueCatalog.GetGetter(name) is { } g ? g(cs) : null, now);
        _colors = result.Colors;
        string? last = null;
        foreach (var text in result.Spoken)
        {
            speech.SpeakAsync(text);
            last = text;
        }

        return last;
    }

    /// <summary>The link closed: no tile stays coloured.</summary>
    public static void Clear()
    {
        if (_colors.Count > 0)
            _colors = new Dictionary<string, string>();
    }
}
