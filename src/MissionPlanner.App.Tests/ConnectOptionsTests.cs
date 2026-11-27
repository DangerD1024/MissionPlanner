using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.Session;
using MissionPlanner.Comms;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Tests;

/// <summary>
/// SETTINGS > Connection's serial options (Mission Planner's "Reset on USB Connect (toggle DTR)" and
/// "Disable RTS reset on ESP32 SerialUSB") reach the serial link before it opens. No real serial
/// device is ever opened: a recording fake, and a socat pseudo-terminal pair for the whole connect.
/// </summary>
[TestClass]
public class ConnectOptionsTests
{
    private static readonly string[] Keys = [ConnectOptions.ResetOnConnectKey, ConnectOptions.Esp32RtsKey];

    private Dictionary<string, string?> _saved = new();

    [TestInitialize]
    public void Save()
    {
        _saved = Keys.ToDictionary(k => k, k => Settings.Instance[k]);
        foreach (var k in Keys)
            Settings.Instance.Remove(k);
    }

    [TestCleanup]
    public void Restore()
    {
        foreach (var (k, v) in _saved)
        {
            if (v == null)
                Settings.Instance.Remove(k);
            else
                Settings.Instance[k] = v;
        }
    }

    [TestMethod]
    public void OffByDefaultASerialPortIsLeftAsItIs()
    {
        var port = new RecordingSerial();
        ConnectOptions.BeforeOpen(port);
        Assert.IsFalse(port.espFix);
        Assert.IsEmpty(port.Calls, "no line is touched");
    }

    [TestMethod]
    public void ResetPulsesDtrAndRtsAndEsp32FixIsSetBeforeOpen()
    {
        Settings.Instance[ConnectOptions.ResetOnConnectKey] = "True";
        Settings.Instance[ConnectOptions.Esp32RtsKey] = "True";
        var port = new RecordingSerial();
        ConnectOptions.BeforeOpen(port);
        Assert.IsTrue(port.espFix, "the ESP32 fix is the serial port's own (applied when it opens)");
        CollectionAssert.AreEqual(new[] { "DTR=False", "RTS=False", "toggleDTR" }, port.Calls,
            "Mission Planner's MainV2: both lines low, then toggleDTR; the port is not opened here");
    }

    [TestMethod]
    public void EachOptionWorksAlone()
    {
        Settings.Instance[ConnectOptions.Esp32RtsKey] = "True";
        var esp = new RecordingSerial();
        ConnectOptions.BeforeOpen(esp);
        Assert.IsTrue(esp.espFix);
        Assert.IsEmpty(esp.Calls);

        Settings.Instance[ConnectOptions.Esp32RtsKey] = "False";
        Settings.Instance[ConnectOptions.ResetOnConnectKey] = "True";
        var reset = new RecordingSerial();
        ConnectOptions.BeforeOpen(reset);
        Assert.IsFalse(reset.espFix);
        Assert.HasCount(3, reset.Calls);
    }

    [TestMethod]
    public void AFailedResetDoesNotStopTheConnect()
    {
        Settings.Instance[ConnectOptions.ResetOnConnectKey] = "True";
        var port = new RecordingSerial { FailToggle = true };
        ConnectOptions.BeforeOpen(port);
        Assert.Contains("toggleDTR", port.Calls);
    }

    [TestMethod]
    public void PhoneUsbAndBluetoothLinksAreLeftAlone()
    {
        Settings.Instance[ConnectOptions.ResetOnConnectKey] = "True";
        Settings.Instance[ConnectOptions.Esp32RtsKey] = "True";
        var link = new FakeChannel();
        ConnectOptions.BeforeOpen(link);
        Assert.IsTrue(link.RtsEnable, "RTS untouched (the reset would leave it low)");
        Assert.AreEqual(0, link.LineChanges);
    }

    [TestMethod]
    [Timeout(90000, CooperativeCancellation = true)]
    public async Task SerialConnectAppliesTheOptionsAndStillConnects()
    {
        if (OperatingSystem.IsWindows())
            Assert.Inconclusive("pseudo-terminal pair needs socat (Linux/macOS)");
        using var pty = ConnectTests.PtyPair.TryStart();
        if (pty == null)
            Assert.Inconclusive("socat is not installed");

        Settings.Instance[ConnectOptions.ResetOnConnectKey] = "True";
        Settings.Instance[ConnectOptions.Esp32RtsKey] = "True";

        using var vehiclePort = new System.IO.Ports.SerialPort(pty.VehiclePath, 115200) { ReadTimeout = 500, WriteTimeout = 500 };
        vehiclePort.Open();
        await using var vehicle = new FakeVehicle(vehiclePort.BaseStream, 5);

        await using var session = new VehicleSession();
        await session.ConnectAsync(LinkSpec.Serial(pty.GcsPath, 115200), getParams: false);
        try
        {
            Assert.IsTrue(session.IsConnected, "a pseudo-terminal has no modem lines: the reset fails quietly and the connect goes on");
            var port = (SerialPort)session.Mav.BaseStream;
            Assert.IsTrue(port.espFix, "the ESP32 fix reached the app's serial port");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (Math.Abs(session.Cs.roll - 5) > 0.01 && sw.Elapsed < TimeSpan.FromSeconds(10))
                await Task.Delay(20);
            Assert.AreEqual(5, session.Cs.roll, 0.01, "telemetry flows over the port after the reset");
        }
        finally
        {
            await session.DisconnectAsync();
        }
    }

    /// <summary>Mission Planner's serial port that records the line changes instead of making them; never opened.</summary>
    private sealed class RecordingSerial : SerialPort, ICommsSerial
    {
        private bool _dtr = true;
        private bool _rts = true;

        public List<string> Calls { get; } = new();

        public bool FailToggle { get; init; }

        bool ICommsSerial.DtrEnable
        {
            get => _dtr;
            set
            {
                _dtr = value;
                Calls.Add("DTR=" + value);
            }
        }

        bool ICommsSerial.RtsEnable
        {
            get => _rts;
            set
            {
                _rts = value;
                Calls.Add("RTS=" + value);
            }
        }

        void ICommsSerial.toggleDTR()
        {
            Calls.Add("toggleDTR");
            if (FailToggle)
                throw new IOException("no modem lines");
        }

        void ICommsSerial.Open() => throw new AssertFailedException("the options must not open the port");

        string ICommsSerial.PortName
        {
            get => "/dev/fake-serial";
            set { }
        }
    }

    /// <summary>A link like the phone's USB host or Bluetooth one.</summary>
    private sealed class FakeChannel() : ChannelLink("usb:fake", 115200)
    {
        public int LineChanges { get; private set; }

        protected override void OpenChannel()
        {
        }

        protected override void CloseChannel(bool wasOpen)
        {
        }

        protected override void WriteChannel(byte[] buffer, int offset, int count)
        {
        }

        protected override void OnLinesChanged(bool dtr, bool rts) => LineChanges++;
    }
}
