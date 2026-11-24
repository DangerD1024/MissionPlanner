using System.Text;
using MissionPlanner.App.Core.ViewModels.Tools;

namespace MissionPlanner.App.Tests.Tools;

[TestClass]
public class FftTests
{
    private static List<double> Sine(int count, double hz, double rate, double amplitude = 1, double offset = 0) =>
        Enumerable.Range(0, count).Select(i => offset + amplitude * Math.Sin(2 * Math.PI * hz * i / rate)).ToList();

    private static int Peak(double[] v, int from = 1)
    {
        var best = from;
        for (var i = from; i < v.Length; i++)
            if (v[i] > v[best])
                best = i;
        return best;
    }

    [TestMethod]
    public void SpectrumOfASinePeaksAtItsFrequency()
    {
        const double rate = 1000;
        // 125 Hz falls on bin 128 of a 1024-point FFT at 1 kHz
        var samples = Sine(1024, 125, rate).ToArray();
        foreach (var db in new[] { true, false })
        {
            var spectrum = FftAnalysis.Spectrum(samples, 10, db);
            Assert.HasCount(512, spectrum);
            var freq = FftAnalysis.Frequencies(1024, rate);
            Assert.AreEqual(125, freq[Peak(spectrum)], 1e-9);
        }
    }

    [TestMethod]
    public void MagnitudeScalesWithAmplitudeAndDbIsTwentyLog()
    {
        var small = FftAnalysis.Spectrum(Sine(1024, 125, 1000, 1).ToArray(), 10, false);
        var big = FftAnalysis.Spectrum(Sine(1024, 125, 1000, 10).ToArray(), 10, false);
        Assert.AreEqual(10, big[128] / small[128], 1e-6);
        var db = FftAnalysis.Spectrum(Sine(1024, 125, 1000, 10).ToArray(), 10, true);
        Assert.AreEqual(20 * Math.Log10(big[128]), db[128], 1e-6);
    }

    [TestMethod]
    public void AverageOverALogFindsTheVibrationAndDropsLowBins()
    {
        // 80 Hz vibration on a 1 g offset, 8.5 blocks of 1024 at 1 kHz
        var samples = Sine(8704, 80, 1000, 2, -9.81);
        var avg = FftAnalysis.AverageSpectrum(samples, 1000, new FftOptions(10, 5, false))!;
        var freq = FftAnalysis.Frequencies(1024, 1000);
        Assert.AreEqual(80, freq[Peak(avg)], 1.0);
        // under the start frequency is left out
        for (var b = 0; freq[b] < 5; b++)
            Assert.AreEqual(0, avg[b]);
        Assert.IsNull(FftAnalysis.AverageSpectrum(Sine(1024, 80, 1000), 1000, new FftOptions(10, 5, false)));
    }

    [TestMethod]
    public void GyroAndAccelGraphsShareTheirScales()
    {
        var g1 = new FftPlot("GYR0", "FFT GYR0", 1000, [0, 1, 2], [new FftCurve("x", [0, 1, 2])]);
        var g2 = new FftPlot("GYR1", "FFT GYR1", 1000, [0, 1, 2], [new FftCurve("x", [0, 5, 3])]);
        var a = new FftPlot("ACC0", "FFT ACC0", 1000, [0, 1, 2], [new FftCurve("x", [0, -4, 40])]);
        FftAnalysis.ShareScales([g1, g2, a]);
        Assert.AreEqual(g1.YMax, g2.YMax);
        Assert.AreEqual(g1.YMin, g2.YMin);
        Assert.IsGreaterThan(5, g1.YMax);
        Assert.IsLessThan(40.0 + 3, a.YMax);
        Assert.IsGreaterThan(40.0, a.YMax);
    }

    [TestMethod]
    public void WavFileHeaderGivesTheRate()
    {
        const int rate = 8000;
        var samples = Sine(4096, 1000, rate, 10000).Select(v => (short)v).ToArray();
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            w.Write("RIFF"u8);
            w.Write(36 + samples.Length * 2);
            w.Write("WAVE"u8);
            w.Write("fmt "u8);
            w.Write(16);
            w.Write((short)1);
            w.Write((short)1);
            w.Write(rate);
            w.Write(rate * 2);
            w.Write((short)2);
            w.Write((short)16);
            w.Write("data"u8);
            w.Write(samples.Length * 2);
            foreach (var s in samples)
                w.Write(s);
        }

