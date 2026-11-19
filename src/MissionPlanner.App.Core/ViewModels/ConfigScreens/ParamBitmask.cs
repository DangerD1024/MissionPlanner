using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.Params;
using ParamType = MAVLink.MAV_PARAM_TYPE;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>
/// Bit arithmetic for bitmask parameters, as Mission Planner's MavlinkCheckBoxBitMask does it:
/// the documented key is the bit index (value 1 &lt;&lt; key), and a signed parameter with its
/// top bit set is sent as a negative number (INT8 255 = -1).
/// </summary>
public static class ParamBitmask
{
    /// <summary>How many bits a parameter of this type holds. Unknown and float types count as 32, like ArduPilot's bitmasks.</summary>
    public static int Width(ParamType type) => type switch
    {
        ParamType.INT8 or ParamType.UINT8 => 8,
        ParamType.INT16 or ParamType.UINT16 => 16,
        ParamType.INT64 or ParamType.UINT64 => 63,
        _ => 32,
    };

    /// <summary>The bits of a value, taken in the parameter's width (so INT8 -1 is 0xFF).</summary>
    public static ulong ToBits(double value, ParamType type)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return 0;
        var rounded = Math.Round(value);
        if (rounded >= long.MaxValue || rounded <= long.MinValue)
            return 0;
        var bits = unchecked((ulong)(long)rounded);
        var width = Width(type);
        return width >= 63 ? bits & long.MaxValue : bits & ((1UL << width) - 1);
    }

    /// <summary>The value to send for a set of bits, signed when the parameter type is.</summary>
    public static double FromBits(ulong bits, ParamType type) => type switch
    {
        ParamType.INT8 => unchecked((sbyte)(byte)bits),
        ParamType.UINT8 => (byte)bits,
        ParamType.INT16 => unchecked((short)(ushort)bits),
        ParamType.UINT16 => (ushort)bits,
        ParamType.UINT32 => (uint)bits,
        ParamType.INT64 or ParamType.UINT64 => bits & long.MaxValue,
        // INT32, and REAL32 when the real type is unknown: Mission Planner assumes INT32
        _ => unchecked((int)(uint)bits),
    };

    /// <summary>Indexes of the set bits, lowest first.</summary>
    public static IEnumerable<int> SetBits(ulong bits)
    {
        for (var i = 0; i < 64 && bits >> i != 0; i++)
        {
            if ((bits & (1UL << i)) != 0)
                yield return i;
        }
    }

    /// <summary>
    /// The set bits as text: documented ones by their label, others as "bit N".
    /// "None" when no bit is set.
    /// </summary>
    public static IReadOnlyList<string> Describe(double value, IReadOnlyList<KeyValuePair<int, string>> documented,
        ParamType type = ParamType.INT32)
    {
        var labels = new Dictionary<int, string>();
        foreach (var (bit, label) in documented)
            labels.TryAdd(bit, label);
        return SetBits(ToBits(value, type)).Select(b => labels.TryGetValue(b, out var l) ? l : $"bit {b}").ToList();
    }
}

/// <summary>One tick box of the bitmask editor.</summary>
public sealed partial class BitmaskBit : ObservableObject
{
    public BitmaskBit(int bit, string label, bool documented, bool isChecked)
    {
        Bit = bit;
        Label = label;
        IsDocumented = documented;
        _isChecked = isChecked;
    }

    public int Bit { get; }

    public string Label { get; }

    /// <summary>False for a bit that is set on the vehicle but has no documentation.</summary>
    public bool IsDocumented { get; }

    /// <summary>"bit 3 = 8": the index and the value it adds.</summary>
    public string BitText => $"bit {Bit} = {1UL << Bit}";

    internal Action? Changed { get; set; }

    [ObservableProperty]
    private bool _isChecked;

    partial void OnIsCheckedChanged(bool value) => Changed?.Invoke();
}

