using MissionPlanner.App.Core.ViewModels.SetupScreens.Calibration;

namespace MissionPlanner.App.Tests.SetupScreens;

[TestClass]
public sealed class RadioCalibratorTests
{
    /// <summary>A 16-channel sample: the given leading channels, the rest at the "not connected" 1500.</summary>
    private static float[] Sample(params float[] first)
    {
        var s = Enumerable.Repeat(1500f, RadioCalibrator.Channels).ToArray();
        first.CopyTo(s, 0);
        return s;
    }

    [TestMethod]
    public void Capture_WidensMinAndMax()
    {
        var cal = new RadioCalibrator();

        Assert.IsTrue(cal.Capture(Sample(1500, 1500, 1100)));
        Assert.IsTrue(cal.Capture(Sample(1100, 1950, 1900)));
        Assert.IsTrue(cal.Capture(Sample(1900, 1050, 1500)));

        Assert.AreEqual(1100f, cal.Min[0]);
        Assert.AreEqual(1900f, cal.Max[0]);
        Assert.AreEqual(1050f, cal.Min[1]);
        Assert.AreEqual(1950f, cal.Max[1]);
        Assert.AreEqual(1100f, cal.Min[2]);
        Assert.AreEqual(1900f, cal.Max[2]);
        Assert.AreEqual(3, cal.Samples);
    }

    [TestMethod]
    public void Capture_IgnoresSamplesWithoutAPlausibleChannel1()
    {
        var cal = new RadioCalibrator();

        Assert.IsFalse(cal.Capture(Sample(0, 1000)), "receiver not connected");
        Assert.IsFalse(cal.Capture(Sample(800, 1000)), "800 is the excluded lower bound");
        Assert.IsFalse(cal.Capture(Sample(2200, 1000)), "2200 is the excluded upper bound");
        Assert.IsFalse(cal.Capture([]));

        Assert.AreEqual(3000f, cal.Min[1]);
        Assert.AreEqual(0f, cal.Max[1]);
        Assert.IsFalse(cal.Channel1Valid, "Mission Planner cancels: bad channel 1 input");
        Assert.AreEqual(0, cal.Samples);
    }

    [TestMethod]
    public void Trims_AreTheCentredSticksClampedToTheRange()
    {
        var cal = new RadioCalibrator();
        cal.Capture(Sample(1100, 1100, 1000, 1200));
        cal.Capture(Sample(1900, 1900, 2000, 1800));

        cal.SetTrims(Sample(1520, 1050, 980, 1490));

        Assert.AreEqual(1520f, cal.Trim[0]);
        Assert.AreEqual(1100f, cal.Trim[1], "below the minimum: clamped up");
        Assert.AreEqual(1000f, cal.Trim[2], "throttle down sits at its minimum");
        Assert.AreEqual(1490f, cal.Trim[3]);
    }

    [TestMethod]
    public void OnlyChannelsWithARealRange_AreWritten()
    {
        var cal = new RadioCalibrator();
        // ch1-3 move; ch4 is constant (not connected); ch5 reads 0 on one sample
        cal.Capture(Sample(1100, 1100, 1000, 1500, 0));
        cal.Capture(Sample(1900, 1900, 2000, 1500, 1700));
        cal.SetTrims(Sample(1500, 1500, 1000, 1500, 1000));

        var results = cal.Results();
        Assert.HasCount(16, results);
        Assert.IsTrue(results[0].Valid);
        Assert.IsTrue(results[1].Valid);
        Assert.IsTrue(results[2].Valid);
        Assert.IsFalse(results[3].Valid, "min == max");
        Assert.IsFalse(results[4].Valid, "min == 0");
        Assert.IsTrue(results.Skip(5).All(r => !r.Valid));

        var write = cal.ParamsToWrite();
        CollectionAssert.AreEqual(
            new[] { "RC1_MIN", "RC1_MAX", "RC1_TRIM", "RC2_MIN", "RC2_MAX", "RC2_TRIM", "RC3_MIN", "RC3_MAX", "RC3_TRIM" },
            write.Select(kv => kv.Key).ToArray());
        CollectionAssert.AreEqual(
            new double[] { 1100, 1900, 1500, 1100, 1900, 1500, 1000, 2000, 1000 },
            write.Select(kv => kv.Value).ToArray());
    }

