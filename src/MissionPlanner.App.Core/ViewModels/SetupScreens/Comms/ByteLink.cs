using System.Text;
using MissionPlanner.Comms;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Comms;

/// <summary>
/// A raw byte pipe the comms tools talk through: a serial port on this computer, or a port on the
/// vehicle reached with SERIAL_CONTROL. Reads wait at most the given time, so a closed or silent
/// link never hangs a worker thread.
/// </summary>
public interface IByteLink : IDisposable
{
    string Name { get; }

    bool IsOpen { get; }

    void Open();

    void Close();

    void Write(byte[] data, int offset, int count);

    /// <summary>Waits up to <paramref name="timeoutMs"/> for data; returns the bytes read, 0 when none came.</summary>
    int Read(byte[] buffer, int offset, int count, int timeoutMs);

    /// <summary>Drops anything received but not read yet.</summary>
    void DiscardInput();
}

public static class ByteLinkExtensions
{
    /// <summary>Text on these links is 8-bit: Latin-1 keeps every byte as one character both ways.</summary>
    public static readonly Encoding Text = Encoding.Latin1;

    public static void Write(this IByteLink link, string text)
    {
        var bytes = Text.GetBytes(text);
        link.Write(bytes, 0, bytes.Length);
    }
}

/// <summary>A serial port (or any <see cref="ICommsSerial"/>) as an <see cref="IByteLink"/>.</summary>
public sealed class CommsByteLink : IByteLink
{
    private readonly ICommsSerial _port;

    public CommsByteLink(ICommsSerial port, string name)
    {
        _port = port;
        Name = name;
    }

    /// <summary>A local serial port, not opened yet.</summary>
    public static CommsByteLink Serial(string portName, int baud) =>
        new(LinkFactory.Create(LinkSpec.Serial(portName, baud)), $"{portName} @ {baud}");

    public string Name { get; }

    public bool IsOpen
    {
        get
        {
            try
            {
                return _port.IsOpen;
            }
            catch
            {
                return false;
            }
        }
    }

    public void Open()
    {
        if (!_port.IsOpen)
            _port.Open();
    }

    public void Close()
    {
        try
        {
            if (_port.IsOpen)
                _port.Close();
        }
        catch
        {
            // already gone (USB unplugged)
        }
    }

    public void Write(byte[] data, int offset, int count) => _port.Write(data, offset, count);

    public int Read(byte[] buffer, int offset, int count, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var available = _port.BytesToRead;
            if (available > 0)
                return _port.Read(buffer, offset, Math.Min(count, available));
            if (Environment.TickCount64 >= deadline || !_port.IsOpen)
                return 0;
            Thread.Sleep(2);
        }
    }

    public void DiscardInput()
    {
        try
        {
            _port.DiscardInBuffer();
        }
        catch
        {
            // nothing to discard on a closed port
        }
    }

    public void Dispose()
    {
        Close();
        try
        {
            _port.Dispose();
        }
        catch
        {
            // closing twice is harmless
        }
    }
}
