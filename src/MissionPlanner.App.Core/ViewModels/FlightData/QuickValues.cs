using System.Collections.Concurrent;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using MissionPlanner.Attributes;

namespace MissionPlanner.App.Core.ViewModels.FlightData;

/// <summary>A numeric CurrentState property a Quick tile can show.</summary>
public sealed record QuickField(string Name, string Label, string Unit)
{
    /// <summary>"Label (unit)" as shown in the picker.</summary>
    public string Display => Unit.Length == 0 ? Label : $"{Label} ({Unit})";

    public override string ToString() => Display;
}

/// <summary>
/// Lists the numeric CurrentState properties (bools count as 0/1, like Mission Planner's Quick
/// tab) and hands out a getter per property, built once and cached, so reading a value never
/// uses reflection.
/// </summary>
public static class QuickValueCatalog
{
    private static readonly Lazy<PropertyInfo[]> Properties = new(FindProperties);
    private static readonly ConcurrentDictionary<string, Func<CurrentState, double>?> Getters = new(StringComparer.Ordinal);

    private static readonly HashSet<Type> NumericTypes =
    [
        typeof(byte), typeof(sbyte), typeof(short), typeof(ushort), typeof(int), typeof(uint), typeof(long),
        typeof(ulong), typeof(float), typeof(double), typeof(decimal), typeof(bool),
    ];

    /// <summary>
    /// The properties a tile can show, sorted by label. Custom fields (customfield0..) are listed
    /// only once the vehicle has named them.
    /// </summary>
    public static IReadOnlyList<QuickField> ListFields()
    {
        var custom = CurrentState.custom_field_names;
        var list = new List<QuickField>();
        foreach (var p in Properties.Value)
        {
            if (p.Name.StartsWith("customfield", StringComparison.Ordinal) &&
                (custom == null || !custom.ContainsKey(p.Name)))
                continue;
            list.Add(Describe(p.Name));
        }

        list.Sort((a, b) =>
        {
            var c = string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
        });
        return list;
    }

    public static bool IsNumericProperty(string name) => Properties.Value.Any(p => p.Name == name);

    /// <summary>Label and display unit of a property, from its DisplayText ("Altitude (alt)" → Altitude, m).</summary>
    public static QuickField Describe(string name)
    {
        var custom = CurrentState.custom_field_names;
        if (custom != null && custom.TryGetValue(name, out var customName))
            return new QuickField(name, customName, "");

        string? text = null;
        try
        {
            text = typeof(CurrentState).GetProperty(name)?.GetCustomAttribute<DisplayTextAttribute>(false)?.Text;
        }
        catch (Exception)
        {
            // a bad translation must not hide the field
        }

        if (string.IsNullOrWhiteSpace(text))
            return new QuickField(name, name, "");
        return SplitLabel(name, text);
    }

    /// <summary>"Ground Speed (speed)" → ("Ground Speed", "m/s"); the unit tokens follow the display units.</summary>
    public static QuickField SplitLabel(string name, string text)
    {
        var open = text.LastIndexOf('(');
        var close = text.LastIndexOf(')');
        if (open <= 0 || close < open)
            return new QuickField(name, text.Trim(), "");

        var label = text[..open].Trim();
        var unit = text[(open + 1)..close].Trim() switch
        {
            "dist" => Units.Distance,
            "speed" => Units.Speed,
            "alt" => Units.Altitude,
            var u => u,
        };
        return new QuickField(name, label, unit);
    }

    /// <summary>A cached getter for a numeric property, or null if there is no such property.</summary>
    public static Func<CurrentState, double>? GetGetter(string name) =>
        Getters.GetOrAdd(name, n => Properties.Value.FirstOrDefault(p => p.Name == n) is { } p ? CreateGetter(p) : null);

    /// <summary>Wraps the property's get method in a typed delegate; no reflection when it is called.</summary>
    internal static Func<CurrentState, double> CreateGetter(PropertyInfo p)
    {
        var m = p.GetMethod!;
        var t = p.PropertyType;
        if (t == typeof(double))
            return m.CreateDelegate<Func<CurrentState, double>>();
        if (t == typeof(float))
        {
            var f = m.CreateDelegate<Func<CurrentState, float>>();
            return cs => f(cs);
        }

        if (t == typeof(int))
        {
            var f = m.CreateDelegate<Func<CurrentState, int>>();
            return cs => f(cs);
        }

        if (t == typeof(uint))
        {
            var f = m.CreateDelegate<Func<CurrentState, uint>>();
            return cs => f(cs);
        }

        if (t == typeof(short))
        {
            var f = m.CreateDelegate<Func<CurrentState, short>>();
            return cs => f(cs);
        }

        if (t == typeof(ushort))
        {
            var f = m.CreateDelegate<Func<CurrentState, ushort>>();
            return cs => f(cs);
        }

        if (t == typeof(byte))
        {
            var f = m.CreateDelegate<Func<CurrentState, byte>>();
            return cs => f(cs);
        }

        if (t == typeof(sbyte))
        {
            var f = m.CreateDelegate<Func<CurrentState, sbyte>>();
            return cs => f(cs);
        }

        if (t == typeof(long))
        {
            var f = m.CreateDelegate<Func<CurrentState, long>>();
            return cs => f(cs);
        }

        if (t == typeof(ulong))
        {
            var f = m.CreateDelegate<Func<CurrentState, ulong>>();
            return cs => f(cs);
        }

        if (t == typeof(decimal))
        {
            var f = m.CreateDelegate<Func<CurrentState, decimal>>();
            return cs => (double)f(cs);
        }

        if (t == typeof(bool))
        {
            var f = m.CreateDelegate<Func<CurrentState, bool>>();
            return cs => f(cs) ? 1 : 0;
        }

        throw new ArgumentException($"{p.Name} is not numeric ({t.Name})", nameof(p));
    }