    [TestMethod]
    public void ValidityRules()
    {
        Assert.IsTrue(RadioCalibrator.IsValid(1100, 1900, 1500));
        Assert.IsTrue(RadioCalibrator.IsValid(1100, 1900, 1100), "trim may equal min");
        Assert.IsTrue(RadioCalibrator.IsValid(1100, 1900, 1900), "trim may equal max");
        Assert.IsFalse(RadioCalibrator.IsValid(1900, 1100, 1500), "min > max");
        Assert.IsFalse(RadioCalibrator.IsValid(1500, 1500, 1500), "min == max");
        Assert.IsFalse(RadioCalibrator.IsValid(0, 1900, 1500), "min 0");
        Assert.IsFalse(RadioCalibrator.IsValid(1100, 0, 1500), "max 0");
        Assert.IsFalse(RadioCalibrator.IsValid(1100, 1900, 0), "trim 0");
        Assert.IsFalse(RadioCalibrator.IsValid(1100, 1900, 1950), "trim above max");
        Assert.IsFalse(RadioCalibrator.IsValid(1100, 1900, 1050), "trim below min");
    }

    [TestMethod]
    public void UncapturedChannel_GetsAZeroTrim_AndIsNotWritten()
    {
        var cal = new RadioCalibrator();
        cal.SetTrims(Sample(1500));

        Assert.AreEqual(0f, cal.Trim[0], "min 3000 / max 0 clamps to 0");
        Assert.IsEmpty(cal.ParamsToWrite());
    }

    [TestMethod]
    public void Reset_StartsOver()
    {
        var cal = new RadioCalibrator();
        cal.Capture(Sample(1100));
        cal.Reset();

        Assert.AreEqual(3000f, cal.Min[0]);
        Assert.AreEqual(0f, cal.Max[0]);
        Assert.AreEqual(1500f, cal.Trim[0]);
        Assert.AreEqual(0, cal.Samples);
    }

    [TestMethod]
    public void Summary_ListsTheWrittenChannels()
    {
        var cal = new RadioCalibrator();
        cal.Capture(Sample(1100, 1050));
        cal.Capture(Sample(1900, 1950));
        cal.SetTrims(Sample(1500, 1500));

        Assert.AreEqual("CH1 1100 | 1900\nCH2 1050 | 1950", cal.Summary());
    }

    [TestMethod]
    public void WrittenValues_AreWholeMicroseconds()
    {
        var cal = new RadioCalibrator();
        cal.Capture(Sample(1100.4f));
        cal.Capture(Sample(1899.6f));
        cal.SetTrims(Sample(1500.5f));

        var write = cal.ParamsToWrite().ToDictionary(kv => kv.Key, kv => kv.Value);

        Assert.AreEqual(1100d, write["RC1_MIN"]);
        Assert.AreEqual(1900d, write["RC1_MAX"]);
        Assert.AreEqual(1500d, write["RC1_TRIM"], "banker's rounding of .5");
        Assert.AreEqual("CH1 1100 | 1900", cal.Summary());
    }

    [TestMethod]
    public void RadioMap_DefaultsAndRcmap()
    {
        Assert.AreEqual(RadioMap.Default, RadioMap.From(_ => null));

        var p = new Dictionary<string, double> { ["RCMAP_ROLL"] = 2, ["RCMAP_PITCH"] = 1, ["RCMAP_THROTTLE"] = 3, ["RCMAP_YAW"] = 4 };
        Assert.AreEqual(new RadioMap(2, 1, 3, 4), RadioMap.From(n => p.TryGetValue(n, out var v) ? v : null));

        p["RCMAP_YAW"] = 0;
        p["RCMAP_THROTTLE"] = 17;
        Assert.AreEqual(new RadioMap(2, 1, 3, 4), RadioMap.From(n => p.TryGetValue(n, out var v) ? v : null), "out of range falls back");
    }
}
