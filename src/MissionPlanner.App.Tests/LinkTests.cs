using MissionPlanner.App.Core.Links;
using MissionPlanner.Comms;

namespace MissionPlanner.App.Tests;

[TestClass]
public class LinkTests
{
    [TestMethod]
    [DataRow("tcp:127.0.0.1:5760", LinkKind.Tcp, "127.0.0.1", 5760)]
    [DataRow("tcp:sitl.local:5762", LinkKind.Tcp, "sitl.local", 5762)]
    [DataRow("tcp:", LinkKind.Tcp, "127.0.0.1", 5760)]
    [DataRow("udp:14550", LinkKind.UdpListen, "", 14550)]
    [DataRow("udp:", LinkKind.UdpListen, "", 14550)]
    [DataRow("udpout:192.168.4.1:14555", LinkKind.UdpConnect, "192.168.4.1", 14555)]
    [DataRow("/dev/ttyACM0", LinkKind.Serial, "/dev/ttyACM0", 115200)]
    [DataRow("/dev/ttyUSB0:57600", LinkKind.Serial, "/dev/ttyUSB0", 57600)]
    [DataRow("serial:COM3:921600", LinkKind.Serial, "COM3", 921600)]
    [DataRow("COM7", LinkKind.Serial, "COM7", 115200)]
    public void ParseRecognisesEachForm(string text, LinkKind kind, string address, int value)
    {
        var spec = LinkSpec.Parse(text);

        Assert.AreEqual(kind, spec.Kind);
        Assert.AreEqual(address, spec.Address);
        Assert.AreEqual(value, spec.Value);
    }

    [TestMethod]
    public void ToStringRoundTrips()
    {
        foreach (var spec in new[]
                 {
                     LinkSpec.Tcp("10.0.0.2", 5763), LinkSpec.UdpListen(14551),
                     LinkSpec.UdpConnect("10.0.0.3", 14556), LinkSpec.Serial("/dev/ttyACM1", 57600),
                 })
        {
            Assert.AreEqual(spec, LinkSpec.Parse(spec.ToString()));
        }
    }

    [TestMethod]
    public void ParseRejectsBadPort()
    {
        Assert.ThrowsExactly<FormatException>(() => LinkSpec.Parse("tcp:host:99999"));
        Assert.ThrowsExactly<FormatException>(() => LinkSpec.Parse("udp:abc"));
    }

    [TestMethod]
    public void FactoryCreatesTheMatchingTransport()
    {
        Assert.IsInstanceOfType<TcpSerial>(LinkFactory.Create(LinkSpec.Tcp()));
        Assert.IsInstanceOfType<UdpSerial>(LinkFactory.Create(LinkSpec.UdpListen()));
        Assert.IsInstanceOfType<UdpSerialConnect>(LinkFactory.Create(LinkSpec.UdpConnect("127.0.0.1")));

        var serial = LinkFactory.Create(LinkSpec.Serial("/dev/null-port", 57600));
        Assert.IsInstanceOfType<SerialPort>(serial);
        Assert.AreEqual(57600, serial.BaudRate);
        Assert.AreEqual("/dev/null-port", serial.PortName);
    }

    [TestMethod]
    [Timeout(15000, CooperativeCancellation = true)]
    public async Task UdpListenOpensWithoutPromptingAndReceives()
    {
        // a handler that would cancel the open: the factory's transport must not call it
        var prompted = false;
        InputBoxShow handler = (string _, string _, ref string _) =>
        {
            prompted = true;
            return inputboxreturn.Cancel;
        };
        CommsBase.InputBoxShow += handler;
        try
        {
            var port = FreeUdpPort();
            using var listen = (UdpSerial)LinkFactory.Create(LinkSpec.UdpListen(port));

            // UdpSerial.Open waits for the first datagram
            using var sender = new System.Net.Sockets.UdpClient();
            using var stop = new CancellationTokenSource();
            var sending = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    await sender.SendAsync(new byte[] { 0xFD, 1, 2, 3 }, 4, "127.0.0.1", port);
                    await Task.Delay(100);
                }
            });

            await Task.Run(listen.Open);
            stop.Cancel();
            await sending;

            Assert.IsFalse(prompted, "UDP listen asked for input");
            Assert.IsTrue(listen.IsOpen, "UDP listen did not open");
            Assert.IsGreaterThan(0, listen.BytesToRead);
        }
        finally
        {
            CommsBase.InputBoxShow -= handler;
        }
    }

    private static int FreeUdpPort()
    {
        using var probe = new System.Net.Sockets.UdpClient(0);
        return ((System.Net.IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }
}

[TestClass]
public class SerialPortFilterTests
{
    [TestMethod]
    [DataRow("/dev/tty", true)]
    [DataRow("/dev/tty0", true)]
    [DataRow("/dev/tty12", true)]
    [DataRow("/dev/console", true)]
    [DataRow("/dev/ttyACM0", false)]
    [DataRow("/dev/ttyUSB1", false)]
    [DataRow("/dev/ttyS0", false)]
    [DataRow("/dev/serial/by-id/usb-ArduPilot_X-if00", false)]
    [DataRow("COM3", false)]
    public void ConsoleDevicesAreNotOffered(string port, bool console) =>
        Assert.AreEqual(console, LinkFactory.IsConsoleDevice(port));
}
