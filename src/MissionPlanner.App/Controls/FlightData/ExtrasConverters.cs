using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MissionPlanner.App.Controls.FlightData;

/// <summary>"#rrggbb" → brush; null or empty leaves the property unset, so the theme's colour applies.</summary>
public sealed class OptionalColorConverter : IValueConverter
{
    public static readonly OptionalColorConverter Instance = new();

    private static readonly Dictionary<string, IBrush> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || s.Length == 0)
            return AvaloniaProperty.UnsetValue;
        if (!Cache.TryGetValue(s, out var brush))
        {
            if (!Color.TryParse(s, out var c))
                return AvaloniaProperty.UnsetValue;
            brush = new SolidColorBrush(c);
            Cache[s] = brush;
        }

        return brush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>A PWM value (µs) as a 0-100 fill over 800-2200 µs.</summary>
public sealed class PwmPercentConverter : IValueConverter
{
    public static readonly PwmPercentConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        double d when d > 0 => Math.Clamp((d - 800) / 14.0, 0, 100),
        _ => 0.0,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
