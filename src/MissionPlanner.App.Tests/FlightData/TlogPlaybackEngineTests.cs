using MissionPlanner.App.Core.ViewModels.FlightData;

namespace MissionPlanner.App.Tests.FlightData;

[TestClass]
public class TlogPlaybackEngineTests
{
    private const int Count = 20; // 2 s of ATTITUDE at 10 Hz

    private static (MAVLinkInterface Mav, TlogPlaybackEngine Engine, FakeTlogClock Clock, string Path) Setup(int count = Count)
    {
        var path = TlogWriter.CreateFlight(TlogWriter.TempPath("flight"), count);
        var mav = new MAVLinkInterface();
        var clock = new FakeTlogClock();
        return (mav, new TlogPlaybackEngine(mav, clock), clock, path);
    }

    [TestMethod]
    public void LoadTakesTheVehicleFromTheFirstNonGcsHeartbeat()
    {
        var (mav, engine, _, path) = Setup();
        using var _ = engine;

        engine.Load(path);

        Assert.AreEqual(TlogPlaybackState.Paused, engine.State);
        Assert.AreEqual((uint)TlogWriter.VehicleSysId, mav.sysidcurrent, "the recording GCS's heartbeat comes first and must be skipped");
        Assert.AreEqual(TlogWriter.VehicleCompId, mav.compidcurrent);
        Assert.AreEqual(MAVLink.MAV_TYPE.QUADROTOR, mav.MAV.aptype);
        Assert.IsFalse(mav.logreadmode, "loaded paused: nothing reads until Play");
        Assert.IsNotNull(mav.logplaybackfile);

        var info = engine.Info!;
        Assert.AreEqual(TlogWriter.T0.ToLocalTime(), info.Start);
        Assert.AreEqual(TlogWriter.T0.AddMilliseconds(100 * Count).ToLocalTime(), info.End);
        Assert.AreEqual(TimeSpan.FromMilliseconds(100 * Count), info.Duration);
        Assert.AreEqual(TlogWriter.T0.ToLocalTime(), engine.CurrentTime);
        Assert.IsTrue(engine.Position is > 0 and < 0.2);
    }

    [TestMethod]
    public async Task PlaysEveryPacketIntoCurrentStateAtTheLogsPace()
    {
        var (mav, engine, clock, path) = Setup();
        using var _ = engine;
        engine.Load(path);

        engine.Play();
        Assert.AreEqual(TlogPlaybackState.Playing, engine.State);
        await engine.WaitForLoopAsync();

        Assert.AreEqual(TlogPlaybackState.Ended, engine.State);
        Assert.IsFalse(mav.logreadmode, "at the end of the file the interface leaves log-read mode");
        Assert.AreEqual(1.0, engine.Position, 1e-9);
        Assert.AreEqual(Count, mav.MAV.cs.roll, 0.01, "the last ATTITUDE is on CurrentState");
        Assert.AreEqual(TlogWriter.T0.AddMilliseconds(100 * Count).ToLocalTime(), engine.CurrentTime);
        Assert.AreEqual(engine.CurrentTime, mav.MAV.cs.datetime, "CurrentState's clock follows the log");

        // each attitude waits its 100 ms gap; the heartbeats share their attitude's timestamp
        Assert.AreEqual(100.0 * Count, clock.TotalSleptMs, 1.0);
        Assert.IsTrue(clock.Requested.All(ms => Math.Abs(ms - 100) < 1e-6), "only the 100 ms gaps are waited for");
    }

    [TestMethod]
    [DataRow(2.0)]
    [DataRow(0.25)]
    [DataRow(10.0)]
    public async Task SpeedDividesTheWaits(double speed)
    {
        var (_, engine, clock, path) = Setup();
        using var _ = engine;
        engine.Load(path);
        engine.Speed = speed;

        engine.Play();
        await engine.WaitForLoopAsync();

        Assert.AreEqual(100.0 * Count / speed, clock.TotalSleptMs, 1.0);
    }

    [TestMethod]
    public async Task OversleepingIsCorrectedSoThePaceHolds()
    {
        var (_, engine, clock, path) = Setup(50);
        using var _ = engine;
        clock.OversleepMs = 7; // e.g. a coarse timer
        engine.Load(path);

        engine.Play();
        await engine.WaitForLoopAsync();

        // without correction this would be 50 * 107 ms
        Assert.AreEqual(100.0 * 50, clock.TotalSleptMs, 8.0);
    }

