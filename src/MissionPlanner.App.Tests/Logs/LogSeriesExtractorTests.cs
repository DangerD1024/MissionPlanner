using MissionPlanner.App.Core.ViewModels.Logs;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Tests.Logs;

[TestClass]
public class LogSeriesExtractorTests
{
    [TestMethod]
    public void BinaryLogListsTypesFieldsAndInstances()
    {
        var path = LogTestFiles.TempPath(".bin");
        File.WriteAllBytes(path, LogTestFiles.BinaryLog());
        using var log = (LogSeriesExtractor)LogSource.Open(path);

        var names = log.MessageTypes.Select(t => t.Name).ToList();
        CollectionAssert.IsSubsetOf(new[] { "ATT", "GPS", "MODE", "ERR", "EV" }, names);
        CollectionAssert.DoesNotContain(names, "FMT");
        CollectionAssert.DoesNotContain(names, "FMTU");
        // MSG has text only
        CollectionAssert.DoesNotContain(names, "MSG");

        var att = log.FindType("ATT")!;
        CollectionAssert.AreEqual(new[] { "TimeUS", "Roll", "Pitch" }, att.Fields.ToArray());
        Assert.AreEqual(0, att.Instances.Count);

        var gps = log.FindType("GPS")!;
        CollectionAssert.AreEqual(new[] { "0", "1" }, gps.Instances.ToArray());
        CollectionAssert.Contains(gps.Fields.ToList(), "Lat");

        // the mode name column is text: only its number is graphable
        CollectionAssert.AreEqual(new[] { "TimeUS", "ModeNum", "Rsn" }, log.FindType("MODE")!.Fields.ToArray());
        Assert.AreEqual(MissionPlanner.ArduPilot.Firmwares.ArduCopter2, log.Firmware);
    }

    [TestMethod]
    public void BinaryLogSeriesHasValuesAgainstSeconds()
    {
        var path = LogTestFiles.TempPath(".bin");
        File.WriteAllBytes(path, LogTestFiles.BinaryLog());
        using var log = LogSource.Open(path);

        var roll = log.GetSeries("ATT", null, "Roll")!;
        Assert.AreEqual(21, roll.Count);
        Assert.AreEqual("ATT.Roll", roll.Name);
        Assert.AreEqual(1.0, roll.Times[0], 1e-9);
        Assert.AreEqual(3.0, roll.Times[^1], 1e-9);
        for (var i = 0; i < roll.Count; i++)
            Assert.AreEqual(1.5 * roll.Times[i], roll.Values[i], 1e-5);

        var pitch = log.GetSeries("ATT", null, "Pitch")!;
        Assert.AreEqual(-2.0, pitch.Values[10], 1e-5);

        Assert.IsNull(log.GetSeries("ATT", null, "Nope"));
        Assert.IsNull(log.GetSeries("NOPE", null, "Roll"));
        Assert.AreEqual(1.0, log.FirstTime, 1e-9);
    }

    [TestMethod]
    public void InstancesAreSeparateSeries()
    {
        var path = LogTestFiles.TempPath(".bin");
        File.WriteAllBytes(path, LogTestFiles.BinaryLog());
        using var log = LogSource.Open(path);

        var alt0 = log.GetSeries("GPS", "0", "Alt")!;
        var alt1 = log.GetSeries("GPS", "1", "Alt")!;
        Assert.AreEqual("GPS[0].Alt", alt0.Name);
        Assert.AreEqual(5, alt0.Count);
        Assert.AreEqual(5, alt1.Count);
        CollectionAssert.AreEqual(new[] { 100.0, 105, 110, 115, 120 }, alt0.Values);
        CollectionAssert.AreEqual(new[] { 1100.0, 1105, 1110, 1115, 1120 }, alt1.Values);

        // no instance: every message of the type
        Assert.AreEqual(10, log.GetSeries("GPS", null, "Alt")!.Count);

        var lat = log.GetSeries("GPS", "0", "Lat")!;
        Assert.AreEqual(-35.3632, lat.Values[0], 1e-7);
    }

