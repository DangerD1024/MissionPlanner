using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MissionPlanner.App.Controls.FlightData;

/// <summary>"#rrggbb" → brush (the Quick tile colours live in the view-model as text).</summary>
public sealed class HexBrushConverter : IValueConverter
{
    public static readonly HexBrushConverter Instance = new();

    private static readonly Dictionary<string, IBrush> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || s.Length == 0)
            return Brushes.White;
        if (!Cache.TryGetValue(s, out var brush))
        {
            brush = Color.TryParse(s, out var c) ? new SolidColorBrush(c) : Brushes.White;
            Cache[s] = brush;
        }

        return brush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Quick tile numbers, as Mission Planner shows them (two decimals; NaN as a dash).</summary>
public sealed class QuickNumberConverter : IValueConverter
{
    public static readonly QuickNumberConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        double d when double.IsNaN(d) || double.IsInfinity(d) => "-",
        double d => d.ToString(Math.Abs(d) >= 100000 ? "0" : "0.00", culture),
        _ => "-",
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
