using System.Text;
using MissionPlanner.App.Core.ViewModels.SetupScreens;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Comms;

namespace MissionPlanner.App.Tests.SetupScreens.Comms;

[TestClass]
public sealed class SerialControlLinkTests
{
    private const byte Exclusive = (byte)MAVLink.SERIAL_CONTROL_FLAG.EXCLUSIVE;
    private const byte Respond = (byte)MAVLink.SERIAL_CONTROL_FLAG.RESPOND;
    private const byte Multi = (byte)MAVLink.SERIAL_CONTROL_FLAG.MULTI;

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task OpenTakesThePortAndPollsForOutput()
    {
        var vehicle = new FakeSerialControlVehicle();
        using var link = new SerialControlLink(vehicle, MAVLink.SERIAL_CONTROL_DEV.SHELL) { FastPollMs = 20, IdlePollMs = 40 };
        link.Open();

        Assert.IsTrue(vehicle.Subscribed);
        var first = vehicle.Sent.First();
        Assert.AreEqual((byte)MAVLink.SERIAL_CONTROL_DEV.SHELL, first.device);
        Assert.AreEqual(Exclusive | Respond | Multi, first.flags);
        Assert.AreEqual(0, first.count);

        // output appearing later is fetched by the polls
        vehicle.Output.Append(Encoding.ASCII.GetBytes("nsh> "));
        var buffer = new byte[64];
        var n = await Task.Run(() => link.Read(buffer, 0, buffer.Length, 2000));
        Assert.AreEqual("nsh> ", Encoding.ASCII.GetString(buffer, 0, n));
        await Wait.UntilAsync(() => vehicle.Sent.Count >= 3, what: "repeated polls");
    }

    [TestMethod]
    public void WritesGoOutIn70BytePackets()
    {
        var vehicle = new FakeSerialControlVehicle { Echo = false };
        using var link = new SerialControlLink(vehicle, MAVLink.SERIAL_CONTROL_DEV.SHELL) { IdlePollMs = 100000, FastPollMs = 100000 };
        link.Open();
        var before = vehicle.Sent.Count;

        var text = new string('x', 100) + "\r";
        link.Write(text);

        var writes = vehicle.Sent.Skip(before).Where(p => p.count > 0).ToList();
        Assert.HasCount(2, writes);
        Assert.AreEqual(70, writes[0].count);
        Assert.AreEqual(31, writes[1].count);
        Assert.IsTrue(writes.All(p => (p.flags & Exclusive) != 0 && (p.flags & Respond) != 0));
        Assert.AreEqual(text, vehicle.InputText);
    }

    [TestMethod]
    public void RepliesForOtherDevicesAreIgnored()
    {
        var vehicle = new FakeSerialControlVehicle { ReplyDevice = (byte)MAVLink.SERIAL_CONTROL_DEV.TELEM1 };
        using var link = new SerialControlLink(vehicle, MAVLink.SERIAL_CONTROL_DEV.SHELL) { IdlePollMs = 20, FastPollMs = 20 };
        link.Open();
        vehicle.Output.Append("abc"u8);
        var n = link.Read(new byte[16], 0, 16, 300);
        Assert.AreEqual(0, n);
    }

