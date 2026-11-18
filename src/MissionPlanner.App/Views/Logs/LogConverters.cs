using Avalonia.Data.Converters;
using Avalonia.Media;
using MissionPlanner.App.Core.ViewModels.Logs;

namespace MissionPlanner.App.Views.Logs;

/// <summary>Converters for the log pages.</summary>
public static class LogConverters
{
    /// <summary>"#rrggbb" → brush; null → transparent.</summary>
    public static readonly IValueConverter HexBrush = new FuncValueConverter<string?, IBrush>(hex =>
        hex != null && Color.TryParse(hex, out var c) ? new SolidColorBrush(c) : Brushes.Transparent);

    /// <summary>Event kind → its marker colour.</summary>
    public static readonly IValueConverter EventBrush = new FuncValueConverter<LogEventKind, IBrush>(kind =>
        new SolidColorBrush(Color.Parse(EventColor(kind))));

    /// <summary>Event kind → short label.</summary>
    public static readonly IValueConverter EventLabel = new FuncValueConverter<LogEventKind, string>(kind => kind switch
    {
        LogEventKind.Mode => "MODE",
        LogEventKind.Error => "ERROR",
        LogEventKind.Message => "MSG",
        _ => "EVENT",
    });

    /// <summary>Log row state → text colour.</summary>
    public static readonly IValueConverter StateBrush = new FuncValueConverter<LogEntryState, IBrush>(state => state switch
    {
        LogEntryState.Done => new SolidColorBrush(Color.Parse("#8cc63f")),
        LogEntryState.Failed => new SolidColorBrush(Color.Parse("#e5534b")),
        LogEntryState.Downloading => new SolidColorBrush(Color.Parse("#4fa3e3")),
        _ => new SolidColorBrush(Color.Parse("#9aa4b0")),
    });

    public static string EventColor(LogEventKind kind) => kind switch
    {
        LogEventKind.Mode => "#4fa3e3",
        LogEventKind.Error => "#e5534b",
        LogEventKind.Event => "#f0b429",
        _ => "#9aa4b0",
    };
}
