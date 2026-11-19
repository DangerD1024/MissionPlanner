using System.Collections.ObjectModel;
using System.Globalization;

namespace MissionPlanner.App.Core.ViewModels.FlightData;

/// <summary>How a line on the Messages tab is drawn: red, amber or plain.</summary>
public enum MessageLevel
{
    Info,
    Warning,
    Critical,
}

/// <summary>A STATUSTEXT line on the Messages tab.</summary>
public sealed record MessageLine(DateTime Time, string Text, MAVLink.MAV_SEVERITY? Severity = null)
{
    public string TimeText { get; } = Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// From the STATUSTEXT severity (EMERGENCY..ERROR red, WARNING amber, the rest plain). Without
    /// one (a log played back, text the GCS added) the text decides, as Mission Planner colours it:
    /// PreArm/Arm checks red, failures and errors amber.
    /// </summary>
    public MessageLevel Level { get; } = Severity switch
    {
        <= MAVLink.MAV_SEVERITY.ERROR => MessageLevel.Critical,
        MAVLink.MAV_SEVERITY.WARNING => MessageLevel.Warning,
        not null => MessageLevel.Info,
        null => Guess(Text),
    };

    /// <summary>The badge: CRIT, WARN, INFO and the other severity names, shortened.</summary>
    public string LevelText { get; } = Severity switch
    {
        MAVLink.MAV_SEVERITY.EMERGENCY => "EMERG",
        MAVLink.MAV_SEVERITY.ALERT => "ALERT",
        MAVLink.MAV_SEVERITY.CRITICAL => "CRIT",
        MAVLink.MAV_SEVERITY.ERROR => "ERROR",
        MAVLink.MAV_SEVERITY.WARNING => "WARN",
        MAVLink.MAV_SEVERITY.NOTICE => "NOTICE",
        MAVLink.MAV_SEVERITY.INFO => "INFO",
        MAVLink.MAV_SEVERITY.DEBUG => "DEBUG",
        _ => Guess(Text) switch
        {
            MessageLevel.Critical => "CRIT",
            MessageLevel.Warning => "WARN",
            _ => "INFO",
        },
    };

    public bool IsCritical => Level == MessageLevel.Critical;

    public bool IsWarning => Level == MessageLevel.Warning;

    private static MessageLevel Guess(string text) =>
        text.StartsWith("PreArm", StringComparison.OrdinalIgnoreCase) || text.StartsWith("Arm:", StringComparison.OrdinalIgnoreCase)
            ? MessageLevel.Critical
            : text.Contains("fail", StringComparison.OrdinalIgnoreCase) || text.Contains("error", StringComparison.OrdinalIgnoreCase)
                ? MessageLevel.Warning
                : MessageLevel.Info;
}

/// <summary>
/// Mirrors <c>CurrentState.messages</c> (oldest first, written by the reader thread, capped at
/// 1000 by removing the oldest) into a newest-first list. New messages are noticed by the time of
/// the last one, as Mission Planner does; only the new lines are inserted.
/// </summary>
public sealed class MessageLog
{
    public const int MaxLines = 1000;

    private DateTime _lastTime;
    private string? _lastText;
    private int _lastCount;
    private List<(DateTime time, string message)>? _source;

    /// <summary>Newest first.</summary>
    public ObservableCollection<MessageLine> Lines { get; } = new();

    /// <summary>The STATUSTEXT severity of a line's text, when known (<see cref="Session.StatusTextSeverities.Find"/>).</summary>
    public Func<string, MAVLink.MAV_SEVERITY?>? SeverityOf { get; set; }

    /// <summary>Checks the vehicle's list; returns true when lines were added or the list was rebuilt.</summary>
    public bool Update(List<(DateTime time, string message)> source)
    {
        if (!ReferenceEquals(source, _source))
        {
            // another vehicle selected
            _source = source;
            Reset();
        }

        (DateTime time, string message)[] snapshot;
        try
        {
            var count = source.Count;
            if (count == 0)
            {
                if (_lastCount == 0)
                    return false;
                // cleared (reconnect, ResetInternals)
                Reset();
                return true;
            }

            var last = source[count - 1];
            if (last.time == _lastTime && ReferenceEquals(last.message, _lastText) && count == _lastCount)
                return false;

            // the reader thread appends while we copy; a torn copy is retried on the next tick
            snapshot = source.ToArray();
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        return Apply(snapshot);
    }

    /// <summary>Merges a snapshot (oldest first) of the vehicle's messages.</summary>
    public bool Apply(IReadOnlyList<(DateTime time, string message)> snapshot)
    {
        if (snapshot.Count == 0)
        {
            var had = Lines.Count > 0;
            Reset();
            return had;
        }

        var newest = snapshot[^1];

        // find where the last line we showed is in the snapshot; everything after it is new
        var start = 0;
        var at = _lastCount - 1;
        if (_lastText != null && at >= 0 && at < snapshot.Count && snapshot[at].time == _lastTime && snapshot[at].message == _lastText)
        {
            // appended only (the usual case; also right when the same text repeats in the same second)
            start = _lastCount;
        }
        else if (_lastText != null)
        {
            start = -1;
            for (var i = snapshot.Count - 1; i >= 0; i--)
            {
                if (snapshot[i].time == _lastTime && snapshot[i].message == _lastText)
                {
                    start = i + 1;
                    break;
                }
            }

            if (start < 0)
            {
                // not there any more (list cleared and refilled): rebuild
                Lines.Clear();
                start = 0;
            }
        }

        if (start == snapshot.Count)
        {
            _lastCount = snapshot.Count;
            return false;
        }

        // newest goes on top
        for (var i = start; i < snapshot.Count; i++)
        {
            var text = snapshot[i].message ?? "";
            Lines.Insert(0, new MessageLine(snapshot[i].time, text, SeverityOf?.Invoke(text)));
        }

        while (Lines.Count > MaxLines)
            Lines.RemoveAt(Lines.Count - 1);

        _lastTime = newest.time;
        _lastText = newest.message;
        _lastCount = snapshot.Count;
        return true;
    }

    public void Reset()
    {
        Lines.Clear();
        _lastTime = default;
        _lastText = null;
        _lastCount = 0;
    }
}
