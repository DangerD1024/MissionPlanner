using System.Globalization;
using System.Text;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Comms;

/// <summary>
/// The SiK (3DR / RFD / HopeRF) radio AT command protocol on a byte link, as Mission Planner's
/// SiK page drives it: a silent guard time, "+++" and an "OK" put the radio in command mode; every
/// command is echoed back before its reply; "AT..." commands address the radio on this computer,
/// "RT..." the one on the vehicle, through the air link. All calls block: run them off the UI thread.
/// </summary>
public sealed class SikAtClient
{
    private readonly IByteLink _link;
    private readonly StringBuilder _pending = new();

    public SikAtClient(IByteLink link) => _link = link;

    /// <summary>Silence the radio needs before (and after) "+++"; SiK uses one second.</summary>
    public int GuardTimeMs { get; init; } = 1100;

    /// <summary>How long to wait for the "OK" after "+++".</summary>
    public int EscapeTimeoutMs { get; init; } = 3000;

    /// <summary>How long to wait for a reply to a local command.</summary>
    public int CommandTimeoutMs { get; init; } = 1000;

    /// <summary>Remote (RT) commands travel over the air and back.</summary>
    public int RemoteTimeoutMs { get; init; } = 2000;

    /// <summary>A multi-line reply (ATI5) has ended after this much silence.</summary>
    public int QuietMs { get; init; } = 300;

    /// <summary>Called with each line sent ("&gt; ATI5") and received ("&lt; S0:FORMAT=25").</summary>
    public Action<string>? Log { get; set; }

    public bool InCommandMode { get; private set; }

    /// <summary>
    /// Puts the radio into AT command mode. Also succeeds when it already is (a previous session
    /// left it there): then "+++" is just typed text and a plain "AT" answers "OK".
    /// </summary>
    public bool EnterCommandMode(CancellationToken cancel = default)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancel.ThrowIfCancellationRequested();

            // the guard time: say nothing, and forget what the radio passes through meanwhile
            Thread.Sleep(GuardTimeMs);
            _link.DiscardInput();
            _pending.Clear();
            Send("+++", newline: false);
            if (WaitFor("OK", EscapeTimeoutMs, cancel))
            {
                // the radio wants silence after "+++" too before it takes a command
                Thread.Sleep(Math.Min(GuardTimeMs, 200));
                InCommandMode = true;
                return true;
            }

