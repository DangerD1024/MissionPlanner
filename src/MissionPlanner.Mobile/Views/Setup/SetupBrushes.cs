using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MissionPlanner.Mobile.Views.Setup;

/// <summary>Flag → colour for the setup pages (a passed step green, a failed bar red, the active slot outlined).</summary>
public sealed class SetupBrushes(string on, string off) : IValueConverter
{
    public static readonly SetupBrushes OkOrText = new("#2FD27A", "#F2F5F8");
    public static readonly SetupBrushes BadOrText = new("#FF4D4F", "#F2F5F8");
    public static readonly SetupBrushes BadOrAccent = new("#FF4D4F", "#3AA0FF");
    public static readonly SetupBrushes BadOrMuted = new("#FF4D4F", "#8B95A3");
    public static readonly SetupBrushes AccentOrNone = new("#3AA0FF", "#00000000");

    private readonly IBrush _on = new SolidColorBrush(Color.Parse(on));
    private readonly IBrush _off = new SolidColorBrush(Color.Parse(off));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? _on : _off;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Pages the vehicle does not have are dimmed (still tappable: they say why).</summary>
public sealed class SetupOpacity : IValueConverter
{
    public static readonly SetupOpacity Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? 1.0 : 0.45;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>An icon key of the theme ("IcCompass") → its geometry.</summary>
public sealed class IconKey : IValueConverter
{
    public static readonly IconKey Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key && Avalonia.Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var g) ? g : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