    [TestMethod]
    public void EventsAndTrack()
    {
        var path = LogTestFiles.TempPath(".bin");
        File.WriteAllBytes(path, LogTestFiles.BinaryLog());
        using var log = LogSource.Open(path);

        var mode = log.Events.Single(e => e.Kind == LogEventKind.Mode);
        Assert.AreEqual(1.5, mode.Time, 1e-9);
        Assert.AreEqual("Loiter", mode.Text, ignoreCase: true);

        var err = log.Events.Single(e => e.Kind == LogEventKind.Error);
        Assert.AreEqual("ERR FAILSAFE_RADIO-1", err.Text);
        Assert.AreEqual(2.0, err.Time, 1e-9);

        Assert.AreEqual("ARMED", log.Events.Single(e => e.Kind == LogEventKind.Event).Text);
        var msg = log.Events.Single(e => e.Kind == LogEventKind.Message);
        StringAssert.StartsWith(msg.Text, "ArduCopter V4.5.0");
        Assert.AreEqual(0.5, msg.Time, 1e-9);

        // events in time order
        CollectionAssert.AreEqual(log.Events.OrderBy(e => e.Time).ToList(), log.Events.ToList());

        // the track follows the first GPS instance
        Assert.AreEqual(5, log.Track.Count);
        Assert.AreEqual(-35.3632, log.Track[0].Lat, 1e-7);
        Assert.AreEqual(149.1652, log.Track[0].Lng, 1e-7);
        Assert.AreEqual(100, log.Track[0].Alt, 1e-6);
        Assert.AreEqual(1.0, log.Track[0].Time, 1e-9);

        Assert.IsNotNull(log.StartTime);
        Assert.AreEqual(DFLog.gpsTimeToTime(LogTestFiles.GpsWeek, LogTestFiles.GpsMs / 1000.0), log.StartTime);
    }

    [TestMethod]
    public void TextLogReadsTheSameWay()
    {
        var path = LogTestFiles.TempPath(".log");
        File.WriteAllText(path, LogTestFiles.TextLog());
        using var log = LogSource.Open(path);

        Assert.AreEqual("DataFlash", log.KindName);
        CollectionAssert.AreEqual(new[] { "ATT", "MODE" }, log.MessageTypes.Select(t => t.Name).ToArray());

        var roll = log.GetSeries("ATT", null, "Roll")!;
        CollectionAssert.AreEqual(new[] { 1.0, 2.0, 3.0 }, roll.Times);
        CollectionAssert.AreEqual(new[] { 1.5, 3.0, 4.5 }, roll.Values);
        CollectionAssert.AreEqual(new[] { -2.25, -4.5, -6.75 }, log.GetSeries("ATT", null, "Pitch")!.Values);

        var mode = log.Events.Single(e => e.Kind == LogEventKind.Mode);
        Assert.AreEqual("Loiter", mode.Text);
        Assert.AreEqual(1.5, mode.Time, 1e-9);
        Assert.AreEqual("ArduPlane V4.5.0", log.Events.Single(e => e.Kind == LogEventKind.Message).Text);
    }

    [TestMethod]
    public void FastBinaryScanMatchesDFLogBuffer()
    {
        var path = LogTestFiles.TempPath(".bin");
        File.WriteAllBytes(path, LogTestFiles.BinaryLog());
        using var fast = LogSource.Open(path);
        // without a path the extractor reads every record through DFLogBuffer
        using var slow = new LogSeriesExtractor(new DFLogBuffer(path));

        foreach (var (type, instance) in new[] { ("ATT", (string?)null), ("GPS", "0"), ("GPS", "1"), ("GPS", null), ("MODE", null) })
        {
            var a = fast.GetTable(type, instance)!;
            var b = slow.GetTable(type, instance)!;
            CollectionAssert.AreEqual(b.Times, a.Times, $"{type}[{instance}] times");
            foreach (var (field, column) in b.Columns)
                CollectionAssert.AreEqual(column, a.Columns[field], $"{type}[{instance}].{field}");
        }
    }

    [TestMethod]
    public void TablesAreCachedAndReusedForOtherFields()
    {
        var path = LogTestFiles.TempPath(".bin");
        File.WriteAllBytes(path, LogTestFiles.BinaryLog());
        using var log = LogSource.Open(path);

        var a = log.GetTable("ATT", null);
        var b = log.GetTable("ATT", null);
        Assert.AreSame(a, b);
        Assert.AreSame(a!.Times, log.GetSeries("ATT", null, "Roll")!.Times);
    }

    [TestMethod]
    public void DisposedLogReturnsNothing()
    {
        var path = LogTestFiles.TempPath(".bin");
        File.WriteAllBytes(path, LogTestFiles.BinaryLog());
        var log = LogSource.Open(path);
        log.Dispose();
        Assert.IsNull(log.GetSeries("ATT", null, "Roll"));
        log.Dispose();
    }

    [TestMethod]
    public void DetectsLogKindByExtensionOrContent()
    {
        Assert.IsTrue(LogSource.IsTlog("a.tlog"));
        Assert.IsFalse(LogSource.IsTlog("a.BIN"));
        Assert.IsFalse(LogSource.IsTlog("a.log"));

        var bin = LogTestFiles.TempPath(".dat");
        File.WriteAllBytes(bin, LogTestFiles.BinaryLog());
        Assert.IsFalse(LogSource.IsTlog(bin));

        var tlog = LogTestFiles.TempPath(".dat");
        File.WriteAllBytes(tlog, LogTestFiles.Tlog(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)));
        Assert.IsTrue(LogSource.IsTlog(tlog));
    }
}
