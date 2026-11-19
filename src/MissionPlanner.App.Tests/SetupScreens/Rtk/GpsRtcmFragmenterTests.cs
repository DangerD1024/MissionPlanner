using MissionPlanner.App.Core.ViewModels.SetupScreens.Rtk;

namespace MissionPlanner.App.Tests.SetupScreens.Rtk;

[TestClass]
public class GpsRtcmFragmenterTests
{
    private static byte[] Data(int n) => Enumerable.Range(0, n).Select(i => (byte)(i * 7 + 3)).ToArray();

    [TestMethod]
    public void ShortDataIsOneUnfragmentedMessage()
    {
        var f = new GpsRtcmFragmenter { NextSequence = 5 };
        var p = f.Fragment(Data(100));

        Assert.HasCount(1, p);
        Assert.AreEqual(5 << 3, p[0].flags, "not fragmented, fragment 0, sequence 5");
        Assert.AreEqual(100, p[0].len);
        Assert.HasCount(180, p[0].data);
        CollectionAssert.AreEqual(Data(100), p[0].data.Take(100).ToArray());
        Assert.AreEqual(6, f.NextSequence);
    }

    [TestMethod]
    public void LongDataIsSplitInto180ByteFragments()
    {
        var f = new GpsRtcmFragmenter { NextSequence = 9 };
        var p = f.Fragment(Data(400));

        CollectionAssert.AreEqual(new byte[] { 180, 180, 40 }, p.Select(x => x.len).ToArray());
        CollectionAssert.AreEqual(new[] { 1 | 0 << 1 | 9 << 3, 1 | 1 << 1 | 9 << 3, 1 | 2 << 1 | 9 << 3 },
            p.Select(x => (int)x.flags).ToArray());
    }

    [TestMethod]
    public void ExactMultipleOf180EndsWithAnEmptyFragment()
    {
        var p = new GpsRtcmFragmenter().Fragment(Data(360));
        CollectionAssert.AreEqual(new byte[] { 180, 180, 0 }, p.Select(x => x.len).ToArray());
        Assert.AreEqual(1 | 2 << 1, p[2].flags);

        // 180 bytes: a full fragment and an empty one, as Mission Planner sends it
        p = new GpsRtcmFragmenter().Fragment(Data(180));
        CollectionAssert.AreEqual(new byte[] { 180, 0 }, p.Select(x => x.len).ToArray());
        Assert.IsTrue(p.All(x => (x.flags & 1) == 1));
    }

    [TestMethod]
    public void FourFullFragmentsNeedNoTerminator()
    {
        var p = new GpsRtcmFragmenter().Fragment(Data(720));
        CollectionAssert.AreEqual(new byte[] { 180, 180, 180, 180 }, p.Select(x => x.len).ToArray());
    }

    [TestMethod]
    public void FramesOver720BytesUseConsecutiveSequences()
    {
        var f = new GpsRtcmFragmenter { NextSequence = 31 };
        var p = f.Fragment(Data(1029));

        Assert.HasCount(6, p);
        Assert.IsTrue(p.Take(4).All(x => x.flags >> 3 == 31));
        Assert.IsTrue(p.Skip(4).All(x => x.flags >> 3 == 0), "the sequence wraps at 32");
        CollectionAssert.AreEqual(new byte[] { 180, 180, 180, 180, 180, 129 }, p.Select(x => x.len).ToArray());
        Assert.AreEqual(1, f.NextSequence);
    }

    [TestMethod]
    public void ArduPilotReassemblesEverySize()
    {
        // what AP_GPS::handle_gps_rtcm_fragment rebuilds must be exactly the frames, back to back
        var f = new GpsRtcmFragmenter();
        var vehicle = new ArduPilotRtcmReassembler();
        var expected = new List<byte>();
        for (var n = 1; n <= 1029; n += n < 200 ? 1 : 13)
        {
            var data = Data(n);
            expected.AddRange(data);
            foreach (var p in f.Fragment(data))
                vehicle.Handle(p);
        }

        foreach (var n in new[] { 179, 180, 181, 359, 360, 361, 539, 540, 541, 719, 720, 721, 900, 1029 })
        {
            var data = Data(n);
            expected.AddRange(data);
            foreach (var p in f.Fragment(data))
                vehicle.Handle(p);
        }

        Assert.AreEqual(0, vehicle.Discarded);
        CollectionAssert.AreEqual(expected, vehicle.Injected);
    }

    [TestMethod]
    public void ALostFragmentDropsOnlyItsBlock()
    {
        var f = new GpsRtcmFragmenter();
        var vehicle = new ArduPilotRtcmReassembler();
        var first = f.Fragment(Data(400));
        var second = f.Fragment(Data(50));

        vehicle.Handle(first[0]);
        vehicle.Handle(first[2]); // fragment 1 lost on the radio
        foreach (var p in second)
            vehicle.Handle(p);

        CollectionAssert.AreEqual(Data(50), vehicle.Injected);
    }
}