        ms.Position = 0;
        var (read, readRate) = FftAnalysis.ReadWav(ms, 44100);
        Assert.AreEqual(rate, readRate);
        Assert.HasCount(samples.Length, read);
        var plot = FftAnalysis.WavPlot(read, readRate, new FftOptions(10, 5, true), "tone.wav")!;
        Assert.AreEqual(1000, plot.PeakFrequency(0, 5), rate / 1024.0);
    }

    // ---- a binary DataFlash log -------------------------------------------------------------------

    private static void Fmt(BinaryWriter w, byte type, byte length, string name, string format, string columns)
    {
        w.Write((byte)0xA3);
        w.Write((byte)0x95);
        w.Write((byte)128);
        w.Write(type);
        w.Write(length);
        w.Write(Fixed(name, 4));
        w.Write(Fixed(format, 16));
        w.Write(Fixed(columns, 64));
    }

    private static byte[] Fixed(string s, int n)
    {
        var b = new byte[n];
        Encoding.ASCII.GetBytes(s, 0, Math.Min(s.Length, n), b, 0);
        return b;
    }

    /// <summary>ACC1 and GYR1 at 1 kHz: a 120 Hz vibration on the gyro x axis, 60 Hz on the accel z axis.</summary>
    private static string WriteImuLog(int samples)
    {
        var path = Path.Combine(Path.GetTempPath(), "fft-" + Guid.NewGuid().ToString("N") + ".bin");
        using var w = new BinaryWriter(File.Create(path));
        Fmt(w, 128, 89, "FMT", "BBnNZ", "Type,Length,Name,Format,Columns");
        Fmt(w, 200, 23, "ACC1", "Qfff", "TimeUS,AccX,AccY,AccZ");
        Fmt(w, 201, 23, "GYR1", "Qfff", "TimeUS,GyrX,GyrY,GyrZ");
        for (var i = 0; i < samples; i++)
        {
            var t = 10_000_000UL + (ulong)i * 1000;
            var s = i / 1000.0;
            w.Write((byte)0xA3);
            w.Write((byte)0x95);
            w.Write((byte)200);
            w.Write(t);
            w.Write(0.01f);
            w.Write(0.02f);
            w.Write((float)(-9.81 + 3 * Math.Sin(2 * Math.PI * 60 * s)));
            w.Write((byte)0xA3);
            w.Write((byte)0x95);
            w.Write((byte)201);
            w.Write(t);
            w.Write((float)(0.5 * Math.Sin(2 * Math.PI * 120 * s)));
            w.Write(0f);
            w.Write(0.001f);
        }

        return path;
    }

    [TestMethod]
    public void ReadsAccAndGyrFromADataFlashLog()
    {
        var path = WriteImuLog(5000);
        try
        {
            var sensors = FftAnalysis.Read(path, FftSource.AccGyr);
            Assert.HasCount(2, sensors);
            Assert.AreEqual("GYR1", sensors[0].Type);
            Assert.AreEqual("ACC1", sensors[1].Type);
            Assert.HasCount(5000, sensors[0].X);

            var options = new FftOptions(10, 5, true);
            var gyro = FftAnalysis.Plot(sensors[0], options, "test.bin")!;
            var accel = FftAnalysis.Plot(sensors[1], options, "test.bin")!;
            Assert.AreEqual(1000, gyro.SampleRate, 1);
            Assert.AreEqual(120, gyro.PeakFrequency(0, 5), 1.5);
            Assert.AreEqual(60, accel.PeakFrequency(2, 5), 1.5);
            StringAssert.Contains(gyro.Title, "GYR1");
            Assert.AreEqual("GYR", gyro.Group);

            var first = FftAnalysis.Read(path, FftSource.FirstImu);
            Assert.HasCount(2, first);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
