using System.Net.Sockets;
using MissionPlanner.Comms;

namespace MissionPlanner.App.Core.Links;

/// <summary>
/// Detects links that are gone even though the transport still reports itself open: an unplugged
/// USB serial device (the port object stays "open" and reads just stop) and a TCP connection the
/// other end closed (the socket stays "connected" until a write fails). UDP has no connection, so
/// for it only the absence of packets tells, which the session reports separately.
/// </summary>
public static class LinkHealth
{
    /// <summary>Why the link is gone, or null while it looks alive.</summary>
    public static string? Check(LinkSpec spec, ICommsSerial? stream, Func<string, bool>? portExists = null)
    {
        switch (spec.Kind)
        {
            case LinkKind.Serial:
                if (!(portExists ?? SerialPortExists)(spec.Address) || SerialPortDead(stream))
                    return $"{Path.GetFileName(spec.Address)} was disconnected (the USB device is gone).";
                return null;

            case LinkKind.Tcp when stream is TcpSerial tcp:
                return TcpPeerClosed(tcp.client) ? "The remote end closed the TCP connection." : null;

            default:
                return null;
        }
    }

    /// <summary>Whether a serial device is still present.</summary>
    public static bool SerialPortExists(string port)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return System.IO.Ports.SerialPort.GetPortNames().Any(p => string.Equals(p, port, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception)
            {
                return true; // can't tell: don't drop a working link
            }
        }

        // device nodes (and the /dev/serial/by-id links udev makes) vanish when the device is unplugged
        return File.Exists(port);
    }

    /// <summary>
    /// After the device goes away the runtime keeps the port "open", but its byte count turns
    /// negative (the driver call fails) or throws.
    /// </summary>
    public static bool SerialPortDead(ICommsSerial? stream)
    {
        if (stream == null)
            return false;
        try
        {
            return stream.IsOpen && stream.BytesToRead < 0;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>A socket that polls readable with nothing to read has received the peer's close.</summary>
    public static bool TcpPeerClosed(TcpClient? client)
    {
        try
        {
            var socket = client?.Client;
            if (socket == null)
                return true;
            return socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
        catch (SocketException)
        {
            return true;
        }
    }
}
