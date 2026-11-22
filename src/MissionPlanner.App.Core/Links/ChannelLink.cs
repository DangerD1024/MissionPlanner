using System.Diagnostics;
using System.Text;
using MissionPlanner.Comms;

namespace MissionPlanner.App.Core.Links;

/// <summary>
/// A link whose bytes arrive on their own (a USB reader thread, Bluetooth notifications) for the
/// MAVLink library, which reads a serial port: incoming data waits in a buffer, as the OS keeps
/// it for a desktop serial port, and Read/BytesToRead serve from it.
/// </summary>
public abstract class ChannelLink : ICommsSerial
{
    private readonly object _gate = new();
    private byte[] _buffer = new byte[64 * 1024];
    private int _start;
    private int _count;
    private volatile bool _open;
    private bool _dtr = true;
    private bool _rts = true;
    private int _baud;

    protected ChannelLink(string portName, int baud)
    {
        PortName = portName;
        _baud = baud;
    }

    public Stream BaseStream => new LinkStream(this);

    public int BaudRate
    {
        get => _baud;
        set
        {
            _baud = value;
            if (_open)
                OnBaudChanged(value);
        }
    }

    public int BytesToRead
    {
        get
        {
            lock (_gate)
                return _count;
        }
    }

    public int BytesToWrite => 0;

    public int DataBits { get; set; } = 8;

    public bool DtrEnable
    {
        get => _dtr;
        set
        {
            _dtr = value;
            if (_open)
                OnLinesChanged(_dtr, _rts);
        }
    }

    public bool RtsEnable
    {
        get => _rts;
        set
        {
            _rts = value;
            if (_open)
                OnLinesChanged(_dtr, _rts);
        }
    }

    public bool IsOpen => _open;

    public string PortName { get; set; }

    public int ReadBufferSize
    {
        get => _buffer.Length;
        set
        {
            lock (_gate)
            {
                if (value > _buffer.Length)
                    Grow(value);
            }
        }
    }

    public int ReadTimeout { get; set; } = 500;

    public int WriteBufferSize { get; set; } = 4096;

    public int WriteTimeout { get; set; } = 1000;

    public void Open()
    {
        if (_open)
            return;
        lock (_gate)
        {
            _start = 0;
            _count = 0;
        }

        OpenChannel();
        _open = true;
        Started();
    }

    public void Close()
    {
        var wasOpen = _open;
        _open = false;
        lock (_gate)
            Monitor.PulseAll(_gate);
        CloseChannel(wasOpen);
    }

    public void Dispose()
    {
        Close();
        GC.SuppressFinalize(this);
    }

    /// <summary>Connects; throws <see cref="IOException"/> when it cannot. Data may arrive only after <see cref="Started"/>.</summary>
    protected abstract void OpenChannel();

    /// <summary>Called once the link counts as open: start delivering data.</summary>
    protected virtual void Started()
    {
    }

    /// <summary>Disconnects and frees the channel (also after <see cref="Lost"/>).</summary>
    protected abstract void CloseChannel(bool wasOpen);

    protected abstract void WriteChannel(byte[] buffer, int offset, int count);

    protected virtual void OnBaudChanged(int baud)
    {
    }

    protected virtual void OnLinesChanged(bool dtr, bool rts)
    {
    }

    /// <summary>The device went away: the link closes, so the session's monitor reopens it when it is back.</summary>
    protected void Lost()
    {
        _open = false;
        lock (_gate)
            Monitor.PulseAll(_gate);
    }

    public void DiscardInBuffer()
    {
        lock (_gate)
        {
            _start = 0;
            _count = 0;
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (count == 0)
            return 0;
        lock (_gate)
        {
            WaitForData();
            var n = Math.Min(count, _count);
            var first = Math.Min(n, _buffer.Length - _start);
            Buffer.BlockCopy(_buffer, _start, buffer, offset, first);
            if (n > first)
                Buffer.BlockCopy(_buffer, 0, buffer, offset + first, n - first);
            _start = (_start + n) % _buffer.Length;
            _count -= n;
            return n;
        }
    }

    public int ReadByte()
    {
        lock (_gate)
        {
            WaitForData();
            var b = _buffer[_start];
            _start = (_start + 1) % _buffer.Length;
            _count--;
            return b;
        }
    }

    public int ReadChar() => ReadByte();

    public string ReadExisting()
    {
        var data = new byte[BytesToRead];
        var n = data.Length == 0 ? 0 : Read(data, 0, data.Length);
        return Encoding.ASCII.GetString(data, 0, n);
    }

    public string ReadLine()
    {
        var line = new StringBuilder();
        while (true)
        {
            var c = ReadByte();
            if (c == '\n')
                return line.ToString().TrimEnd('\r');
            line.Append((char)c);
        }
    }

    public void Write(string text)
    {
        var data = Encoding.ASCII.GetBytes(text);
        Write(data, 0, data.Length);
    }

    public void WriteLine(string text) => Write(text + "\n");

    public void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (!_open)
            throw new InvalidOperationException("The link is not open.");
        if (count > 0)
            WriteChannel(buffer, offset, count);
    }

    public void toggleDTR()
    {
        DtrEnable = false;
        Thread.Sleep(50);
        DtrEnable = true;
    }

    /// <summary>Adds received bytes; when the reader is far behind the oldest are dropped, as a full serial buffer does.</summary>
    protected void Received(byte[] data, int offset, int n)
    {
        if (n <= 0)
            return;
        lock (_gate)
        {
            if (n >= _buffer.Length)
            {
                offset += n - _buffer.Length;
                n = _buffer.Length;
                _start = 0;
                _count = 0;
            }
            else if (_count + n > _buffer.Length)
            {
                var drop = _count + n - _buffer.Length;
                _start = (_start + drop) % _buffer.Length;
                _count -= drop;
            }

            var end = (_start + _count) % _buffer.Length;
            var first = Math.Min(n, _buffer.Length - end);
            Buffer.BlockCopy(data, offset, _buffer, end, first);
            if (n > first)
                Buffer.BlockCopy(data, offset + first, _buffer, 0, n - first);
            _count += n;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Waits (holding the lock) until bytes are buffered; as SerialPort, times out.</summary>
    private void WaitForData()
    {
        var waited = Stopwatch.StartNew();
        while (_count == 0)
        {
            if (!_open)
                throw new IOException("The link is closed.");
            if (ReadTimeout < 0)
            {
                Monitor.Wait(_gate, 1000);
                continue;
            }

            var left = ReadTimeout - (int)waited.ElapsedMilliseconds;
            if (left <= 0)
                throw new TimeoutException("No data on the link.");
            Monitor.Wait(_gate, Math.Min(left, 1000));
        }
    }

    private void Grow(int size)
    {
        var bigger = new byte[size];
        for (var i = 0; i < _count; i++)
            bigger[i] = _buffer[(_start + i) % _buffer.Length];
        _buffer = bigger;
        _start = 0;
    }

    private sealed class LinkStream(ChannelLink link) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => link.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => link.Write(buffer, offset, count);
    }
}
