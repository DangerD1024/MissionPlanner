using MissionPlanner.Comms;

namespace MissionPlanner.App.Core.Links;

/// <summary>
/// Creates an unopened <see cref="ICommsSerial"/> for a <see cref="LinkSpec"/>.
/// The network transports in ExtLibs/Comms read their host and port back through
/// <c>CommsBase.OnSettings</c> and may prompt through <c>CommsBase.OnInputBoxShow</c>; the
/// subclasses here answer both from the spec, so opening a link never blocks on UI.
/// </summary>
public static class LinkFactory
{
    public static ICommsSerial Create(LinkSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        return spec.Kind switch
        {
            LinkKind.Serial => new SerialPort(spec.Address, spec.Value),
            LinkKind.Tcp => new FixedTcp(spec.Address, spec.Value),
            LinkKind.UdpListen => new FixedUdpListen(spec.Value),
            LinkKind.UdpConnect => new FixedUdpConnect(spec.Address, spec.Value),
            _ => throw new ArgumentOutOfRangeException(nameof(spec), spec.Kind, "unknown link kind"),
        };
    }

    /// <summary>
    /// Serial ports the OS reports, plus stable /dev/serial/by-id names on Linux. Console devices
    /// (/dev/tty, /dev/console, /dev/ttyN virtual terminals) are never vehicles and are left out.
    /// </summary>
    public static IReadOnlyList<string> GetSerialPortNames() =>
        SerialPort.GetPortNames().Where(p => !IsConsoleDevice(p)).ToList();

    internal static bool IsConsoleDevice(string port) =>
        port is "/dev/tty" or "/dev/console" or "/dev/ptmx" ||
        (port.StartsWith("/dev/tty", StringComparison.Ordinal) && port.Length > 8 && port[8..].All(char.IsDigit));

    private static string? Answer(string name, string host, string port)
    {
        if (name.StartsWith("TCP_host", StringComparison.Ordinal) || name.StartsWith("UDP_host", StringComparison.Ordinal))
            return host;
        if (name.StartsWith("TCP_port", StringComparison.Ordinal) || name.StartsWith("UDP_port", StringComparison.Ordinal))
            return port;
        return null;
    }

    private sealed class FixedTcp : TcpSerial
    {
        private readonly string _host;
        private readonly string _port;

        public FixedTcp(string host, int port)
        {
            _host = host;
            _port = port.ToString();
            Host = host;
            Port = _port;
        }

        protected override string OnSettings(string name, string value, bool set = false) =>
            Answer(name, _host, _port) ?? value;

        protected override inputboxreturn OnInputBoxShow(string title, string prompttext, ref string text) =>
            inputboxreturn.NotSet;
    }

    private sealed class FixedUdpListen : UdpSerial
    {
        private readonly string _port;

        public FixedUdpListen(int port)
        {
            _port = port.ToString();
            Port = _port;
        }

        protected override string OnSettings(string name, string value, bool set = false) =>
            Answer(name, "", _port) ?? value;

        protected override inputboxreturn OnInputBoxShow(string title, string prompttext, ref string text) =>
            inputboxreturn.NotSet;
    }

    private sealed class FixedUdpConnect : UdpSerialConnect
    {
        private readonly string _host;
        private readonly string _port;

        public FixedUdpConnect(string host, int port)
        {
            _host = host;
            _port = port.ToString();
            Port = _port;
        }

        protected override string OnSettings(string name, string value, bool set = false) =>
            Answer(name, _host, _port) ?? value;

        protected override inputboxreturn OnInputBoxShow(string title, string prompttext, ref string text) =>
            inputboxreturn.NotSet;
    }
}