    [TestMethod]
    public void CloseHandsThePortBack()
    {
        var vehicle = new FakeSerialControlVehicle();
        var link = new SerialControlLink(vehicle, MAVLink.SERIAL_CONTROL_DEV.SERIAL_CONTROL_SERIAL2, 57600);
        link.Open();
        Assert.AreEqual(57600u, vehicle.Sent.First().baudrate, "the first request sets the baud rate");
        link.Close();

        var last = vehicle.Sent.Last();
        Assert.AreEqual(0, last.flags, "a request without EXCLUSIVE releases the port");
        Assert.AreEqual((byte)MAVLink.SERIAL_CONTROL_DEV.SERIAL_CONTROL_SERIAL2, last.device);
        Assert.IsFalse(vehicle.Subscribed);
        Assert.IsFalse(link.IsOpen);
        Assert.AreEqual("SERIAL2", link.Name);
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task MavlinkShellNeedsAVehicleLink()
    {
        var ui = new CommsUi();
        var services = ui.Services();
        var vehicle = new FakeSerialControlVehicle();
        var vm = new TerminalViewModel(services, () => vehicle, (_, _) => throw new InvalidOperationException("no serial"));

        // the MAVLink shell needs a vehicle link: without one the page says so and stays closed
        await vm.OpenCommand.ExecuteAsync(null);
        Assert.IsFalse(vm.IsOpen);
        StringAssert.Contains(ui.Messages.Single(), "Connect to the vehicle first");
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task TerminalOnASerialPortEchoesHistoryAndSavesTheLog()
    {
        var ui = new CommsUi();
        var services = ui.Services();
        var port = new LoopbackLink();
        var vm = new TerminalViewModel(services, () => throw new InvalidOperationException("no vehicle"), (_, _) => port)
        {
            IsSerialMode = true,
        };
        vm.SelectedPort ??= "/dev/ttyFAKE0";

        await vm.OpenCommand.ExecuteAsync(null);
        Assert.IsTrue(vm.IsOpen, vm.Status);
        Assert.IsTrue(port.IsOpen);

        vm.CommandText = "help";
        await vm.SendCommand.ExecuteAsync(null);
        Assert.AreEqual("help\r", port.Written, "CR line ending by default, as Mission Planner");
        Assert.AreEqual("", vm.CommandText);

        port.Reply("\x1b[32mok\x1b[0m\r\nnsh> ");
        await Wait.UntilAsync(() =>
        {
            vm.Tick();
            return vm.Output.Contains("ok\nnsh> ");
        }, what: "the reply in the output");

        vm.CommandText = "+++";
        await vm.SendCommand.ExecuteAsync(null);
        StringAssert.EndsWith(port.Written, "help\r+++", "+++ goes out bare");

        vm.HistoryUp();
        Assert.AreEqual("+++", vm.CommandText);
        vm.HistoryUp();
        Assert.AreEqual("help", vm.CommandText);
        vm.HistoryDown();
        Assert.AreEqual("+++", vm.CommandText);

        var path = Path.Combine(Path.GetTempPath(), $"terminal-test-{Guid.NewGuid():N}.txt");
        ui.SavePath = path;
        try
        {
            await vm.SaveLogCommand.ExecuteAsync(null);
            StringAssert.Contains(await File.ReadAllTextAsync(path), "ok\n");
        }
        finally
        {
            File.Delete(path);
        }

        vm.OnHidden();
        await Wait.UntilAsync(() => !vm.IsOpen && port.Disposed, what: "the port to close when the page is left");
        StringAssert.Contains(vm.Output, "*** Closed when the page was left.");
    }

    /// <summary>A serial port stand-in: records writes, plays back replies.</summary>
    private sealed class LoopbackLink : IByteLink
    {
        private readonly ByteQueue _rx = new();
        private readonly StringBuilder _written = new();

        public string Name => "loopback";

        public bool IsOpen { get; private set; }

        public bool Disposed { get; private set; }

        public string Written
        {
            get
            {
                lock (_written)
                    return _written.ToString();
            }
        }

        public void Reply(string text) => _rx.Append(Encoding.Latin1.GetBytes(text));

        public void Open() => IsOpen = true;

        public void Close() => IsOpen = false;

        public void Write(byte[] data, int offset, int count)
        {
            lock (_written)
                _written.Append(Encoding.Latin1.GetString(data, offset, count));
        }

        public int Read(byte[] buffer, int offset, int count, int timeoutMs) => _rx.Take(buffer, offset, count, timeoutMs);

        public void DiscardInput() => _rx.Clear();

        public void Dispose()
        {
            IsOpen = false;
            Disposed = true;
        }
    }
}
