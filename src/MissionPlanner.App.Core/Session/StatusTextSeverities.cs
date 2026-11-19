using System.Collections.Concurrent;
using System.Text;

namespace MissionPlanner.App.Core.Session;

/// <summary>
/// The severity of each STATUSTEXT seen on the link, by text. The shared library keeps only the
/// time and text in <c>CurrentState.messages</c> (the WinForms app depends on that shape), so the
/// Messages tab looks the severity up here. A text the autopilot sends always carries the same
/// severity, so the latest one recorded for a text is the one shown.
/// </summary>
public sealed class StatusTextSeverities
{
    /// <summary>Distinct texts kept; the table starts over past this (texts with numbers in them vary).</summary>
    public const int MaxTexts = 4096;

    private readonly ConcurrentDictionary<string, MAVLink.MAV_SEVERITY> _byText = new(StringComparer.Ordinal);

    /// <summary>Called on the reader thread for every packet; records STATUSTEXT only.</summary>
    public void Record(MAVLink.MAVLinkMessage message)
    {
        if (message.msgid != (uint)MAVLink.MAVLINK_MSG_ID.STATUSTEXT)
            return;
        var st = message.ToStructure<MAVLink.mavlink_statustext_t>();
        Record(Decode(st.text), (MAVLink.MAV_SEVERITY)st.severity);
    }

    public void Record(string text, MAVLink.MAV_SEVERITY severity)
    {
        if (_byText.Count >= MaxTexts)
            _byText.Clear();
        _byText[text] = severity;
    }

    /// <summary>
    /// The severity recorded for a line of <c>CurrentState.messages</c>, or null. Lines from another
    /// component of the vehicle (a gimbal, a companion) are listed as "&lt;compid&gt; : text".
    /// </summary>
    public MAVLink.MAV_SEVERITY? Find(string text)
    {
        if (_byText.TryGetValue(text, out var s))
            return s;
        var at = text.IndexOf(" : ", StringComparison.Ordinal);
        if (at is > 0 and <= 3 && text.AsSpan(0, at).IndexOfAnyExceptInRange('0', '9') < 0 &&
            _byText.TryGetValue(text[(at + 3)..], out s))
            return s;
        return null;
    }

    public void Clear() => _byText.Clear();

    /// <summary>The text as MAVLinkInterface decodes it (UTF-8 up to the first NUL), so lookups match.</summary>
    public static string Decode(byte[]? text)
    {
        if (text == null)
            return "";
        var s = Encoding.UTF8.GetString(text);
        var nul = s.IndexOf('\0');
        return nul == -1 ? s : s[..nul];
    }
}