    [TestMethod]
    public async Task PauseStopsReadingAndPlayResumesWhereItWas()
    {
        var (mav, engine, clock, path) = Setup();
        using var _ = engine;
        engine.Load(path);
        clock.BlockAfter = 5;

        engine.Play();
        await TlogTestExtensions.WaitUntilAsync(() => clock.Delays > 5);
        engine.Pause();

        Assert.AreEqual(TlogPlaybackState.Paused, engine.State);
        Assert.IsTrue(engine.LoopTask.IsCompleted, "Pause returns once the loop has stopped");
        Assert.IsFalse(mav.logreadmode, "paused as in Mission Planner: log-read mode off");
        var packets = engine.PacketsRead;
        var roll = mav.MAV.cs.roll;
        var position = engine.Position;
        Assert.AreEqual(6, roll, 0.01, "five 100 ms waits after the first attitude: the sixth is shown");
        Assert.IsTrue(position is > 0.1 and < 0.9);

        await Task.Delay(100);
        Assert.AreEqual(packets, engine.PacketsRead, "nothing is read while paused");

        clock.BlockAfter = int.MaxValue;
        engine.Play();
        await engine.WaitForLoopAsync();

        Assert.AreEqual(TlogPlaybackState.Ended, engine.State);
        Assert.AreEqual(Count, mav.MAV.cs.roll, 0.01);
        // GCS heartbeat + vehicle heartbeats + attitudes, each read exactly once (two were read on load)
        Assert.AreEqual(Count + Count / 10, engine.PacketsRead);
    }

    [TestMethod]
    public async Task SeekJumpsToARecordInTheMiddleAndResetsState()
    {
        var (mav, engine, _, path) = Setup(100);
        using var _ = engine;
        engine.Load(path);

        var attitudes = new List<float>();
        mav.OnPacketReceived += (_, m) =>
        {
            if (m.msgid == (uint)MAVLink.MAVLINK_MSG_ID.ATTITUDE)
                attitudes.Add(m.ToStructure<MAVLink.mavlink_attitude_t>().roll * (float)(180 / Math.PI));
        };

        engine.Seek(0.5);

        Assert.AreEqual(0.5, engine.Position, 0.02);
        Assert.AreEqual("Unknown", mav.MAV.cs.mode, "the shown vehicle's state is reset, as Mission Planner does");
        var mid = TlogWriter.T0.AddSeconds(5).ToLocalTime();
        Assert.IsTrue(Math.Abs((engine.CurrentTime - mid).TotalSeconds) < 0.5, "time at the record the seek landed on: " + engine.CurrentTime);

        engine.Play();
        await engine.WaitForLoopAsync();

        Assert.IsTrue(attitudes.Count is > 40 and < 60, $"about half the attitudes are played, got {attitudes.Count}");
        Assert.AreEqual(100 - attitudes.Count + 1, attitudes[0], 0.01, "playback resumes at a record boundary, in order");
        CollectionAssert.AreEqual(Enumerable.Range(101 - attitudes.Count, attitudes.Count).Select(i => (float)i).ToList(),
            attitudes.Select(r => (float)Math.Round(r)).ToList());
    }

    [TestMethod]
    public async Task SeekWhilePlayingContinuesFromTheNewPosition()
    {
        var (mav, engine, clock, path) = Setup(100);
        using var _ = engine;
        engine.Load(path);
        clock.BlockAfter = 3;
        engine.Play();
        await TlogTestExtensions.WaitUntilAsync(() => clock.Delays > 3);

        engine.Seek(0.9);
        Assert.AreEqual(TlogPlaybackState.Playing, engine.State);
        clock.BlockAfter = int.MaxValue;
        engine.Pause(); // releases the blocked delay
        engine.Play();
        await engine.WaitForLoopAsync();

        Assert.AreEqual(TlogPlaybackState.Ended, engine.State);
        Assert.AreEqual(100, mav.MAV.cs.roll, 0.01);
        Assert.IsTrue(engine.PacketsRead < 30, "the middle of the file was skipped");
    }

    [TestMethod]
    public async Task PlayAfterTheEndStartsAgain()
    {
        var (mav, engine, _, path) = Setup();
        using var _ = engine;
        engine.Load(path);
        engine.Play();
        await engine.WaitForLoopAsync();
        var first = engine.PacketsRead;

        engine.Play();
        await engine.WaitForLoopAsync();

        Assert.AreEqual(TlogPlaybackState.Ended, engine.State);
        Assert.IsTrue(engine.PacketsRead >= 2 * first, "the whole file was played again");
        Assert.AreEqual(Count, mav.MAV.cs.roll, 0.01);
    }

