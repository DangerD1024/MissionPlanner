namespace MissionPlanner.App.Core.Links;

public enum LinkKind
{
    /// <summary>Serial port or USB CDC device; <see cref="LinkSpec.Address"/> is the port name.</summary>
    Serial,

    /// <summary>TCP client connecting to a vehicle or SITL (default 127.0.0.1:5760).</summary>
    Tcp,

    /// <summary>Listen for UDP packets on a local port (default 14550).</summary>
    UdpListen,

    /// <summary>Send to and receive from a remote UDP endpoint.</summary>
    UdpConnect,
}

/// <summary>
/// Everything needed to open one link, without any prompting.
/// <see cref="Value"/> is the baud rate for serial links and the port number for network links.
/// </summary>
public sealed record LinkSpec(LinkKind Kind, string Address, int Value)
{
    public static LinkSpec Serial(string port, int baud = 115200) => new(LinkKind.Serial, port, baud);

    public static LinkSpec Tcp(string host = "127.0.0.1", int port = 5760) => new(LinkKind.Tcp, host, port);

    public static LinkSpec UdpListen(int port = 14550) => new(LinkKind.UdpListen, "", port);

    public static LinkSpec UdpConnect(string host, int port = 14550) => new(LinkKind.UdpConnect, host, port);

    /// <summary>
    /// Parses "tcp:host:port", "udp:port", "udpout:host:port" or "serial:port[:baud]".
    /// A bare value is treated as a serial port name.
    /// </summary>
    public static LinkSpec Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        text = text.Trim();

        var colon = text.IndexOf(':');
        var scheme = colon > 0 ? text[..colon].ToLowerInvariant() : "";
        var rest = colon > 0 ? text[(colon + 1)..] : text;

        switch (scheme)
        {
            case "tcp":
            {
                var (host, port) = SplitHostPort(rest, 5760);
                return Tcp(host, port);
            }
            case "udp":
                return UdpListen(rest.Length == 0 ? 14550 : ParsePort(rest));
            case "udpout":
            {
                var (host, port) = SplitHostPort(rest, 14550);
                return UdpConnect(host, port);
            }
            case "serial":
                text = rest;
                break;
        }

        // serial: the port name itself may contain ':' on no platform we support, so the last
        // segment is a baud rate only when it is numeric
        var last = text.LastIndexOf(':');
        if (last > 0 && int.TryParse(text[(last + 1)..], out var baud) && baud > 0)
            return Serial(text[..last], baud);

        return Serial(text);
    }

    public override string ToString() => Kind switch
    {
        LinkKind.Serial => $"serial:{Address}:{Value}",
        LinkKind.Tcp => $"tcp:{Address}:{Value}",
        LinkKind.UdpListen => $"udp:{Value}",
        LinkKind.UdpConnect => $"udpout:{Address}:{Value}",
        _ => base.ToString()!,
    };

    private static (string Host, int Port) SplitHostPort(string text, int defaultPort)
    {
        var last = text.LastIndexOf(':');
        if (last < 0)
            return (text.Length == 0 ? "127.0.0.1" : text, defaultPort);
        var host = text[..last];
        return (host.Length == 0 ? "127.0.0.1" : host, ParsePort(text[(last + 1)..]));
    }

    private static int ParsePort(string text)
    {
        if (!int.TryParse(text, out var port) || port is < 1 or > 65535)
            throw new FormatException($"'{text}' is not a valid port number");
        return port;
    }
}
