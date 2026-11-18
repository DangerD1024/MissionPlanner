using System.Text;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Comms;

/// <summary>
/// The terminal's screen text: received characters turned into lines the way a dumb terminal would
/// (CR LF, a lone CR returning to the line start, backspace) with ANSI escape sequences (colours,
/// erase-line) dropped, even when one is split across reads. Keeps the last
/// <see cref="MaxLines"/> lines for display and the whole session for saving.
/// </summary>
public sealed class TerminalBuffer
{
    private readonly LinkedList<string> _lines = new();
    private readonly StringBuilder _current = new();
    private readonly StringBuilder _log = new();
    private readonly StringBuilder _escape = new();
    private bool _carriageReturn;
    private string? _text;

    public TerminalBuffer(int maxLines = 3000, int maxLogChars = 8 * 1024 * 1024)
    {
        MaxLines = maxLines;
        MaxLogChars = maxLogChars;
    }

    public int MaxLines { get; }

    public int MaxLogChars { get; }

    /// <summary>Lines dropped from the display so far.</summary>
    public long TrimmedLines { get; private set; }

    /// <summary>What the terminal shows: the kept lines and the line being received.</summary>
    public string Text => _text ??= BuildText();

    /// <summary>Every completed line of the session plus the current one, for "Save log".</summary>
    public string FullLog => _log.ToString() + _current;

    public int LineCount => _lines.Count + (_current.Length > 0 ? 1 : 0);

    public void Append(string data)
    {
        foreach (var c in data)
            Put(c);
        _text = null;
    }

    /// <summary>Adds a whole local line (a note such as "Opened SERIAL2"), ending any partial line first.</summary>
    public void AppendLine(string line)
    {
        if (_current.Length > 0)
            NewLine();
        Append(line + "\n");
    }

    public void Clear()
    {
        _lines.Clear();
        _current.Clear();
        _log.Clear();
        _escape.Clear();
        _carriageReturn = false;
        TrimmedLines = 0;
        _text = null;
    }

    private void Put(char c)
    {
        if (_escape.Length > 0)
        {
            _escape.Append(c);
            // ESC [ params final  (final byte 0x40..0x7E); ESC x for anything else
            if (_escape.Length == 2 ? c != '[' : c is >= '@' and <= '~')
                _escape.Clear();
            else if (_escape.Length > 32)
                _escape.Clear(); // not a sequence we understand; drop it
            return;
        }

        switch (c)
        {
            case '\x1b':
                _escape.Append(c);
                return;
            case '\n':
                _carriageReturn = false;
                NewLine();
                return;
            case '\r':
                _carriageReturn = true;
                return;
            case '\b':
                _carriageReturn = false;
                if (_current.Length > 0)
                    _current.Length--;
                return;
            case '\t':
                break;
            default:
                if (c < ' ' || c == '\x7f')
                    return; // NUL, bell and other controls
                break;
        }

        if (_carriageReturn)
        {
            // a lone CR: the next text overwrites the line (progress counters, prompts redrawn)
            _carriageReturn = false;
            _current.Clear();
        }

        _current.Append(c);
    }

    private void NewLine()
    {
        var line = _current.ToString();
        _current.Clear();
        _lines.AddLast(line);
        _log.Append(line).Append('\n');
        if (_log.Length > MaxLogChars)
            _log.Remove(0, _log.Length - MaxLogChars);
        while (_lines.Count > MaxLines)
        {
            _lines.RemoveFirst();
            TrimmedLines++;
        }
    }

    private string BuildText()
    {
        var sb = new StringBuilder();
        foreach (var line in _lines)
            sb.Append(line).Append('\n');
        sb.Append(_current);
        return sb.ToString();
    }
}

/// <summary>Command line history with Up/Down recall, skipping repeats as Mission Planner's terminal does.</summary>
public sealed class CommandHistory
{
    private readonly List<string> _items = new();
    private int _index;

    public CommandHistory(int capacity = 200) => Capacity = capacity;

    public int Capacity { get; }

    public IReadOnlyList<string> Items => _items;

    public void Add(string command)
    {
        if (command.Length > 0 && (_items.Count == 0 || _items[^1] != command))
        {
            _items.Add(command);
            if (_items.Count > Capacity)
                _items.RemoveAt(0);
        }

        _index = _items.Count;
    }

    /// <summary>The previous (older) command, or null at the oldest.</summary>
    public string? Previous()
    {
        if (_index <= 0)
            return _items.Count > 0 ? _items[0] : null;
        return _items[--_index];
    }

    /// <summary>The next (newer) command; an empty line past the newest.</summary>
    public string? Next()
    {
        if (_index >= _items.Count)
            return null;
        _index++;
        return _index == _items.Count ? "" : _items[_index];
    }
}