/// <summary>
/// "Set Bitmask" (Mission Planner's MavlinkCheckBoxBitMask in its own window): one tick box per
/// documented bit. Bits set on the vehicle but not documented are listed too, ticked, so they
/// are kept unless the user clears them. Shown modally; OK returns <see cref="Value"/>.
/// </summary>
public sealed partial class BitmaskEditorViewModel : PageViewModel, IDialogPage
{
    private bool _bulk;

    public BitmaskEditorViewModel(string name, ParamMeta? meta, double value, ParamType type)
        : base(name + " bitmask")
    {
        Name = name;
        Type = type;
        OriginalValue = value;
        DisplayName = meta?.DisplayName is { Length: > 0 } d ? d : name;
        Description = meta?.Description ?? "";

        var width = ParamBitmask.Width(type);
        var bits = ParamBitmask.ToBits(value, type);
        var documented = new List<BitmaskBit>();
        var seen = new HashSet<int>();
        foreach (var (bit, label) in (meta?.Bitmask ?? []).OrderBy(b => b.Key))
        {
            if (bit < 0 || bit >= width || !seen.Add(bit))
                continue;
            documented.Add(new BitmaskBit(bit, label, documented: true, (bits & (1UL << bit)) != 0));
        }

        Documented = documented;
        Undocumented = ParamBitmask.SetBits(bits)
            .Where(b => !seen.Contains(b))
            .Select(b => new BitmaskBit(b, $"bit {b} (not documented)", documented: false, isChecked: true))
            .ToList();

        foreach (var b in Documented.Concat(Undocumented))
            b.Changed = OnBitChanged;
    }

    public event EventHandler<bool>? CloseRequested;

    public string Name { get; }

    public string DisplayName { get; }

    public string Description { get; }

    public bool HasDescription => Description.Length > 0;

    public ParamType Type { get; }

    public double OriginalValue { get; }

    public IReadOnlyList<BitmaskBit> Documented { get; }

    /// <summary>Set bits without documentation, kept unless cleared.</summary>
    public IReadOnlyList<BitmaskBit> Undocumented { get; }

    public bool HasUndocumented => Undocumented.Count > 0;

    public bool HasNoDocumentation => Documented.Count == 0;

    /// <summary>The combined value of the ticked bits, as the vehicle stores it.</summary>
    public double Value
    {
        get
        {
            ulong bits = 0;
            foreach (var b in Documented.Concat(Undocumented))
            {
                if (b.IsChecked)
                    bits |= 1UL << b.Bit;
            }

            return ParamBitmask.FromBits(bits, Type);
        }
    }

    public string ValueText
    {
        get
        {
            var v = Value;
            var hex = ParamBitmask.ToBits(v, Type).ToString("X");
            return $"{ParamValues.Format(v)}  (0x{hex})";
        }
    }

    public bool IsChanged => !ParamValues.Same(Value, OriginalValue);

    public string OriginalText => "was " + ParamValues.Format(OriginalValue);

    /// <summary>Window width: two columns of tick boxes.</summary>
    public const double DialogWidth = 640;

    /// <summary>Window height for the number of tick boxes (two per line), within 340 to 720.</summary>
    public double DialogHeight
    {
        get
        {
            var lines = (Documented.Count + 1) / 2 + (Undocumented.Count + 1) / 2;
            var height = 250 + (HasDescription ? 54 : 0) + lines * 30 + (HasUndocumented ? 44 : 0);
            return Math.Clamp(height, 340, 720);
        }
    }

    private void OnBitChanged()
    {
        if (_bulk)
            return;
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(ValueText));
        OnPropertyChanged(nameof(IsChanged));
    }

    private void SetAll(bool value)
    {
        _bulk = true;
        try
        {
            foreach (var b in Documented)
                b.IsChecked = value;
        }
        finally
        {
            _bulk = false;
        }

        OnBitChanged();
    }

    [RelayCommand]
    private void SelectAll() => SetAll(true);

    [RelayCommand]
    private void ClearAll() => SetAll(false);

    [RelayCommand]
    private void Apply() => CloseRequested?.Invoke(this, true);

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}
