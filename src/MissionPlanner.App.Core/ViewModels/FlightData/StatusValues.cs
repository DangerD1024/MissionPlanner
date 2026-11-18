using System.Collections;
using System.Globalization;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MissionPlanner.App.Core.ViewModels.FlightData;

/// <summary>One row of the Status tab: a CurrentState property and its current value.</summary>
public sealed partial class StatusEntry : ObservableObject
{
    private readonly Func<CurrentState, double>? _number;
    private readonly Func<CurrentState, object?>? _other;
    private double _lastNumber = double.NaN;
    private object? _lastObject;
    private int _failures;
    private int _skip;

    internal StatusEntry(string name, Func<CurrentState, double>? number, Func<CurrentState, object?>? other, bool isBool,
        bool isFloat = false)
    {
        Name = name;
        _number = number;
        _other = other;
        IsBool = isBool;
        _isFloat = isFloat;
    }

    private readonly bool _isFloat;

    public string Name { get; }

    public bool IsBool { get; }

    [ObservableProperty]
    private string _value = "";

    /// <summary>Re-reads the value; formats only when it changed. A property that throws is retried less often.</summary>
    public void Update(CurrentState cs)
    {
        if (_skip > 0)
        {
            _skip--;
            return;
        }

        try
        {
            if (_number != null)
            {
                var v = _number(cs);
                if (v.Equals(_lastNumber) && Value.Length > 0)
                    return;
                _lastNumber = v;
                Value = IsBool ? (v != 0 ? "True" : "False") : FormatNumber(v, _isFloat);
            }
            else if (_other != null)
            {
                var o = _other(cs);
                // reference types (PointLatLngAlt...) may be the same instance with new contents
                if ((o is string || o?.GetType().IsValueType == true) && Equals(o, _lastObject) && Value.Length > 0)
                    return;
                _lastObject = o;
                Value = FormatObject(o);
            }

            _failures = 0;
        }
        catch (Exception)
        {
            _failures++;
            _skip = Math.Min(20, _failures * 2);
            Value = "-";
        }
    }

    /// <summary>Floats are shown at float precision (58.95, not 58.950001).</summary>
    internal static string FormatNumber(double v, bool isFloat = false)
    {
        if (double.IsNaN(v))
            return "NaN";
        var format = Math.Abs(v) >= 1e7 ? "0" : "0.######";
        return isFloat ? ((float)v).ToString(format, CultureInfo.InvariantCulture) : v.ToString(format, CultureInfo.InvariantCulture);
    }

    internal static string FormatObject(object? o) => o switch
    {
        null => "",
        string s => s,
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        ICollection c => $"[{c.Count} items]",
        _ => o.ToString() ?? "",
    };
}

/// <summary>
/// Every public readable property of CurrentState, with a getter built once per property
/// (numbers through typed delegates, so refreshing reads no metadata and boxes nothing).
/// </summary>
public static class StatusCatalog
{
    private static readonly Lazy<(string Name, PropertyInfo Property)[]> Properties = new(Find);

    public static IReadOnlyList<string> Names => Properties.Value.Select(p => p.Name).ToArray();

    /// <summary>New rows for a list; each list owns its rows (their cached values are per list).</summary>
    public static List<StatusEntry> CreateEntries()
    {
        var list = new List<StatusEntry>(Properties.Value.Length);
        foreach (var (name, p) in Properties.Value)
        {
            var t = p.PropertyType;
            if (QuickValueCatalog.IsNumericProperty(name))
                list.Add(new StatusEntry(name, QuickValueCatalog.CreateGetter(p), null, t == typeof(bool), t == typeof(float)));
            else
                list.Add(new StatusEntry(name, null, CreateObjectGetter(p), false));
        }

        return list;
    }

    /// <summary>Case-insensitive substring match on the name; empty matches everything.</summary>
    public static bool Matches(StatusEntry e, string? filter) =>
        string.IsNullOrWhiteSpace(filter) || e.Name.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase);

    private static Func<CurrentState, object?> CreateObjectGetter(PropertyInfo p)
    {
        var make = typeof(StatusCatalog).GetMethod(nameof(Box), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(p.PropertyType);
        return (Func<CurrentState, object?>)make.Invoke(null, [p.GetMethod!])!;
    }

    private static Func<CurrentState, object?> Box<T>(MethodInfo getter)
    {
        var f = getter.CreateDelegate<Func<CurrentState, T>>();
        return cs => f(cs);
    }

    private static (string, PropertyInfo)[] Find() =>
        typeof(CurrentState).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0 &&
                        !p.PropertyType.IsByRefLike && !p.PropertyType.IsPointer)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => (p.Name, p))
            .ToArray();
}
