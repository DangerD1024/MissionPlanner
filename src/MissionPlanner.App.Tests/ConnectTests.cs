using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MissionPlanner.App.Core.Links;
using MissionPlanner.Comms;

namespace MissionPlanner.App.Tests;

/// <summary>
/// End-to-end connects through the unchanged MAVLinkInterface on .NET 10, against a fake vehicle:
/// TCP everywhere, and a real serial device (a socat pseudo-terminal pair) on Linux and macOS.
/// </summary>
[TestClass]
public class ConnectTests
{
    private const float Roll = 12.5f;

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task TcpConnectReadsTelemetryAndTalksBack()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var accept = listener.AcceptTcpClientAsync();
        var link = LinkFactory.Create(LinkSpec.Tcp("127.0.0.1", port));

        var connecting = ConnectAsync(link);
        using var vehicleSide = await accept;
        listener.Stop();
        await using var vehicle = new FakeVehicle(vehicleSide.GetStream(), Roll);

        using var mav = await connecting;
        await AssertConnectedAndTalkingAsync(mav, vehicle);
    }

    [TestMethod]
    [Timeout(90000, CooperativeCancellation = true)]
    public async Task SerialConnectReadsTelemetryAndTalksBack()
    {
        if (OperatingSystem.IsWindows())
            Assert.Inconclusive("pseudo-terminal pair needs socat (Linux/macOS)");

        using var pty = PtyPair.TryStart();
        if (pty == null)
            Assert.Inconclusive("socat is not installed");

        // vehicle end: plain System.IO.Ports, independent of Mission Planner's wrapper
        using var vehiclePort = new System.IO.Ports.SerialPort(pty.VehiclePath, 115200) { ReadTimeout = 500, WriteTimeout = 500 };
        vehiclePort.Open();
        await using var vehicle = new FakeVehicle(vehiclePort.BaseStream, Roll);

        // ground station end: Mission Planner's SerialPort, as the app will use it
        var link = LinkFactory.Create(LinkSpec.Serial(pty.GcsPath, 115200));
        Assert.IsInstanceOfType<SerialPort>(link);

        using var mav = await ConnectAsync(link);
        await AssertConnectedAndTalkingAsync(mav, vehicle);
    }

    private static Task<MAVLinkInterface> ConnectAsync(ICommsSerial link) => Task.Run(() =>
    {
        var reporter = new BackgroundProgressReporter();
        var mav = new MAVLinkInterface { BaseStream = link };
        using (BackgroundProgressReporter.Scope.Use(reporter))
            mav.Open(getparams: false, skipconnectedcheck: false, showui: true);

        if (reporter.Error != null || !link.IsOpen)
        {
            mav.Dispose();
            Assert.Fail("connect failed: " + (reporter.ErrorMessage ?? "link closed"));
        }

        return mav;
    });

    private static async Task AssertConnectedAndTalkingAsync(MAVLinkInterface mav, FakeVehicle vehicle)
    {
        Assert.AreEqual(FakeVehicle.SysId, mav.MAV.sysid);
        Assert.AreEqual(FakeVehicle.CompId, mav.MAV.compid);
        Assert.AreEqual(MAVLink.MAV_TYPE.QUADROTOR, mav.MAV.aptype);

        // pump packets the way the app's reader will, until telemetry lands in CurrentState
        var sw = Stopwatch.StartNew();
        while (Math.Abs(mav.MAV.cs.roll - Roll) > 0.01 && sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (mav.BaseStream.BytesToRead > 0)
                await mav.readPacketAsync();
            else
                await Task.Delay(10);
        }

        Assert.AreEqual(Roll, mav.MAV.cs.roll, 0.01, "ATTITUDE did not reach CurrentState");
        Assert.IsNull(vehicle.SendError, "vehicle side failed to write: " + vehicle.SendError);

        // the library announces itself after connecting; the vehicle must have received it
        sw.Restart();
        while (!vehicle.Received.Any(m => m.msgid == (uint)MAVLink.MAVLINK_MSG_ID.STATUSTEXT) && sw.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(20);

        Assert.IsTrue(vehicle.Received.Any(m => m.msgid == (uint)MAVLink.MAVLINK_MSG_ID.STATUSTEXT),
            $"vehicle received no STATUSTEXT from the GCS ({vehicle.Received.Count} messages)");

        mav.Close();
        Assert.IsFalse(mav.BaseStream.IsOpen);
    }

    /// <summary>Two linked pseudo-terminals from socat, exposed as stable symlinks.</summary>
    internal sealed class PtyPair : IDisposable
    {
        private readonly Process _socat;
        private readonly string _dir;

        private PtyPair(Process socat, string dir)
        {
            _socat = socat;
            _dir = dir;
        }

        public string VehiclePath => Path.Combine(_dir, "vehicle");

        public string GcsPath => Path.Combine(_dir, "gcs");

        public static PtyPair? TryStart()
        {
            var dir = Path.Combine(TestEnvironment.DataRoot, "pty-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);

            Process? socat;
            try
            {
                socat = Process.Start(new ProcessStartInfo("socat")
                {
                    ArgumentList =
                    {
                        $"pty,raw,echo=0,link={Path.Combine(dir, "vehicle")}",
                        $"pty,raw,echo=0,link={Path.Combine(dir, "gcs")}",
                    },
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return null;
            }

            if (socat == null)
                return null;

            var pair = new PtyPair(socat, dir);
            var sw = Stopwatch.StartNew();
            while (!(File.Exists(pair.VehiclePath) && File.Exists(pair.GcsPath)))
            {
                if (socat.HasExited || sw.Elapsed > TimeSpan.FromSeconds(5))
                {
                    pair.Dispose();
                    Assert.Fail("socat did not create the pty pair: " + (socat.HasExited ? socat.StandardError.ReadToEnd() : "timeout"));
                }

                Thread.Sleep(20);
            }

            return pair;
        }

        public void Dispose()
        {
            try
            {
                if (!_socat.HasExited)
                    _socat.Kill();
                _socat.WaitForExit(2000);
            }
            catch (InvalidOperationException)
            {
            }

            _socat.Dispose();
        }
    }
}
