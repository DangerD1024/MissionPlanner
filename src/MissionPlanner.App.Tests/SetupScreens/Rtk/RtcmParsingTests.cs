using MissionPlanner.App.Core.ViewModels.SetupScreens.Rtk;

namespace MissionPlanner.App.Tests.SetupScreens.Rtk;

[TestClass]
public class RtcmParsingTests
{
    private static readonly byte[] Base = RtcmFrameBuilder.Type1005(-35.363261, 149.165230, 584.25);
    private static readonly byte[] Gps = RtcmFrameBuilder.Msm4(1074, [3, 7, 12, 30], [45, 38, 27, 50]);

    [TestMethod]
    public void FramerFindsFramesSplitAnywhereAndSkipsNoise()
    {
        var noise = "$GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,*47\r\n"u8.ToArray();
        var stream = noise.Concat(Base).Concat(new byte[] { 0xB5, 0x62, 0x01, 0x02 }).Concat(Gps).Concat(noise).ToArray();

        // byte at a time and all at once give the same frames
        foreach (var chunk in new[] { 1, 7, stream.Length })
        {
            var framer = new RtcmFramer();
            var frames = new List<RtcmFrame>();
            for (var i = 0; i < stream.Length; i += chunk)
                framer.Push(stream.AsSpan(i, Math.Min(chunk, stream.Length - i)), frames);

            Assert.HasCount(2, frames, "chunk " + chunk);
            Assert.AreEqual(1005, frames[0].MessageType);
            CollectionAssert.AreEqual(Base, frames[0].Bytes);
            Assert.AreEqual(1074, frames[1].MessageType);
            CollectionAssert.AreEqual(Gps, frames[1].Bytes);
            Assert.AreEqual(0, framer.CrcErrors);
            Assert.AreEqual(2 * noise.Length + 4, framer.OtherBytes);
        }
    }

    [TestMethod]
    public void FramerResynchronisesInsideACorruptFrame()
    {
        // a frame whose tail is lost: its length runs into the next good frame, the CRC fails, and
        // the framer must still find the good frame that starts inside the bad one
        var truncated = Gps.Take(Gps.Length - 10).ToArray();
        var framer = new RtcmFramer();
        var frames = new List<RtcmFrame>();
        // (a false 0xD3 inside the bad frame may claim a long length; more stream bytes settle it)
        framer.Push(truncated.Concat(Base).Concat(Base).Concat(new byte[RtcmFramer.MaxFrame]).ToArray(), frames);

        Assert.IsGreaterThanOrEqualTo(1, framer.CrcErrors);
        Assert.HasCount(2, frames);
        Assert.IsTrue(frames.All(f => f.MessageType == 1005));
    }

    [TestMethod]
    public void FramerRejectsAFlippedBit()
    {
        var bad = (byte[])Base.Clone();
        bad[10] ^= 0x10;
        var framer = new RtcmFramer();
        var frames = new List<RtcmFrame>();
        framer.Push(bad, frames);
        framer.Push(Gps, frames);

        Assert.AreEqual(1, framer.CrcErrors);
        Assert.HasCount(1, frames);
        Assert.AreEqual(1074, frames[0].MessageType);
    }

    [TestMethod]
    public void BasePositionDecodesWithRtcm3()
    {
        var pos = RtcmMessages.TryGetBasePosition(new RtcmFrame(Base));
        Assert.IsNotNull(pos);
        Assert.AreEqual(-35.363261, pos.Lat, 1e-7);
        Assert.AreEqual(149.165230, pos.Lng, 1e-7);
        Assert.AreEqual(584.25, pos.Alt, 0.01);
        Assert.AreEqual(42, pos.StationId);
        Assert.IsNull(RtcmMessages.TryGetBasePosition(new RtcmFrame(Gps)));
    }

    [TestMethod]
    public void MsmSatelliteMaskAndSignalStrength()
    {
        CollectionAssert.AreEqual(new[] { 3, 7, 12, 30 }, RtcmMessages.MsmSatellites(Gps).ToArray());

        var snr = RtcmMessages.TryGetSignalStrengths(new RtcmFrame(Gps));
        CollectionAssert.AreEqual(new[] { 3, 7, 12, 30 }, snr.Select(s => s.Prn).ToArray());
        CollectionAssert.AreEqual(new[] { 45.0, 38, 27, 50 }, snr.Select(s => s.Snr).ToArray());
        Assert.IsTrue(snr.All(s => s.System == GnssSystem.Gps));

        var glo = RtcmMessages.TryGetSignalStrengths(new RtcmFrame(RtcmFrameBuilder.Msm4(1084, [1, 24], [40, 41])));
        Assert.IsTrue(glo.All(s => s.System == GnssSystem.Glonass));
        Assert.HasCount(2, glo);
    }

