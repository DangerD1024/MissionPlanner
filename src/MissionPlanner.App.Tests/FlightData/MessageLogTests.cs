using MissionPlanner.App.Core.ViewModels.FlightData;

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
    public void PrearmAndFailuresAreFlagged()
    {
        Assert.IsTrue(new MessageLine(T0, "PreArm: Compass not calibrated").IsWarning);
        Assert.IsTrue(new MessageLine(T0, "EKF3 IMU0 failed").IsWarning);
        Assert.IsFalse(new MessageLine(T0, "ArduCopter V4.6.0").IsWarning);
    }
}