            // maybe already in command mode
            Send("", newline: true);
            Thread.Sleep(50);
            if (IsOk(Command("AT")))
            {
                InCommandMode = true;
                return true;
            }
        }

        InCommandMode = false;
        return false;
    }

    /// <summary>
    /// Sends one command and returns its reply without the echo. A single-line command returns the
    /// first line; a multi-line one everything until the radio goes quiet. Empty when nothing came back.
    /// </summary>
    public string Command(string command, bool multiLine = false, int? timeoutMs = null)
    {
        var remote = command.StartsWith("RT", StringComparison.OrdinalIgnoreCase);
        var timeout = timeoutMs ?? (remote ? RemoteTimeoutMs : CommandTimeoutMs);

        _link.DiscardInput();
        _pending.Clear();
        Send(command, newline: true);

        var lines = new List<string>();
        var echoSeen = false;
        var deadline = Environment.TickCount64 + timeout;
        long quietUntil = 0;

        while (true)
        {
            var now = Environment.TickCount64;
            if (lines.Count == 0 ? now >= deadline : multiLine && now >= quietUntil)
                break;

            var wait = lines.Count == 0 ? deadline - now : quietUntil - now;
            var line = ReadLine((int)Math.Max(1, Math.Min(wait, 50)));
            if (line == null)
                continue;

            var text = line.Trim();
            if (text.Length == 0)
                continue;
            if (!echoSeen && text.EndsWith(command, StringComparison.OrdinalIgnoreCase))
            {
                // the radio echoes what it is typed
                echoSeen = true;
                continue;
            }

            lines.Add(text);
            if (!multiLine)
                break;
            quietUntil = Environment.TickCount64 + QuietMs;
        }

        return string.Join("\n", lines);
    }

    public static bool IsOk(string reply) =>
        reply.Split('\n').Any(l => SikParser.StripMultipointPrefix(l).Equals("OK", StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads the version, board, band and settings of the local (or the remote) radio; null when it does not answer.</summary>
    public SikRadioInfo? ReadInfo(bool remote)
    {
        var p = remote ? "RT" : "AT";
        var version = Command(p + "I");
        if (version.Length == 0 || version.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
            return null;

        var multipoint = SikParser.HasMultipointPrefix(version);
        var board = SikParser.ParseNumber(Command(p + "I2"));
        var freqReply = Command(p + "I3");
        multipoint |= SikParser.HasMultipointPrefix(freqReply);
        var freq = SikParser.ParseNumber(freqReply);
        var parameters = SikParser.ParseParameters(Command(p + "I5", multiLine: true));
        var rssi = remote ? "" : SikParser.StripMultipointPrefix(Command("ATI7"));
        var key = multipoint ? null : SikParser.ParseAesKey(Command(p + "&E?"));

        return new SikRadioInfo(SikParser.StripMultipointPrefix(version), board, freq, parameters, rssi, key)
        {
            IsMultipoint = multipoint,
        };
    }

    /// <summary>ATSn=v (or RTSn=v); true when the radio said OK.</summary>
    public bool SetParameter(bool remote, string designator, int value) =>
        IsOk(Command($"{(remote ? "RT" : "AT")}{designator}={value.ToString(CultureInfo.InvariantCulture)}"));

    /// <summary>AT&amp;E=key: the AES key, as hex.</summary>
    public bool SetAesKey(bool remote, string hexKey) => IsOk(Command($"{(remote ? "RT" : "AT")}&E={hexKey}"));

    /// <summary>AT&amp;W: store the settings in EEPROM.</summary>
    public bool Save(bool remote) => IsOk(Command(remote ? "RT&W" : "AT&W"));

    /// <summary>AT&amp;F: factory defaults (not stored until <see cref="Save"/>).</summary>
    public bool FactoryReset(bool remote) => IsOk(Command(remote ? "RT&F" : "AT&F"));

    /// <summary>ATZ: restart with the stored settings; the radio leaves command mode.</summary>
    public void Reboot(bool remote)
    {
        Command(remote ? "RTZ" : "ATZ", timeoutMs: 300);
        if (!remote)
            InCommandMode = false;
    }

    /// <summary>ATO: back to passing data through.</summary>
    public void ExitCommandMode()
    {
        Command("ATO", timeoutMs: 200);
        InCommandMode = false;
    }

    private void Send(string text, bool newline)
    {
        if (text.Length > 0)
            Log?.Invoke("> " + text);
        _link.Write(newline ? text + "\r\n" : text);
    }

    /// <summary>Waits until <paramref name="token"/> shows up in the received text.</summary>
    private bool WaitFor(string token, int timeoutMs, CancellationToken cancel)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        var buffer = new byte[256];
        while (Environment.TickCount64 < deadline && !cancel.IsCancellationRequested)
        {
            var n = _link.Read(buffer, 0, buffer.Length, 50);
            if (n > 0)
                _pending.Append(ByteLinkExtensions.Text.GetString(buffer, 0, n));
            if (_pending.ToString().Contains(token, StringComparison.Ordinal))
            {
                Log?.Invoke("< " + _pending.ToString().Trim());
                _pending.Clear();
                return true;
            }
        }

        return false;
    }

    /// <summary>One line (without its terminator) or null if none completed in time.</summary>
    private string? ReadLine(int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        var buffer = new byte[256];
        while (true)
        {
            var text = _pending.ToString();
            var end = text.IndexOf('\n');
            if (end >= 0)
            {
                _pending.Remove(0, end + 1);
                var line = text[..end].TrimEnd('\r');
                if (line.Trim().Length > 0)
                    Log?.Invoke("< " + line.Trim());
                return line;
            }

            var left = deadline - Environment.TickCount64;
            if (left <= 0)
                return null;
            var n = _link.Read(buffer, 0, buffer.Length, (int)Math.Min(left, 50));
            if (n > 0)
                _pending.Append(ByteLinkExtensions.Text.GetString(buffer, 0, n));
        }
    }
}