    [TestMethod]
    public void MessageNamesAndConstellations()
    {
        Assert.AreEqual("GPS MSM7", RtcmMessages.Describe(1077));
        Assert.AreEqual("BeiDou MSM4", RtcmMessages.Describe(1124));
        Assert.AreEqual("Galileo MSM5", RtcmMessages.Describe(1095));
        Assert.AreEqual("Base station position", RtcmMessages.Describe(1005));
        Assert.AreEqual("GLONASS code-phase biases", RtcmMessages.Describe(1230));
        Assert.AreEqual(GnssSystem.Qzss, RtcmMessages.MsmSystem(1117));
        Assert.AreEqual(GnssSystem.Sbas, RtcmMessages.MsmSystem(1107));
        Assert.IsNull(RtcmMessages.MsmSystem(1230));
        Assert.IsNull(RtcmMessages.MsmSystem(1080));
        Assert.IsNull(RtcmMessages.MsmSystem(1078));
        Assert.AreEqual(GnssSystem.Glonass, RtcmMessages.ObservationSystem(1012));
        Assert.AreEqual(GnssSystem.Gps, RtcmMessages.ObservationSystem(1004));
        Assert.IsTrue(RtcmMessages.IsBasePosition(1006));
        Assert.AreEqual(9, RtcmMessages.LegacySatelliteCount(RtcmFrameBuilder.Type1004(9), 1004));
    }

    [TestMethod]
    public void StatisticsCountTypesAndExpireLikeMissionPlanner()
    {
        var stats = new RtcmStatistics();
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.IsNotNull(stats.Record(new RtcmFrame(Base), t0));
        stats.Record(new RtcmFrame(Gps), t0);
        stats.Record(new RtcmFrame(Gps), t0.AddSeconds(1));
        stats.Record(new RtcmFrame(RtcmFrameBuilder.Msm4(1094, [2, 5, 9], [44, 44, 44])), t0.AddSeconds(1));

        var s = stats.Snapshot(t0.AddSeconds(2));
        Assert.AreEqual(4, s.Frames);
        CollectionAssert.AreEqual(new[] { 1005, 1074, 1094 }, s.Messages.Select(m => m.Type).ToArray());
        Assert.AreEqual(2, s.Messages.Single(m => m.Type == 1074).Count);
        Assert.AreEqual(1.0, s.Messages.Single(m => m.Type == 1074).Age.TotalSeconds, 1e-9);
        Assert.AreEqual(4, s.Constellations.Single(c => c.System == GnssSystem.Gps).Satellites);
        Assert.AreEqual(3, s.Constellations.Single(c => c.System == GnssSystem.Galileo).Satellites);
        Assert.IsTrue(s.Constellations.All(c => c.Active));
        Assert.IsTrue(s.BaseActive);
        Assert.HasCount(7, s.Satellites);
        Assert.AreEqual(-35.363261, s.Base!.Lat, 1e-7);

        // observations count as live for 5 s, the base for 20 s
        s = stats.Snapshot(t0.AddSeconds(7));
        Assert.IsFalse(s.Constellations.Any(c => c.Active));
        Assert.IsTrue(s.BaseActive);
        Assert.IsEmpty(s.Satellites);
        s = stats.Snapshot(t0.AddSeconds(21));
        Assert.IsFalse(s.BaseActive);
        Assert.IsNotNull(s.Base, "the last base position stays known");

        stats.Clear();
        Assert.AreEqual(0, stats.Snapshot(t0).Frames);
    }

    [TestMethod]
    public void SatelliteCountFollowsTheLatestEpoch()
    {
        var stats = new RtcmStatistics();
        var t0 = DateTime.UtcNow;
        stats.Record(new RtcmFrame(RtcmFrameBuilder.Msm4(1074, [1, 2, 3, 4, 5], [40, 40, 40, 40, 40])), t0);
        stats.Record(new RtcmFrame(RtcmFrameBuilder.Msm4(1074, [1, 2], [40, 40])), t0.AddSeconds(1));
        Assert.AreEqual(2, stats.Snapshot(t0.AddSeconds(1)).Constellations.Single().Satellites);
    }
}
