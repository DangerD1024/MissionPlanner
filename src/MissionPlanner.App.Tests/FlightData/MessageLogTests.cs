using MissionPlanner.App.Core.Session;
using MissionPlanner.App.Core.ViewModels.FlightData;
using MessageLevel = MissionPlanner.App.Core.ViewModels.FlightData.MessageLevel;

namespace MissionPlanner.App.Tests.FlightData;

[TestClass]
public class MessageLogTests
{
    private static readonly DateTime T0 = new(2026, 11, 18, 10, 0, 0);

    [TestMethod]
    public void NewestFirstWithTimes()
    {
        var src = new List<(DateTime, string)> { (T0, "first"), (T0.AddSeconds(1), "second") };
        var log = new MessageLog();
        Assert.IsTrue(log.Update(src));
        CollectionAssert.AreEqual(new[] { "second", "first" }, log.Lines.Select(l => l.Text).ToArray());
        Assert.AreEqual("10:00:01", log.Lines[0].TimeText);
    }

    [TestMethod]
    public void OnlyNewMessagesAreInserted()
    {
        var src = new List<(DateTime, string)> { (T0, "a"), (T0, "b") };
        var log = new MessageLog();
        log.Update(src);
        var firstLine = log.Lines[^1];

        var changes = 0;
        log.Lines.CollectionChanged += (_, _) => changes++;
        Assert.IsFalse(log.Update(src), "nothing new");
        Assert.AreEqual(0, changes);

        // same timestamp as the last one: still noticed
        src.Add((T0, "c"));
        src.Add((T0.AddSeconds(2), "d"));
        Assert.IsTrue(log.Update(src));
        Assert.AreEqual(2, changes, "two inserts, no rebuild");
        CollectionAssert.AreEqual(new[] { "d", "c", "b", "a" }, log.Lines.Select(l => l.Text).ToArray());
        Assert.AreSame(firstLine, log.Lines[^1], "existing lines are kept");
    }

    [TestMethod]
    public void RepeatedTextInTheSameSecondIsNotLost()
    {
        var src = new List<(DateTime, string)> { (T0, "PreArm: RC not calibrated") };
        var log = new MessageLog();
        log.Update(src);
        src.Add((T0, "PreArm: RC not calibrated"));
        Assert.IsTrue(log.Update(src));
        Assert.HasCount(2, log.Lines);
    }

    [TestMethod]
    public void CappedSourceStillAppends()
    {
        // the library drops the oldest once it holds 1000
        var src = Enumerable.Range(0, 5).Select(i => (T0.AddSeconds(i), "m" + i)).ToList();
        var log = new MessageLog();
        log.Update(src);
        src.RemoveAt(0);
        src.Add((T0.AddSeconds(5), "m5"));
        Assert.IsTrue(log.Update(src));
        Assert.AreEqual("m5", log.Lines[0].Text);
        Assert.HasCount(6, log.Lines, "lines already shown stay");
    }

    [TestMethod]
    public void ClearedSourceClearsAndRefills()
    {
        var src = new List<(DateTime, string)> { (T0, "old1"), (T0.AddSeconds(1), "old2") };
        var log = new MessageLog();
        log.Update(src);

        src.Clear();
        Assert.IsTrue(log.Update(src));
        Assert.IsEmpty(log.Lines);

        src.Add((T0.AddSeconds(10), "new"));
        log.Update(src);
        Assert.AreEqual("new", log.Lines.Single().Text);
    }

    [TestMethod]
    public void ReplacedHistoryIsRebuilt()
    {
        var log = new MessageLog();
        log.Update(new List<(DateTime, string)> { (T0, "x"), (T0.AddSeconds(1), "y") });

        // another vehicle's list
        var other = new List<(DateTime, string)> { (T0.AddSeconds(3), "p"), (T0.AddSeconds(4), "q") };
        Assert.IsTrue(log.Update(other));
        CollectionAssert.AreEqual(new[] { "q", "p" }, log.Lines.Select(l => l.Text).ToArray());
    }

    [TestMethod]
    public void LinesAreCapped()
    {
        var log = new MessageLog();
        var src = Enumerable.Range(0, MessageLog.MaxLines + 50).Select(i => (T0.AddMilliseconds(i), "m" + i)).ToList();
        log.Apply(src);
        Assert.HasCount(MessageLog.MaxLines, log.Lines);
        Assert.AreEqual("m" + (MessageLog.MaxLines + 49), log.Lines[0].Text);
    }

