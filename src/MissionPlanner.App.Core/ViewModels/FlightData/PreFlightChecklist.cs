using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using log4net;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.FlightData;

/// <summary>How a checklist item compares its value with the trigger (Mission Planner's CheckListItem.Conditional).</summary>
public enum ChecklistCondition
{
    NONE = 0,
    LT,
    LTEQ,
    EQ,
    GT,
    GTEQ,
    NEQ,
}

/// <summary>
/// One pre-flight checklist line, in Mission Planner's checklist.xml shape: a description, a
/// CurrentState property (or "PARAM" with the parameter name in braces in the description), a
/// condition against a trigger value, a display text with {value}/{trigger}/{name}, and the
/// colours for a passed / failed check. A NONE condition is a manual tick box.
/// </summary>
public sealed class ChecklistItem
{
    public string Description { get; set; } = "";

    /// <summary>Shown next to the description; {value}, {trigger} and {name} are replaced.</summary>
    public string Text { get; set; } = "";

    /// <summary>The CurrentState property, or <see cref="ChecklistEvaluator.ParamSource"/>.</summary>
    public string Name { get; set; } = "";

    public ChecklistCondition ConditionType { get; set; }

    public double TriggerValue { get; set; }

    /// <summary>.NET colour name for a passed check ("Green").</summary>
    public string TrueColor { get; set; } = "Green";

    /// <summary>.NET colour name for a failed check ("Red").</summary>
    public string FalseColor { get; set; } = "Red";

    /// <summary>A nested condition that must also hold (Mission Planner's Child).</summary>
    public ChecklistItem? Child { get; set; }

    public ChecklistItem Clone() => new()
    {
        Description = Description,
        Text = Text,
        Name = Name,
        ConditionType = ConditionType,
        TriggerValue = TriggerValue,
        TrueColor = TrueColor,
        FalseColor = FalseColor,
        Child = Child?.Clone(),
    };
}

