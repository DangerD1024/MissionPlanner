namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;

/// <summary>
/// Bitmask parameters: metadata lists bit indexes (<c>"0:Fast Attitude,1:..."</c>), the value is the sum
/// of <c>1 &lt;&lt; bit</c>. Bits the metadata does not describe are kept as they are.
/// </summary>
public static class BitmaskCodec
{
    /// <summary>The parameter value as an unsigned 32-bit mask (parameters travel as floats; negatives wrap).</summary>
    public static uint ToMask(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return 0;
        var rounded = Math.Round(value);
        if (rounded >= uint.MaxValue)
            return uint.MaxValue;
        if (rounded < int.MinValue)
            return 0;
        return rounded >= 0 ? (uint)rounded : unchecked((uint)(int)rounded);
    }

    public static bool IsSet(double value, int bit) => bit is >= 0 and < 32 && (ToMask(value) & (1u << bit)) != 0;

    /// <summary>The listed bits that are set in <paramref name="value"/>, in the order given.</summary>
    public static IReadOnlyList<int> Decode(double value, IEnumerable<int> bits) => bits.Where(b => IsSet(value, b)).ToList();

    /// <summary>
    /// Sets or clears each listed bit and keeps every other bit of <paramref name="original"/>.
    /// </summary>
    public static double Encode(double original, IEnumerable<KeyValuePair<int, bool>> bits)
    {
        var mask = ToMask(original);
        foreach (var (bit, on) in bits)
        {
            if (bit is < 0 or >= 32)
                continue;
            if (on)
                mask |= 1u << bit;
            else
                mask &= ~(1u << bit);
        }

        return mask;
    }

    /// <summary>Value with exactly these bits set.</summary>
    public static double FromBits(IEnumerable<int> bits) => Encode(0, bits.Select(b => new KeyValuePair<int, bool>(b, true)));

    /// <summary>"Label / Label" for the set bits, "None" when none are.</summary>
    public static string Summary(double value, IEnumerable<KeyValuePair<int, string>> labels)
    {
        var set = labels.Where(l => IsSet(value, l.Key)).Select(l => l.Value).ToList();
        return set.Count == 0 ? "None" : string.Join(" / ", set);
    }
}
