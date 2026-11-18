using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MissionPlanner.App.Controls.SetupScreens.Firmware;

/// <summary>
/// Outline drawings (48×48, stroked) of the vehicle types, for the Install Firmware and
/// Simulation tiles. Keyed by the view-models' icon names.
/// </summary>
public static class VehicleIcons
{
    private static readonly Dictionary<string, Geometry> Icons = new(StringComparer.Ordinal)
    {
        // quad X: arms, props and body
        ["copter"] = Geometry.Parse(
            "M12,12 L36,36 M36,12 L12,36 " +
            "M4,12 A8,8 0 1 1 20,12 A8,8 0 1 1 4,12 Z M28,12 A8,8 0 1 1 44,12 A8,8 0 1 1 28,12 Z " +
            "M4,36 A8,8 0 1 1 20,36 A8,8 0 1 1 4,36 Z M28,36 A8,8 0 1 1 44,36 A8,8 0 1 1 28,36 Z " +
            "M19,19 L29,19 L29,29 L19,29 Z"),

        // top-down fixed wing
        ["plane"] = Geometry.Parse(
            "M24,3 C26,3 27,6 27,9 L27,17 L45,23 L45,27 L27,24 L27,36 L33,40 L33,43 L24,41 L15,43 L15,40 L21,36 L21,24 " +
            "L3,27 L3,23 L21,17 L21,9 C21,6 22,3 24,3 Z"),

        // side view car
        ["rover"] = Geometry.Parse(
            "M4,32 L4,23 L12,22 L17,13 L33,13 L38,22 L44,23 L44,32 Z M17,22 L38,22 M25,13 L25,22 " +
            "M9,33 A5,5 0 1 1 19,33 A5,5 0 1 1 9,33 Z M29,33 A5,5 0 1 1 39,33 A5,5 0 1 1 29,33 Z"),

        // submarine with sail and propeller
        ["sub"] = Geometry.Parse(
            "M7,28 C7,21 13,19 21,19 L35,19 C41,19 45,23 45,28 C45,33 41,36 35,36 L21,36 C13,36 7,34 7,28 Z " +
            "M23,19 L24,12 L32,12 L33,19 M2,23 L7,26 M2,33 L7,30 M27,12 L27,8"),

        // helicopter: rotor, cabin, tail boom, skids
        ["heli"] = Geometry.Parse(
            "M4,10 L42,10 M23,10 L23,17 M12,27 C12,20 17,17 23,17 C30,17 33,22 33,27 C33,31 30,33 25,33 L18,33 C14,33 12,31 12,27 Z " +
            "M33,25 L45,23 M45,19 L45,27 M15,40 L33,40 M18,33 L18,40 M29,33 L29,40"),

        // antenna tracker: dish on a mast
        ["tracker"] = Geometry.Parse(
            "M7,13 A18,18 0 0 0 33,35 Z M20,24 L33,11 M31,9 A3,3 0 1 1 37,9 A3,3 0 1 1 31,9 Z " +
            "M20,30 L24,44 M13,44 L35,44"),
    };

    public static Geometry? Get(string? key) => key != null && Icons.TryGetValue(key, out var g) ? g : null;

    /// <summary>Icon name → geometry, for bindings.</summary>
    public static IValueConverter Converter { get; } = new FuncValueConverter<string?, Geometry?>(Get);
}

/// <summary>XAML-friendly instance of <see cref="VehicleIcons.Converter"/>.</summary>
public sealed class VehicleIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => VehicleIcons.Get(value as string);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
