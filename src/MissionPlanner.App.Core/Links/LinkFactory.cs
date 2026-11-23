using MissionPlanner.App.Core.Links.Usb;
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
    /// <summary>
    /// The phone's USB host when the app runs on Android; serial links named "usb:..." open
    /// through it (<see cref="UsbSerialLink"/>). Null on the desktop.
    /// </summary>
    public static IUsbHost? UsbHost { get; set; }

    /// <summary>
    /// The phone's Bluetooth (Android, iOS); serial links named "bt:..." or "ble:..." open
    /// through it (<see cref="Bluetooth.BluetoothLink"/>). Null on the desktop, where paired
    /// Bluetooth serial devices are ordinary serial ports.
    /// </summary>
    public static Bluetooth.IBluetoothHost? BluetoothHost { get; set; }

    public static ICommsSerial Create(LinkSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        return spec.Kind switch
        {
            LinkKind.Serial when UsbHost != null && UsbSerialAddress.IsUsb(spec.Address) => new UsbSerialLink(UsbHost, spec.Address, spec.Value),
            LinkKind.Serial when BluetoothHost != null && Bluetooth.BluetoothAddress.IsBluetooth(spec.Address) => new Bluetooth.BluetoothLink(BluetoothHost, spec.Address, spec.Value),
            LinkKind.Serial => new SerialLink(spec.Address, spec.Value),
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

    /// <summary>
    /// Mission Planner's serial port, except that closing never hangs: once the USB device is
    /// unplugged, closing the still-"open" port blocks indefinitely in the runtime, and the library
    /// closes the old port whenever a new one is assigned. Close and Dispose give up waiting after a
    /// second and let the stuck close finish (or not) on its own thread.
    /// </summary>
    internal sealed class SerialLink : SerialPort, ICommsSerial
    {
        private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(1);

        public SerialLink(string port, int baud)
            : base(port, baud)
        {
        }

        void ICommsSerial.Close() => Bounded(Close);

        void IDisposable.Dispose() => Bounded(Dispose);

        private static void Bounded(Action close)
        {
            var task = Task.Run(() =>
            {
                try
                {
                    close();
                }
                catch (Exception)
                {
                    // a vanished device can throw anything on close; it is gone either way
                }
            });
            task.Wait(CloseWait);
        }
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
