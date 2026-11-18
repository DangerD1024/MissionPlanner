using System.Diagnostics;
using MissionPlanner.App.Core.Links;

namespace MissionPlanner.App.Tests;

/// <summary>
/// Read-only checks against a real flight controller. Opt in with MP_TEST_SERIAL_PORT
/// (e.g. /dev/serial/by-id/usb-ArduPilot_...-if00 or COM5) and optionally MP_TEST_BAUD.
/// Nothing is written to the vehicle and it is never armed.
/// </summary>
[TestClass]
public class HardwareTests
{
    public TestContext TestContext { get; set; } = null!;

    private static LinkSpec? Port()
    {
        var port = Environment.GetEnvironmentVariable("MP_TEST_SERIAL_PORT");
        if (string.IsNullOrWhiteSpace(port))
            return null;
        var baud = int.TryParse(Environment.GetEnvironmentVariable("MP_TEST_BAUD"), out var b) ? b : 115200;
        return LinkSpec.Serial(port, baud);
    }

    [TestMethod]
    [TestCategory("Hardware")]
    [Timeout(180000, CooperativeCancellation = true)]
    public async Task UsbFlightControllerConnectsStreamsAndSendsParams()
    {
        var spec = Port();
        if (spec == null)
            Assert.Inconclusive("set MP_TEST_SERIAL_PORT to run against a real flight controller");

        var progress = new List<string>();
        var sw = Stopwatch.StartNew();
        using var mav = await Task.Run(() =>
        {
            var reporter = new BackgroundProgressReporter(new Progress<OperationProgress>(p => progress.Add(p.Status)));
            var m = new MAVLinkInterface { BaseStream = LinkFactory.Create(spec) };
            using (BackgroundProgressReporter.Scope.Use(reporter))
                m.Open(getparams: true, skipconnectedcheck: false, showui: true);
            if (reporter.Error != null || !m.BaseStream.IsOpen)
                Assert.Fail("connect failed: " + (reporter.ErrorMessage ?? "link closed"));
            return m;
        });
        var connectTime = sw.Elapsed;

        TestContext.WriteLine($"connected in {connectTime.TotalSeconds:F1}s: sysid {mav.MAV.sysid} compid {mav.MAV.compid} " +
                              $"type {mav.MAV.aptype} autopilot {mav.MAV.apname} firmware {mav.MAV.cs.firmware} version {mav.MAV.cs.version}");
        TestContext.WriteLine($"params: {mav.MAV.param.Count} (total reported {mav.MAV.param.TotalReported})");

        Assert.IsGreaterThan(100, mav.MAV.param.Count, "parameter download incomplete");

        // stream telemetry for a few seconds the way the app's reader does
        var packets = 0;
        mav.OnPacketReceived += (_, _) => Interlocked.Increment(ref packets);
        sw.Restart();
        while (sw.Elapsed < TimeSpan.FromSeconds(4))
        {
            if (mav.BaseStream.BytesToRead > 0)
                await mav.readPacketAsync();
            else
                await Task.Delay(5);
            if (sw.ElapsedMilliseconds % 500 < 6)
                mav.MAV.cs.UpdateCurrentSettings(null, false, mav, mav.MAV);
        }

        var cs = mav.MAV.cs;
        TestContext.WriteLine($"telemetry: roll {cs.roll:F1} pitch {cs.pitch:F1} yaw {cs.yaw:F1} mode {cs.mode} armed {cs.armed} " +
                              $"batt {cs.battery_voltage:F2}V gps {cs.gpsstatus} sats {cs.satcount} packets in 4s {packets}");
        Assert.IsGreaterThan(20, packets, "telemetry is not streaming after connect");

        mav.Close();
    }
}