    private static PropertyInfo[] FindProperties() =>
        typeof(CurrentState).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0 &&
                        NumericTypes.Contains(p.PropertyType))
            .ToArray();
}

/// <summary>Display unit names; CurrentState leaves them empty until the user picks non-metric units.</summary>
public static class Units
{
    public static string Distance => CurrentState.DistanceUnit is { Length: > 0 } u ? u : "m";

    public static string Altitude => CurrentState.AltUnit is { Length: > 0 } u ? u : "m";

    public static string Speed => CurrentState.SpeedUnit is { Length: > 0 } u ? u : "m/s";
}

/// <summary>One of the six big readouts on the Quick tab.</summary>
public sealed partial class QuickTile : ObservableObject
{
    private Func<CurrentState, double> _getter;

    internal QuickTile(int index, QuickDefault fallback)
    {
        Index = index;
        Default = fallback;
        Color = fallback.Color;
        _getter = fallback.Getter;
        _field = fallback.Describe();
    }

    public int Index { get; }

    /// <summary>The value colour, as Mission Planner's defaults ("#rrggbb").</summary>
    public string Color { get; }

    internal QuickDefault Default { get; }

    public bool IsDefault => Field.Name == Default.Name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label), nameof(Unit), nameof(IsDefault))]
    private QuickField _field;

    [ObservableProperty]
    private double _value;

    /// <summary>Background colour from a colouring rule of TOOLS > Warning Manager, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarningColor))]
    private string? _warningColor;

    public bool HasWarningColor => WarningColor != null;

    public string Label => Field.Label;

    public string Unit => Field.Unit;

    /// <summary>Shows another property; false if it is not a numeric CurrentState property.</summary>
    public bool Show(string name)
    {
        Func<CurrentState, double>? getter = name == Default.Name ? Default.Getter : QuickValueCatalog.GetGetter(name);
        if (getter == null)
            return false;
        _getter = getter;
        Field = name == Default.Name ? Default.Describe() : QuickValueCatalog.Describe(name);
        return true;
    }

    /// <summary>Re-reads the labels, after the display units changed.</summary>
    public void RefreshLabel() => Field = IsDefault ? Default.Describe() : QuickValueCatalog.Describe(Field.Name);

    public void Update(CurrentState cs)
    {
        var color = Tools.AppWarnings.ColorFor(Field.Name);
        if (color != WarningColor)
            WarningColor = color;

        double v;
        try
        {
            v = _getter(cs);
        }
        catch (Exception)
        {
            v = double.NaN;
        }

        // NaN != NaN would notify every tick
        if (double.IsNaN(v) && double.IsNaN(Value))
            return;
        Value = v;
    }
}

/// <summary>A Quick tile's default property, read with a plain lambda (no reflection at all).</summary>
internal sealed record QuickDefault(string Name, Func<CurrentState, double> Getter, string Color, string? Label = null)
{
    public QuickField Describe()
    {
        var f = QuickValueCatalog.Describe(Name);
        return Label == null ? f : f with { Label = Label };
    }

    /// <summary>Mission Planner's six defaults and their colours.</summary>
    public static readonly QuickDefault[] All =
    [
        new("alt", cs => cs.alt, "#d197f8"),
        new("groundspeed", cs => cs.groundspeed, "#fe842e"),
        new("wp_dist", cs => cs.wp_dist, "#ff605b"),
        new("yaw", cs => cs.yaw, "#00ff53"),
        new("verticalspeed", cs => cs.verticalspeed, "#fefe56"),
        // Mission Planner labels this tile "DistToMAV": the distance from home (or the tracker) to the vehicle
        new("DistToHome", cs => cs.DistToHome, "#00fffc", "Dist to MAV"),
    ];

    /// <summary>Defaults of the tiles after the first six, when the view shows more.</summary>
    public static readonly QuickDefault[] Extra =
    [
        new("battery_voltage", cs => cs.battery_voltage, "#ffffff"),
        new("current", cs => cs.current, "#ffb3ff"),
        new("satcount", cs => cs.satcount, "#9ad0ff"),
        new("gpshdop", cs => cs.gpshdop, "#c8ff9a"),
        new("airspeed", cs => cs.airspeed, "#ffd27f"),
        new("battery_remaining", cs => cs.battery_remaining, "#7fffd4"),
    ];

    /// <summary>The default of tile number <paramref name="index"/>.</summary>
    public static QuickDefault For(int index) => index < All.Length ? All[index] : Extra[(index - All.Length) % Extra.Length];
}