    [TestMethod]
    public async Task SeekBackAfterTheEndAllowsPlayingOn()
    {
        var (_, engine, _, path) = Setup();
        using var _ = engine;
        engine.Load(path);
        engine.Play();
        await engine.WaitForLoopAsync();

        engine.Seek(0.5);
        Assert.AreEqual(TlogPlaybackState.Paused, engine.State);
        Assert.AreEqual(0.5, engine.Position, 0.05);
    }

    [TestMethod]
    public async Task StopClosesTheLogAndClearsItsVehicles()
    {
        var (mav, engine, clock, path) = Setup();
        using var _ = engine;
        engine.Load(path);
        clock.BlockAfter = 2;
        engine.Play();
        await TlogTestExtensions.WaitUntilAsync(() => clock.Delays > 2);

        engine.Stop();

        Assert.AreEqual(TlogPlaybackState.Unloaded, engine.State);
        Assert.IsTrue(engine.LoopTask.IsCompleted);
        Assert.IsFalse(mav.logreadmode);
        Assert.IsNull(mav.logplaybackfile);
        Assert.AreEqual(0, mav.MAVlist.Count, "the log's vehicles are gone");
        Assert.IsNull(engine.Info);
        Assert.AreEqual(0, engine.Position);

        // the file is closed: it can be deleted
        File.Delete(path);
    }

    [TestMethod]
    public void AFileWithoutMavlinkIsRefusedAndNothingChanges()
    {
        var path = TlogWriter.TempPath("junk");
        File.WriteAllBytes(path, Enumerable.Range(0, 5000).Select(i => (byte)(i * 7 % 251)).ToArray());
        var mav = new MAVLinkInterface();
        using var engine = new TlogPlaybackEngine(mav, new FakeTlogClock());

        Assert.ThrowsExactly<InvalidDataException>(() => engine.Load(path));
        Assert.AreEqual(TlogPlaybackState.Unloaded, engine.State);
        Assert.IsNull(mav.logplaybackfile);
        Assert.IsFalse(mav.logreadmode);
    }

    [TestMethod]
    public void LoadingAnotherLogReplacesTheFirst()
    {
        var (mav, engine, _, path) = Setup();
        using var _ = engine;
        engine.Load(path);
        var first = mav.logplaybackfile!;

        var second = TlogWriter.CreateFlight(TlogWriter.TempPath("second"), 5);
        engine.Load(second);

        Assert.AreNotSame(first, mav.logplaybackfile);
        Assert.AreEqual(second, engine.Info!.Path);
        Assert.ThrowsExactly<ObjectDisposedException>(() => first.BaseStream.ReadByte(), "the first file was closed");
    }

    [TestMethod]
    public void PacerWaitsTheGapDividedBySpeed()
    {
        var pacer = new TlogPacer(0);
        Assert.AreEqual(50, pacer.Next(100, 2, 0), 1e-9);
        // the 50 ms took exactly 50 ms: no correction
        Assert.AreEqual(25, pacer.Next(50, 2, 50), 1e-9);
    }

    [TestMethod]
    public void PacerIgnoresJumpsAndCapsLongGaps()
    {
        var pacer = new TlogPacer(0);
        Assert.AreEqual(0, pacer.Next(-5, 1, 0), "time went backwards (seek, clock change)");
        Assert.AreEqual(0, pacer.Next(10_000, 1, 0), "more than 9999 ms: not waited for");
        Assert.AreEqual(TlogPacer.MaxSleepMs, pacer.Next(5000, 1, 0), "long gaps wait at most a second");
    }

    [TestMethod]
    public void PacerCarriesTimeLostToSlowReadsIntoTheNextWait()
    {
        var pacer = new TlogPacer(0);
        Assert.AreEqual(100, pacer.Next(100, 1, 0), 1e-9);
        // the 100 ms wait plus reading took 130 ms: the next wait is 30 ms shorter
        Assert.AreEqual(70, pacer.Next(100, 1, 130), 1e-9);
        // packets with the same timestamp keep the debt until there is a wait to take it from
        Assert.AreEqual(0, pacer.Next(0, 1, 200), 1e-9);
    }
}