    [TestMethod]
    public void WithoutASeverityPrearmIsCriticalAndFailuresAreWarnings()
    {
        Assert.IsTrue(new MessageLine(T0, "PreArm: Compass not calibrated").IsCritical);
        Assert.AreEqual("CRIT", new MessageLine(T0, "Arm: Gyros inconsistent").LevelText);
        Assert.IsTrue(new MessageLine(T0, "EKF3 IMU0 failed").IsWarning);
        var plain = new MessageLine(T0, "ArduCopter V4.6.0");
        Assert.AreEqual(MessageLevel.Info, plain.Level);
        Assert.AreEqual("INFO", plain.LevelText);
    }

    [TestMethod]
    public void TheStatustextSeverityDecides()
    {
        Assert.AreEqual(MessageLevel.Critical, new MessageLine(T0, "x", MAVLink.MAV_SEVERITY.EMERGENCY).Level);
        Assert.AreEqual(MessageLevel.Critical, new MessageLine(T0, "x", MAVLink.MAV_SEVERITY.ERROR).Level);
        Assert.AreEqual("ERROR", new MessageLine(T0, "x", MAVLink.MAV_SEVERITY.ERROR).LevelText);
        var warn = new MessageLine(T0, "Mode change to GUIDED failed: requires position", MAVLink.MAV_SEVERITY.WARNING);
        Assert.IsTrue(warn.IsWarning);
        Assert.AreEqual("WARN", warn.LevelText);
        // the vehicle said INFO: the word "failed" does not make it a warning
        var info = new MessageLine(T0, "Lua: script failed to load (none)", MAVLink.MAV_SEVERITY.INFO);
        Assert.AreEqual(MessageLevel.Info, info.Level);
        Assert.AreEqual("NOTICE", new MessageLine(T0, "x", MAVLink.MAV_SEVERITY.NOTICE).LevelText);
    }

    [TestMethod]
    public void LinesTakeTheSeverityRecordedForTheirText()
    {
        var severities = new StatusTextSeverities();
        severities.Record("PreArm: Need Position Estimate", MAVLink.MAV_SEVERITY.CRITICAL);
        severities.Record("Disarming motors", MAVLink.MAV_SEVERITY.INFO);
        var log = new MessageLog { SeverityOf = severities.Find };
        log.Apply([(T0, "PreArm: Need Position Estimate"), (T0.AddSeconds(1), "Disarming motors"), (T0.AddSeconds(2), "unknown text")]);
        Assert.AreEqual(MAVLink.MAV_SEVERITY.CRITICAL, log.Lines[2].Severity);
        Assert.AreEqual(MAVLink.MAV_SEVERITY.INFO, log.Lines[1].Severity);
        Assert.IsNull(log.Lines[0].Severity);
    }

    [TestMethod]
    public void SeverityOfAnotherComponentsLineIsFound()
    {
        var severities = new StatusTextSeverities();
        severities.Record("Gimbal: not healthy", MAVLink.MAV_SEVERITY.WARNING);
        Assert.AreEqual(MAVLink.MAV_SEVERITY.WARNING, severities.Find("154 : Gimbal: not healthy"));
        Assert.IsNull(severities.Find("abc : Gimbal: not healthy"));
    }

    [TestMethod]
    public void SeverityIsReadFromAStatustextPacket()
    {
        var text = new byte[50];
        System.Text.Encoding.UTF8.GetBytes("EKF3 IMU0 is using GPS").CopyTo(text, 0);
        var st = new MAVLink.mavlink_statustext_t { severity = (byte)MAVLink.MAV_SEVERITY.INFO, text = text };
        var packet = new MAVLink.MavlinkParse().GenerateMAVLinkPacket20(MAVLink.MAVLINK_MSG_ID.STATUSTEXT, st, false, 1, 1);
        var message = new MAVLink.MAVLinkMessage(packet);

        var severities = new StatusTextSeverities();
        severities.Record(message);
        Assert.AreEqual(MAVLink.MAV_SEVERITY.INFO, severities.Find("EKF3 IMU0 is using GPS"));
    }
}
