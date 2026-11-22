using System.Diagnostics;
using System.Text;
using MissionPlanner.Comms;

namespace MissionPlanner.App.Core.Links.Usb;

/// <summary>
/// A serial port on a USB device opened through the phone's USB host, for the MAVLink library:
/// a reader thread keeps the bulk IN endpoint drained into a buffer (as the OS does for a
/// desktop serial port), and Read/BytesToRead serve from it.
/// </summary>
public sealed class UsbSerialLink : ICommsSerial
{
    private const int ReadChunk = 16 * 1024;
    private const int PollTimeout = 200;

    private readonly IUsbHost _host;
    private readonly UsbSerialAddress _address;
    private readonly object _gate = new();
    private byte[] _buffer = new byte[64 * 1024];
    private int _start;
    private int _count;

    private IUsbConnection? _connection;
    private UsbSerialDriver? _driver;
    private UsbSerialPortInfo? _port;
    private Thread? _reader;
    private volatile bool _open;
    private int _baud;
    private bool _dtr = true;
    private bool _rts = true;

    public UsbSerialLink(IUsbHost host, string portName, int baud)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _address = UsbSerialAddress.TryParse(portName) ?? throw new ArgumentException($"'{portName}' is not a USB serial address", nameof(portName));
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
                _driver?.SetBaud(value);
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
                _driver?.SetLines(_dtr, _rts);
        }
    }

    public bool RtsEnable
    {
        get => _rts;
        set
        {
            _rts = value;
            if (_open)
                _driver?.SetLines(_dtr, _rts);
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

    /// <summary>The device the link opened, for the connected text.</summary>
    public UsbSerialPortInfo? Device => _port;

    public void Open()
    {
        if (_open)
            return;
        var port = UsbSerialProbe.Find(UsbSerialProbe.Ports(_host.Devices()), _address)
                   ?? throw new IOException("The USB device is not plugged in.");
        if (!_host.HasPermission(port.Device))
            throw new IOException("Mission Planner is not allowed to use this USB device.");
        var connection = _host.Open(port.Device) ?? throw new IOException("The USB device could not be opened.");
        try
        {
            var driver = UsbSerialDriver.For(connection, port);
            driver.Open(_baud, _dtr, _rts);
            _connection = connection;
            _driver = driver;
            _port = port;
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        lock (_gate)
        {
            _start = 0;
            _count = 0;
        }

        _open = true;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "USB serial " + PortName };
        _reader.Start();
    }

    public void Close()
    {
        if (!_open && _connection == null)
            return;
        _open = false;
        lock (_gate)
            Monitor.PulseAll(_gate);
        if (_reader != null && _reader != Thread.CurrentThread)
            _reader.Join(PollTimeout * 3);
        _reader = null;
        try
        {
            _driver?.Close();
        }
        catch (Exception)
        {
            // unplugged
        }

        _connection?.Dispose();
        _connection = null;
        _driver = null;
    }

    public void Dispose() => Close();

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
            for (var i = 0; i < n; i++)
                buffer[offset + i] = _buffer[(_start + i) % _buffer.Length];
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
        var connection = _connection;
        var port = _port;
        if (!_open || connection == null || port == null)
            throw new InvalidOperationException("The USB port is not open.");
        while (count > 0)
        {
            var n = connection.BulkTransfer(port.Out, buffer, offset, Math.Min(count, ReadChunk), WriteTimeout);
            if (n <= 0)
                throw new TimeoutException("The USB device did not take the data.");
            offset += n;
            count -= n;
        }
    }

    public void toggleDTR()
    {
        DtrEnable = false;
        Thread.Sleep(50);
        DtrEnable = true;
    }

    /// <summary>Waits (holding the lock) until bytes are buffered; as SerialPort, times out.</summary>
    private void WaitForData()
    {
        var deadline = Stopwatch.StartNew();
        while (_count == 0)
        {
            if (!_open)
                throw new IOException("The USB port is closed.");
            var left = ReadTimeout < 0 ? Timeout.Infinite : ReadTimeout - (int)deadline.ElapsedMilliseconds;
            if (ReadTimeout >= 0 && left <= 0)
                throw new TimeoutException("No data from the USB device.");
            Monitor.Wait(_gate, left == Timeout.Infinite ? 1000 : Math.Min(left, 1000));
        }
    }

    private void ReadLoop()
    {
        var connection = _connection!;
        var port = _port!;
        var header = _driver!.PacketHeader;
        var packet = Math.Max(port.In.MaxPacketSize, 8);
        var chunk = new byte[ReadChunk];
        var quickFailures = 0;
        while (_open)
        {
            var timer = Stopwatch.StartNew();
            var n = connection.BulkTransfer(port.In, chunk, 0, chunk.Length, PollTimeout);
            if (n < 0)
            {
                // a timeout takes the whole wait; failing at once over and over is a device gone
                if (timer.ElapsedMilliseconds < PollTimeout / 2 && ++quickFailures >= 5)
                {
                    _open = false;
                    lock (_gate)
                        Monitor.PulseAll(_gate);
                    return;
                }

                if (timer.ElapsedMilliseconds >= PollTimeout / 2)
                    quickFailures = 0;
                continue;
            }

            quickFailures = 0;
            if (n == 0)
                continue;
            if (header > 0)
                n = StripHeaders(chunk, n, packet, header);
            if (n > 0)
                Append(chunk, n);
        }
    }

    /// <summary>Removes the status bytes at the start of each USB packet; returns the data length.</summary>
    public static int StripHeaders(byte[] data, int length, int packetSize, int header)
    {
        var write = 0;
        for (var packetStart = 0; packetStart < length; packetStart += packetSize)
        {
            var from = packetStart + header;
            var end = Math.Min(packetStart + packetSize, length);
            if (from >= end)
                continue;
            Buffer.BlockCopy(data, from, data, write, end - from);
            write += end - from;
        }

        return write;
    }

    private void Append(byte[] data, int n)
    {
        lock (_gate)
        {
            if (_count + n > _buffer.Length)
            {
                // the reader is far behind: keep the newest bytes, as a full serial buffer drops
                var drop = _count + n - _buffer.Length;
                if (drop >= _count)
                {
                    _start = 0;
                    _count = 0;
                    var skip = n - _buffer.Length;
                    if (skip > 0)
                    {
                        Buffer.BlockCopy(data, skip, data, 0, _buffer.Length);
                        n = _buffer.Length;
                    }
                }
                else
                {
                    _start = (_start + drop) % _buffer.Length;
                    _count -= drop;
                }
            }

            var end = (_start + _count) % _buffer.Length;
            var first = Math.Min(n, _buffer.Length - end);
            Buffer.BlockCopy(data, 0, _buffer, end, first);
            if (n > first)
                Buffer.BlockCopy(data, first, _buffer, 0, n - first);
            _count += n;
            Monitor.PulseAll(_gate);
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

    private sealed class LinkStream(UsbSerialLink link) : Stream
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