[TestClass]
public class TlogScannerTests
{
    [TestMethod]
    public void InspectReadsFirstAndLastTimestamps()
    {
        var path = TlogWriter.CreateFlight(TlogWriter.TempPath("inspect"), 30);

        var info = TlogScanner.Inspect(path);

        Assert.AreEqual(TlogWriter.T0.ToLocalTime(), info.Start);
        Assert.AreEqual(TlogWriter.T0.AddSeconds(3).ToLocalTime(), info.End);
        Assert.AreEqual(new FileInfo(path).Length, info.Length);
    }

    [TestMethod]
    public void InspectFindsTheEndOfALargeLogPastATruncatedRecord()
    {
        var path = TlogWriter.TempPath("large");
        using (var w = new TlogWriter(path))
        {
            w.Heartbeat(TlogWriter.T0);
            // ~1 MB: the end is found from the tail only
            for (var k = 1; k <= 20000; k++)
                w.Attitude(TlogWriter.T0.AddMilliseconds(10 * k), k % 90);
            // the app died while writing the last record
            w.WriteRaw(new byte[] { 0x00, 0x06, 0x1d, 0x5c, 0x12, 0x34, 0x56, 0x78, 0xfd, 0x1c, 0x00 });
        }

        var info = TlogScanner.Inspect(path);

        Assert.AreEqual(TlogWriter.T0.AddSeconds(200).ToLocalTime(), info.End);
    }

    [TestMethod]
    public void AlignFindsTheNextRecordStart()
    {
        var path = TlogWriter.TempPath("align");
        List<(long Offset, DateTime Time)> records;
        using (var w = new TlogWriter(path))
        {
            w.Heartbeat(TlogWriter.T0);
            for (var k = 1; k <= 200; k++)
                w.Attitude(TlogWriter.T0.AddMilliseconds(100 * k), k);
            records = w.Records;
        }

        using var fs = File.OpenRead(path);
        foreach (var target in new long[] { 0, 1, 7, 8, 9, fs.Length / 3, fs.Length / 2 + 5, records[^1].Offset - 1 })
        {
            var (position, time) = TlogScanner.AlignToRecord(fs, target);
            var record = records.First(r => r.Offset >= target);
            Assert.AreEqual(record.Offset, position, "target " + target);
            Assert.AreEqual(record.Time.ToLocalTime(), time);
        }

        Assert.AreEqual(fs.Length, TlogScanner.AlignToRecord(fs, fs.Length).Position);
    }

    [TestMethod]
    public void RecordLengthsCoverMavlink1AndSignedMavlink2()
    {
        var path = TlogWriter.TempPath("v1");
        using (var w = new TlogWriter(path))
        {
            w.Write(TlogWriter.T0, MAVLink.MAVLINK_MSG_ID.ATTITUDE, new MAVLink.mavlink_attitude_t(), mavlink1: true);
            w.Write(TlogWriter.T0.AddMilliseconds(10), MAVLink.MAVLINK_MSG_ID.ATTITUDE, new MAVLink.mavlink_attitude_t(1, 1, 1, 1, 1, 1, 1));
        }

        var bytes = File.ReadAllBytes(path);
        Assert.IsTrue(TlogScanner.TryRecord(bytes, 0, out _, out var v1));
        Assert.AreEqual(8 + 6 + 28 + 2, v1);
        Assert.IsTrue(TlogScanner.TryRecord(bytes, v1, out _, out var v2));
        Assert.AreEqual(bytes.Length, v1 + v2);

        // a signed MAVLink 2 frame carries a 13-byte signature
        var signed = (byte[])bytes.Clone();
        signed[v1 + 8 + 2] = MAVLink.MAVLINK_IFLAG_SIGNED;
        Assert.IsTrue(TlogScanner.TryRecord(signed, v1, out _, out var v2Signed));
        Assert.AreEqual(v2 + 13, v2Signed);

        // unknown incompatibility flags: not a frame we can size
        signed[v1 + 8 + 2] = 0x02;
        Assert.IsFalse(TlogScanner.TryRecord(signed, v1, out _, out _));
    }

    [TestMethod]
    public void FilesWithoutRecordsAreRejected()
    {
        var path = TlogWriter.TempPath("empty");
        File.WriteAllBytes(path, []);
        Assert.ThrowsExactly<InvalidDataException>(() => TlogScanner.Inspect(path));
    }
}
