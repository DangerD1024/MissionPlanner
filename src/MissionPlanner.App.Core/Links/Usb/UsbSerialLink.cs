using System.Diagnostics;

namespace MissionPlanner.App.Core.Links.Usb;

/// <summary>
/// A serial port on a USB device opened through the phone's USB host: a reader thread keeps
/// the bulk IN endpoint drained into the link's buffer.
/// </summary>
public sealed class UsbSerialLink : ChannelLink
{
    private const int ReadChunk = 16 * 1024;
    private const int PollTimeout = 200;

    private readonly IUsbHost _host;
    private readonly UsbSerialAddress _address;
    private IUsbConnection? _connection;
    private UsbSerialDriver? _driver;
    private UsbSerialPortInfo? _port;
    private Thread? _reader;

    public UsbSerialLink(IUsbHost host, string portName, int baud)
        : base(portName, baud)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _address = UsbSerialAddress.TryParse(portName) ?? throw new ArgumentException($"'{portName}' is not a USB serial address", nameof(portName));
    }

    /// <summary>The device the link opened.</summary>
    public UsbSerialPortInfo? Device => _port;

    protected override void OpenChannel()
    {
        var port = UsbSerialProbe.Find(UsbSerialProbe.Ports(_host.Devices()), _address)
                   ?? throw new IOException("The USB device is not plugged in.");
        if (!_host.HasPermission(port.Device))
            throw new IOException("Mission Planner is not allowed to use this USB device.");
        var connection = _host.Open(port.Device) ?? throw new IOException("The USB device could not be opened.");
        try
        {
            var driver = UsbSerialDriver.For(connection, port);
            driver.Open(BaudRate, DtrEnable, RtsEnable);
            _connection = connection;
            _driver = driver;
            _port = port;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    protected override void Started()
    {
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "USB serial " + PortName };
        _reader.Start();
    }

    protected override void CloseChannel(bool wasOpen)
    {
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

    protected override void OnBaudChanged(int baud) => _driver?.SetBaud(baud);

    protected override void OnLinesChanged(bool dtr, bool rts) => _driver?.SetLines(dtr, rts);

    protected override void WriteChannel(byte[] buffer, int offset, int count)
    {
        var connection = _connection ?? throw new InvalidOperationException("The USB port is not open.");
        var port = _port!;
        while (count > 0)
        {
            var n = connection.BulkTransfer(port.Out, buffer, offset, Math.Min(count, ReadChunk), WriteTimeout);
            if (n <= 0)
                throw new TimeoutException("The USB device did not take the data.");
            offset += n;
            count -= n;
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
        while (IsOpen)
        {
            var timer = Stopwatch.StartNew();
            var n = connection.BulkTransfer(port.In, chunk, 0, chunk.Length, PollTimeout);
            if (n < 0)
            {
                // a timeout takes the whole wait; failing at once over and over is a device gone
                if (timer.ElapsedMilliseconds < PollTimeout / 2)
                {
                    if (++quickFailures >= 5)
                    {
                        Lost();
                        return;
                    }
                }
                else
                {
                    quickFailures = 0;
                }

                continue;
            }

            quickFailures = 0;
            if (header > 0)
                n = StripHeaders(chunk, n, packet, header);
            Received(chunk, 0, n);
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
}
