using MissionPlanner.App.Core.ViewModels.Logs;

namespace MissionPlanner.App.Tests.Logs;

[TestClass]
public class TlogReaderTests
{
    private static readonly DateTime Start = new(2026, 3, 14, 15, 9, 26, DateTimeKind.Utc);

    private static LogSource OpenTlog(int samples = 10)
    {
        var path = LogTestFiles.TempPath(".tlog");
        File.WriteAllBytes(path, LogTestFiles.Tlog(Start, samples));
        return LogSource.Open(path);
    }

    [TestMethod]
    public void ListsMessageTypesAndNumericFields()
    {
        using var log = OpenTlog();
        Assert.IsInstanceOfType<TlogReader>(log);
        Assert.AreEqual("Telemetry", log.KindName);

        var names = log.MessageTypes.Select(t => t.Name).ToList();
        CollectionAssert.IsSubsetOf(new[] { "ATTITUDE", "GLOBAL_POSITION_INT", "HEARTBEAT", "STATUSTEXT" }, names);
        CollectionAssert.AreEqual(names.OrderBy(n => n, StringComparer.Ordinal).ToList(), names);

        var att = log.FindType("ATTITUDE")!;
        CollectionAssert.IsSubsetOf(new[] { "time_boot_ms", "roll", "pitch", "yaw", "rollspeed" }, att.Fields.ToList());
        Assert.AreEqual(0, att.Instances.Count);
        Assert.AreEqual(10, att.Count);

        // two senders of HEARTBEAT: the vehicle and the GCS
        CollectionAssert.AreEqual(new[] { "1:1", "255:190" }, log.FindType("HEARTBEAT")!.Instances.ToArray());

        // the status text is not a number; the severity is
        var st = log.FindType("STATUSTEXT")!;
        CollectionAssert.Contains(st.Fields.ToList(), "severity");
        Assert.IsFalse(st.Fields.Any(f => f.StartsWith("text", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void SeriesValuesAndTimestamps()
    {
        using var log = OpenTlog();

        var roll = log.GetSeries("ATTITUDE", null, "roll")!;
        Assert.AreEqual(10, roll.Count);
        for (var i = 0; i < roll.Count; i++)
        {
            Assert.AreEqual(0.2 * (i + 1), roll.Times[i], 1e-3, "time of sample " + i);
            Assert.AreEqual(0.1 * i, roll.Values[i], 1e-6, "roll of sample " + i);
        }

        var boot = log.GetSeries("ATTITUDE", null, "time_boot_ms")!;
        Assert.AreEqual(200, boot.Values[0], 1e-9);

        var alt = log.GetSeries("GLOBAL_POSITION_INT", null, "alt")!;
        Assert.AreEqual(584_000 + 9 * 1000, alt.Values[^1], 1e-9);

        var gcs = log.GetSeries("HEARTBEAT", "255:190", "type")!;
        Assert.AreEqual(1, gcs.Count);
        Assert.AreEqual((double)MAVLink.MAV_TYPE.GCS, gcs.Values[0]);

        var vehicle = log.GetSeries("HEARTBEAT", "1:1", "custom_mode")!;
        CollectionAssert.AreEqual(new[] { 0.0, 5.0 }, vehicle.Values);

        Assert.AreEqual(Start.ToLocalTime(), log.StartTime);
        Assert.AreEqual(2.0, log.LastTime, 1e-3);
    }

    [TestMethod]
    public void ModesStatusTextAndTrack()
    {
        using var log = OpenTlog();

        var modes = log.Events.Where(e => e.Kind == LogEventKind.Mode).ToList();
        Assert.AreEqual(2, modes.Count);
        Assert.AreEqual(0, modes[0].Time, 1e-3);
        Assert.AreEqual("Stabilize", modes[0].Text, ignoreCase: true);
        Assert.AreEqual(1.0, modes[1].Time, 1e-3);
        Assert.AreEqual("Loiter", modes[1].Text, ignoreCase: true);

        var error = log.Events.Single(e => e.Kind == LogEventKind.Error);
        Assert.AreEqual("PreArm: test failure", error.Text);
        Assert.AreEqual(1.2, error.Time, 1e-3);
        Assert.AreEqual("ArduCopter V4.5.0", log.Events.Single(e => e.Kind == LogEventKind.Message).Text);

        Assert.AreEqual(10, log.Track.Count);
        Assert.AreEqual(-35.3632, log.Track[0].Lat, 1e-6);
        Assert.AreEqual(149.1652 + 9e-4, log.Track[^1].Lng, 1e-6);
        Assert.AreEqual(584.0, log.Track[0].Alt, 1e-6);
    }

    [TestMethod]
    public void ReadsALargerLogQuickly()
    {
        using var log = OpenTlog(samples: 5000);
        var roll = log.GetSeries("ATTITUDE", null, "roll")!;
        Assert.AreEqual(5000, roll.Count);
        Assert.AreEqual(0.1 * 4999, roll.Values[^1], 1e-2);
    }

    [TestMethod]
    public void CancelStopsReading()
    {
        var path = LogTestFiles.TempPath(".tlog");
        File.WriteAllBytes(path, LogTestFiles.Tlog(Start, 2000));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => TlogReader.Open(path, null, cancel.Token));
    }

    [TestMethod]
    public void FirmwareFromHeartbeat()
    {
        Assert.AreEqual(MissionPlanner.ArduPilot.Firmwares.ArduPlane,
            TlogReader.FirmwareOf((byte)MAVLink.MAV_TYPE.VTOL_QUADROTOR, (byte)MAVLink.MAV_AUTOPILOT.ARDUPILOTMEGA));
        Assert.AreEqual(MissionPlanner.ArduPilot.Firmwares.ArduRover,
            TlogReader.FirmwareOf((byte)MAVLink.MAV_TYPE.SURFACE_BOAT, (byte)MAVLink.MAV_AUTOPILOT.ARDUPILOTMEGA));
        Assert.IsNull(TlogReader.FirmwareOf((byte)MAVLink.MAV_TYPE.QUADROTOR, (byte)MAVLink.MAV_AUTOPILOT.GENERIC));
    }
}
