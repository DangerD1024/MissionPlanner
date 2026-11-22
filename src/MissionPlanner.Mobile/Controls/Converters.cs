using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using MissionPlanner.App.Core.ViewModels.FlightData;
using MissionPlanner.App.Core.ViewModels.Mobile;

namespace MissionPlanner.Mobile.Controls;

/// <summary>Status level → its colour (text and icons), or the dim version for chip backgrounds.</summary>
public sealed class LevelBrush : IValueConverter
{
    public static readonly LevelBrush Fore = new(dim: false);
    public static readonly LevelBrush Back = new(dim: true);

    private static readonly IBrush Ok = new SolidColorBrush(Color.Parse("#2FD27A"));
    private static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#FFB020"));
    private static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#FF4D4F"));
    private static readonly IBrush None = new SolidColorBrush(Color.Parse("#8B95A3"));
    private static readonly IBrush OkDim = new SolidColorBrush(Color.Parse("#2E2FD27A"));
    private static readonly IBrush WarnDim = new SolidColorBrush(Color.Parse("#2EFFB020"));
    private static readonly IBrush BadDim = new SolidColorBrush(Color.Parse("#38FF4D4F"));
    private static readonly IBrush NoneDim = new SolidColorBrush(Color.Parse("#1E242D"));

    private readonly bool _dim;

    private LevelBrush(bool dim) => _dim = dim;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        StatusLevel.Ok => _dim ? OkDim : Ok,
        StatusLevel.Warning => _dim ? WarnDim : Warn,
        StatusLevel.Bad => _dim ? BadDim : Bad,
        _ => _dim ? NoneDim : None,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A vehicle message's level → its colour in the Messages list.</summary>
public sealed class MessageBrush : IValueConverter
{
    public static readonly MessageBrush Instance = new();

    private static readonly IBrush Critical = new SolidColorBrush(Color.Parse("#FF6B6D"));
    private static readonly IBrush Warning = new SolidColorBrush(Color.Parse("#FFC24D"));
    private static readonly IBrush Normal = new SolidColorBrush(Color.Parse("#E6EAF0"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        MessageLevel.Critical => Critical,
        MessageLevel.Warning => Warning,
        _ => Normal,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>The tab bar: true when the bound tab is the parameter.</summary>
public sealed class TabIs : IValueConverter
{
    public static readonly TabIs Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is MobileTab t && parameter is MobileTab p && t == p;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A flag → a colour when set, another when not (armed red, link green...).</summary>
public sealed class BoolBrush(string on, string off) : IValueConverter
{
    public static readonly BoolBrush ArmedRed = new("#FF4D4F", "#8B95A3");
    public static readonly BoolBrush ErrorRed = new("#FF4D4F", "#3AA0FF");
    public static readonly BoolBrush ConnectedGreen = new("#2FD27A", "#8B95A3");
    public static readonly BoolBrush FollowBlue = new("#E63AA0FF", "#D9101419");

    private readonly IBrush _on = new SolidColorBrush(Color.Parse(on));
    private readonly IBrush _off = new SolidColorBrush(Color.Parse(off));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? _on : _off;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A count above zero (the unread badge).</summary>
public sealed class Positive : IValueConverter
{
    public static readonly Positive Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is int n && n > 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