/// <summary>
/// Reads and writes the checklist in Mission Planner's XmlSerializer format
/// (<c>ArrayOfCheckListItem</c>), so both apps share <c>checklist.xml</c> in the data folder.
/// </summary>
public static class ChecklistFile
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(ChecklistFile));

    public const string FileName = "checklist.xml";
    public const string DefaultFileName = "checklistDefault.xml";

    /// <summary>The user's edited list (shared with the WinForms app).</summary>
    public static string UserPath => Path.Combine(Settings.GetUserDataDirectory(), FileName);

    /// <summary>The default list shipped next to the executable, when present.</summary>
    public static string DefaultPath => Path.Combine(Settings.GetRunningDirectory(), DefaultFileName);

    /// <summary>The user's list, else the shipped default, else the built-in copy of Mission Planner's default.</summary>
    public static List<ChecklistItem> Load() => Load(UserPath, DefaultPath, out _);

    public static List<ChecklistItem> Load(string userPath, string defaultPath, out string source)
    {
        foreach (var path in new[] { userPath, defaultPath })
        {
            try
            {
                if (File.Exists(path))
                {
                    var items = Parse(File.ReadAllText(path));
                    source = path;
                    return items;
                }
            }
            catch (Exception ex)
            {
                // a broken file falls through to the next source
                Log.Warn("checklist " + path, ex);
            }
        }

        source = "built-in";
        return BuiltInDefault();
    }

    /// <summary>Mission Planner's checklistDefault.xml, for when the file is not next to the executable.</summary>
    public static List<ChecklistItem> BuiltInDefault() => Parse(BuiltInDefaultXml);

    public static void Save(IEnumerable<ChecklistItem> items) => Save(items, UserPath);

    public static void Save(IEnumerable<ChecklistItem> items, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, Serialize(items));
    }

    public static List<ChecklistItem> Parse(string xml)
    {
        var doc = XDocument.Parse(xml);
        var root = doc.Root ?? throw new FormatException("empty checklist");
        return root.Elements().Where(e => e.Name.LocalName == "CheckListItem").Select(ParseItem).ToList();
    }

    private static ChecklistItem ParseItem(XElement e)
    {
        string Get(string name) => e.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value ?? "";

        var item = new ChecklistItem
        {
            Description = Get("Description"),
            Text = Get("Text"),
            Name = Get("Name"),
            TrueColor = Get("TrueColor") is { Length: > 0 } t ? t : "Green",
            FalseColor = Get("FalseColor") is { Length: > 0 } f ? f : "Red",
            TriggerValue = double.TryParse(Get("TriggerValue"), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0,
            ConditionType = Enum.TryParse<ChecklistCondition>(Get("ConditionType"), true, out var c) ? c : ChecklistCondition.NONE,
        };
        var child = e.Elements().FirstOrDefault(x => x.Name.LocalName == "Child" && x.HasElements);
        if (child != null)
            item.Child = ParseItem(child);
        return item;
    }

    public static string Serialize(IEnumerable<ChecklistItem> items)
    {
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        XNamespace xsd = "http://www.w3.org/2001/XMLSchema";
        var root = new XElement("ArrayOfCheckListItem",
            new XAttribute(XNamespace.Xmlns + "xsi", xsi),
            new XAttribute(XNamespace.Xmlns + "xsd", xsd),
            items.Select(i => ToElement("CheckListItem", i)));
        return new XDocument(new XDeclaration("1.0", "utf-8", null), root).Declaration + Environment.NewLine + root;
    }

    private static XElement ToElement(string name, ChecklistItem i)
    {
        // element order as XmlSerializer writes Mission Planner's class (Child is a public field, so it comes first)
        var e = new XElement(name);
        if (i.Child != null)
            e.Add(ToElement("Child", i.Child));
        e.Add(new XElement("TrueColor", i.TrueColor),
            new XElement("FalseColor", i.FalseColor),
            new XElement("Description", i.Description),
            new XElement("Text", i.Text),
            new XElement("TriggerValue", i.TriggerValue.ToString("R", CultureInfo.InvariantCulture)),
            new XElement("Name", i.Name),
            new XElement("ConditionType", i.ConditionType.ToString()));
        return e;
    }

    /// <summary>Copy of the repository's checklistDefault.xml.</summary>
    public const string BuiltInDefaultXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <ArrayOfCheckListItem xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
          <CheckListItem><TrueColor>Green</TrueColor><FalseColor>Red</FalseColor><Description>Veryify GPS</Description><Text>{value} &gt;= 3</Text><TriggerValue>3</TriggerValue><Name>gpsstatus</Name><ConditionType>GTEQ</ConditionType></CheckListItem>
          <CheckListItem><TrueColor>Green</TrueColor><FalseColor>Red</FalseColor><Description>Gps Sat Count</Description><Text>{value} Sats</Text><TriggerValue>5</TriggerValue><Name>satcount</Name><ConditionType>GTEQ</ConditionType></CheckListItem>
          <CheckListItem><TrueColor>Green</TrueColor><FalseColor>Red</FalseColor><Description>Telemetry Signal</Description><Text>{value}%</Text><TriggerValue>95</TriggerValue><Name>linkqualitygcs</Name><ConditionType>GT</ConditionType></CheckListItem>
          <CheckListItem><TrueColor>Green</TrueColor><FalseColor>Red</FalseColor><Description>Battery Level</Description><Text>{value} V</Text><TriggerValue>22</TriggerValue><Name>battery_voltage</Name><ConditionType>GT</ConditionType></CheckListItem>
          <CheckListItem><TrueColor>White</TrueColor><FalseColor>White</FalseColor><Description>Mode</Description><Text>{value}</Text><TriggerValue>0</TriggerValue><Name>mode</Name><ConditionType>NONE</ConditionType></CheckListItem>
          <CheckListItem><TrueColor>White</TrueColor><FalseColor>White</FalseColor><Description>Check Altitude</Description><Text>{value} &lt; 5m</Text><TriggerValue>5</TriggerValue><Name>alt</Name><ConditionType>LT</ConditionType></CheckListItem>
          <CheckListItem><TrueColor>White</TrueColor><FalseColor>White</FalseColor><Description>Tail and wings secured?</Description><Text /><TriggerValue>0</TriggerValue><Name>roll</Name><ConditionType>NONE</ConditionType></CheckListItem>
          <CheckListItem><TrueColor>White</TrueColor><FalseColor>White</FalseColor><Description>All servos respond to input?</Description><Text /><TriggerValue>0</TriggerValue><Name>roll</Name><ConditionType>NONE</ConditionType></CheckListItem>
          <CheckListItem><TrueColor>White</TrueColor><FalseColor>White</FalseColor><Description>All servos respond to pitch and roll?</Description><Text /><TriggerValue>0</TriggerValue><Name>roll</Name><ConditionType>NONE</ConditionType></CheckListItem>
          <CheckListItem><TrueColor>White</TrueColor><FalseColor>White</FalseColor><Description>Center of gravity at indicated point?</Description><Text /><TriggerValue>0</TriggerValue><Name>roll</Name><ConditionType>NONE</ConditionType></CheckListItem>
          <CheckListItem><TrueColor>White</TrueColor><FalseColor>White</FalseColor><Description>Servo linkages are secure?</Description><Text /><TriggerValue>0</TriggerValue><Name>roll</Name><ConditionType>NONE</ConditionType></CheckListItem>
          <CheckListItem><TrueColor>White</TrueColor><FalseColor>White</FalseColor><Description>Camera is on and ready to fly?</Description><Text /><TriggerValue>0</TriggerValue><Name>roll</Name><ConditionType>NONE</ConditionType></CheckListItem>
        </ArrayOfCheckListItem>
        """;
}

/// <summary>
/// Evaluates checklist items against the vehicle state as Mission Planner's CheckListItem does,
/// with getters built once per property (no reflection per evaluation).
/// </summary>
public static class ChecklistEvaluator
{
    /// <summary>Source name for a parameter; its name is the {NAME} in the item's description.</summary>
    public const string ParamSource = "PARAM";

    private static readonly ConcurrentDictionary<string, Func<CurrentState, object?>?> ObjectGetters = new(StringComparer.Ordinal);
    private static readonly Lazy<IReadOnlyList<string>> SourceNames = new(FindSources);

    /// <summary>Every CurrentState property an item can watch, plus <see cref="ParamSource"/>.</summary>
    public static IReadOnlyList<string> Sources => SourceNames.Value;

    /// <summary>The parameter an item reads: the first {NAME} in its description.</summary>
    public static string? ParamName(ChecklistItem item)
    {
        var m = Regex.Match(item.Description ?? "", @"\{(.+?)\}");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>The value compared with the trigger: numbers (bools as 0/1); text and unknown properties give 0.</summary>
    public static double Value(ChecklistItem item, CurrentState cs, Func<string, double?>? param)
    {
        if (item.Name == ParamSource)
            return ParamName(item) is { } p && param?.Invoke(p) is { } v ? v : 0;

        var getter = QuickValueCatalog.GetGetter(item.Name);
        if (getter == null)
            return 0;
        try
        {
            return getter(cs);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>The condition holds, and so does the nested one (if any). NONE never holds.</summary>
    public static bool Check(ChecklistItem item, CurrentState cs, Func<string, double?>? param)
    {
        if (!Compare(item.ConditionType, Value(item, cs, param), item.TriggerValue))
            return false;
        return item.Child == null || Check(item.Child, cs, param);
    }

    public static bool Compare(ChecklistCondition condition, double value, double trigger) => condition switch
    {
        ChecklistCondition.LT => value < trigger,
        ChecklistCondition.LTEQ => value <= trigger,
        ChecklistCondition.EQ => value == trigger,
        ChecklistCondition.GT => value > trigger,
        ChecklistCondition.GTEQ => value >= trigger,
        ChecklistCondition.NEQ => value != trigger,
        _ => false,
    };

    /// <summary>The item's text with {trigger}, {value} and {name} filled in ("Error" if the value can't be read).</summary>
    public static string DisplayText(ChecklistItem item, CurrentState cs, Func<string, double?>? param)
    {
        var text = item.Text ?? "";
        if (text.Length == 0)
            return "";
        var inv = CultureInfo.InvariantCulture;
        text = text.Replace("{trigger}", item.TriggerValue.ToString("0.##", inv));

        if (item.Name == ParamSource)
            return text.Replace("{value}", Value(item, cs, param).ToString("0.##", inv));

        var getter = ObjectGetters.GetOrAdd(item.Name, CreateObjectGetter);
        if (getter == null)
            return text.Contains("{value}") ? "Error" : text.Replace("{name}", item.Name);

        object? value;
        try
        {
            value = getter(cs);
        }
        catch (Exception)
        {
            return "Error";
        }

        var shown = value switch
        {
            float f => ((double)f).ToString("0.##", inv),
            double d => d.ToString("0.##", inv),
            IFormattable fmt => fmt.ToString(null, inv),
            null => "",
            _ => value.ToString() ?? "",
        };
        return text.Replace("{value}", shown).Replace("{name}", item.Name);
    }

    private static Func<CurrentState, object?>? CreateObjectGetter(string name)
    {
        var p = typeof(CurrentState).GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (p?.GetMethod is not { IsPublic: true } || p.GetIndexParameters().Length > 0 || p.PropertyType.IsByRefLike)
            return null;
        var make = typeof(ChecklistEvaluator).GetMethod(nameof(Box), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(p.PropertyType);
        return (Func<CurrentState, object?>)make.Invoke(null, [p.GetMethod])!;
    }

    private static Func<CurrentState, object?> Box<T>(MethodInfo getter)
    {
        var f = getter.CreateDelegate<Func<CurrentState, T>>();
        return cs => f(cs);
    }

    private static IReadOnlyList<string> FindSources()
    {
        var names = typeof(CurrentState).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0 &&
                        (p.PropertyType.IsPrimitive || p.PropertyType == typeof(string) || p.PropertyType == typeof(decimal)))
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        names.Add(ParamSource);
        return names;
    }

    /// <summary>
    /// A display colour for a .NET colour name, or null for the theme's text colour (white and
    /// black would vanish on one of the themes, so they mean "default").
    /// </summary>
    public static string? ColorHex(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var n = name.Trim();
        if (n.StartsWith('#'))
            return n;
        return n.ToLowerInvariant() switch
        {
            "white" or "black" or "transparent" or "windowtext" or "controltext" => null,
            "green" or "lime" or "limegreen" or "forestgreen" or "darkgreen" or "lawngreen" => "#3fb950",
            "red" or "darkred" or "crimson" or "firebrick" or "orangered" => "#e5534b",
            "orange" or "darkorange" => "#f08c00",
            "yellow" or "gold" or "goldenrod" => "#d4a72c",
            "blue" or "dodgerblue" or "royalblue" or "deepskyblue" => "#4fa3e3",
            "gray" or "grey" or "darkgray" or "silver" => "#9aa4b0",
            "purple" or "violet" or "magenta" or "fuchsia" => "#b083f0",
            "cyan" or "aqua" or "turquoise" => "#39c5cf",
            _ => null,
        };
    }

    /// <summary>A few .NET colour names offered by the editor.</summary>
    public static IReadOnlyList<string> ColorNames { get; } =
        ["Green", "Red", "Orange", "Yellow", "Blue", "Gray", "Purple", "Cyan", "White"];
}
