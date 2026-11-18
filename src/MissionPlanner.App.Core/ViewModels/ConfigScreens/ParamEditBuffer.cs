using System.Globalization;
using MissionPlanner.App.Core.Params;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>Outcome of checking a value typed for a parameter.</summary>
public enum EditCheck
{
    Ok,
    NotANumber,
    ReadOnly,

    /// <summary>A number outside the documented range: allowed only after the user confirms.</summary>
    OutOfRange,
}

public sealed record EditValidation(EditCheck Result, double Value, string Message);

/// <summary>
/// Parameter edits waiting for "Write Params", like Mission Planner's <c>_changes</c> table.
/// An edit equal to the vehicle's value is dropped, so the buffer only ever holds real changes.
/// </summary>
public sealed class ParamEditBuffer
{
    private readonly Dictionary<string, double> _pending = new(StringComparer.Ordinal);

    /// <summary>Raised after any change to the pending set.</summary>
    public event EventHandler? Changed;

    public int Count => _pending.Count;

    public IReadOnlyDictionary<string, double> Pending => _pending;

    public bool IsModified(string name) => _pending.ContainsKey(name);

    public bool TryGet(string name, out double value) => _pending.TryGetValue(name, out value);

    /// <summary>
    /// Queues <paramref name="value"/> for <paramref name="name"/>; when it equals
    /// <paramref name="current"/> (the vehicle's value) any queued edit is removed instead.
    /// Returns true if an edit is pending afterwards.
    /// </summary>
    public bool Set(string name, double value, double? current)
    {
        if (current is { } c && ParamValues.Same(c, value))
        {
            if (_pending.Remove(name))
                Changed?.Invoke(this, EventArgs.Empty);
            return false;
        }

        if (!_pending.TryGetValue(name, out var old) || !ParamValues.Same(old, value))
        {
            _pending[name] = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return true;
    }

    public void Remove(string name)
    {
        if (_pending.Remove(name))
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveAll(IEnumerable<string> names)
    {
        var any = false;
        foreach (var n in names)
            any |= _pending.Remove(n);
        if (any)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        if (_pending.Count == 0)
            return;
        _pending.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>"NAME: old -> new" lines, in write order (*_ENABLE first, then by name).</summary>
    public IReadOnlyList<string> Describe(Func<string, double?> current) =>
        WriteOrder(_pending.Keys)
            .Select(n => $"{n}: {(current(n) is { } c ? ParamValues.Format(c) : "?")} -> {ParamValues.Format(_pending[n])}")
            .ToList();

    /// <summary>The order ParamService writes in: *_ENABLE parameters first, as they create others.</summary>
    public static IEnumerable<string> WriteOrder(IEnumerable<string> names) =>
        names.OrderByDescending(k => k.EndsWith("ENABLE", StringComparison.Ordinal)).ThenBy(k => k, StringComparer.Ordinal);

    /// <summary>
    /// Checks a typed value as the Full Parameter List does: a number (or simple arithmetic),
    /// not read-only, inside the documented range unless the user confirms.
    /// </summary>
    public static EditValidation Validate(string name, string? text, ParamMeta? meta)
    {
        if (!ParamValues.TryParse(text, out var value))
            return new EditValidation(EditCheck.NotANumber, double.NaN, $"\"{text}\" is not a number.");

        if (meta?.ReadOnly == true)
            return new EditValidation(EditCheck.ReadOnly, value, $"{name} is marked as ReadOnly, and will not be changed.");

        if (meta is { Min: { } min, Max: { } max } && (value < min || value > max))
        {
            return new EditValidation(EditCheck.OutOfRange, value,
                $"{name} value {ParamValues.Format(value)} is out of range ({ParamValues.Format(min)} to {ParamValues.Format(max)}). Do you want to continue?");
        }

        return new EditValidation(EditCheck.Ok, value, "");
    }
}

/// <summary>Parsing, formatting and comparing parameter values (sent as 32-bit floats).</summary>
public static class ParamValues
{
    /// <summary>
    /// True when two values are the same parameter value: equal as the 32-bit floats the
    /// vehicle stores, except that distinct whole numbers (exact INT32 values from MAVFTP) differ.
    /// </summary>
    public static bool Same(double a, double b)
    {
        if (a.Equals(b))
            return true;
        if (IsWhole(a) && IsWhole(b))
            return false;
        return (float)a == (float)b;
    }

    private static bool IsWhole(double v) => Math.Abs(v) < 1e15 && v == Math.Floor(v);

    /// <summary>Invariant text without float noise: 0.15000000596 shows as 0.15.</summary>
    public static string Format(double v)
    {
        if (double.IsNaN(v))
            return "NaN";
        if (IsWhole(v))
            return ((long)v).ToString(CultureInfo.InvariantCulture);
        var f = (float)v;
        if (float.IsInfinity(f))
            return v.ToString("R", CultureInfo.InvariantCulture);
        return f.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Parses a number typed by the user: invariant decimal (a comma is taken as the decimal
    /// point), hexadecimal with 0x, or simple arithmetic such as <c>45*100</c> or <c>(1+2)/4</c>.
    /// </summary>
    public static bool TryParse(string? text, out double value)
    {
        value = double.NaN;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var s = text.Trim().Replace(',', '.');

        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!long.TryParse(s[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex))
                return false;
            value = hex;
            return true;
        }

        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return double.IsFinite(value);

        var parser = new ExpressionParser(s);
        if (!parser.TryParse(out value))
            return false;
        return double.IsFinite(value);
    }

    /// <summary>Recursive-descent parser for + - * / and parentheses.</summary>
    private ref struct ExpressionParser(ReadOnlySpan<char> text)
    {
        private readonly ReadOnlySpan<char> _s = text;
        private int _i;

        public bool TryParse(out double value)
        {
            value = double.NaN;
            if (!Sum(out value))
                return false;
            Skip();
            return _i == _s.Length;
        }

        private void Skip()
        {
            while (_i < _s.Length && char.IsWhiteSpace(_s[_i]))
                _i++;
        }

        private bool Sum(out double value)
        {
            if (!Product(out value))
                return false;
            while (true)
            {
                Skip();
                if (_i >= _s.Length || (_s[_i] != '+' && _s[_i] != '-'))
                    return true;
                var op = _s[_i++];
                if (!Product(out var rhs))
                    return false;
                value = op == '+' ? value + rhs : value - rhs;
            }
        }

        private bool Product(out double value)
        {
            if (!Unary(out value))
                return false;
            while (true)
            {
                Skip();
                if (_i >= _s.Length || (_s[_i] != '*' && _s[_i] != '/'))
                    return true;
                var op = _s[_i++];
                if (!Unary(out var rhs))
                    return false;
                value = op == '*' ? value * rhs : value / rhs;
            }
        }

        private bool Unary(out double value)
        {
            Skip();
            if (_i < _s.Length && (_s[_i] == '-' || _s[_i] == '+'))
            {
                var neg = _s[_i++] == '-';
                if (!Unary(out value))
                    return false;
                if (neg)
                    value = -value;
                return true;
            }

            return Atom(out value);
        }

        private bool Atom(out double value)
        {
            value = double.NaN;
            Skip();
            if (_i >= _s.Length)
                return false;
            if (_s[_i] == '(')
            {
                _i++;
                if (!Sum(out value))
                    return false;
                Skip();
                if (_i >= _s.Length || _s[_i] != ')')
                    return false;
                _i++;
                return true;
            }

            var start = _i;
            while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.' ||
                                      ((_s[_i] == 'e' || _s[_i] == 'E') && _i > start) ||
                                      ((_s[_i] == '-' || _s[_i] == '+') && _i > start && (_s[_i - 1] == 'e' || _s[_i - 1] == 'E'))))
                _i++;
            return _i > start && double.TryParse(_s[start.._i], NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
