using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using MissionPlanner.Log;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.Tools;

/// <summary>Where the samples of an FFT come from (Mission Planner's fftui buttons).</summary>
public enum FftSource
{
    /// <summary>"IMU Batch Sample": ISBH/ISBD, the raw sensor batches of INS_LOG_BAT_MASK.</summary>
    BatchSampler,

    /// <summary>"Run all imus - ACC GYR MSG": ACC1..4 / GYR1..4 (or ACC/GYR with an instance).</summary>
    AccGyr,

    /// <summary>"Run all imus - IMU1-3 MSG": IMU, IMU2, IMU3.</summary>
    ImuMessages,

    /// <summary>"Run Log - imu1 ACC1 GYR1 MSG": the first IMU only.</summary>
    FirstImu,

    /// <summary>"Run 16bit Mono Wav".</summary>
    Wav,
}

/// <summary>The samples of one sensor (three axes, or one channel for a WAV file).</summary>
public sealed class FftSensor(string type)
{
    /// <summary>"GYR0", "ACC1", "IMU2 ACC"... Graph titles group the gyro and accel plots by GYR / ACC.</summary>
    public string Type { get; set; } = type;

    public List<double> X { get; } = new();
    public List<double> Y { get; } = new();
    public List<double> Z { get; } = new();

    /// <summary>Hz; 0 until known, then the sensor's own rate (ISBH) or the rate measured from the timestamps.</summary>
    public double SampleRate { get; set; }

    internal double FirstTimeMs = double.NaN;
    internal double LastTimeMs = double.NaN;
    internal long Stamps;

    /// <summary>Counts a timestamp (ms) for the measured rate; false for one that goes backwards (skipped, as Mission Planner).</summary>
    internal bool Stamp(double timeMs)
    {
        if (!double.IsNaN(LastTimeMs) && timeMs < LastTimeMs)
            return false;
        if (double.IsNaN(FirstTimeMs))
            FirstTimeMs = timeMs;
        if (timeMs != LastTimeMs)
            Stamps++;
        LastTimeMs = timeMs;
        return true;
    }

    /// <summary>Samples per second from the first and last distinct timestamps.</summary>
    internal double MeasuredRate =>
        Stamps > 1 && LastTimeMs > FirstTimeMs ? Math.Round(1000.0 * (Stamps - 1) / (LastTimeMs - FirstTimeMs), 1) : 0;
}

public sealed record FftCurve(string Name, double[] Values);

/// <summary>One graph: the averaged spectrum of a sensor's axes.</summary>
public sealed class FftPlot(string sensor, string title, double sampleRate, double[] frequencies, IReadOnlyList<FftCurve> curves)
{
    public string Sensor { get; } = sensor;

    public string Title { get; } = title;

    public double SampleRate { get; } = sampleRate;

    /// <summary>Hz of each bin (N/2 of them).</summary>
    public double[] Frequencies { get; } = frequencies;

    public IReadOnlyList<FftCurve> Curves { get; } = curves;

    /// <summary>Y range of the graph; plots of the same kind (gyro, accel) share it so they compare at a glance.</summary>
    public double YMin { get; set; }

    public double YMax { get; set; }

    /// <summary>"GYR", "ACC" or "" (the kind Mission Planner scales together).</summary>
    public string Group => Title.Contains("GYR", StringComparison.Ordinal) ? "GYR" : Title.Contains("ACC", StringComparison.Ordinal) ? "ACC" : "";

    /// <summary>Frequency of the highest bin of a curve, at or above <paramref name="fromHz"/>.</summary>
    public double PeakFrequency(int curve, double fromHz = 0)
    {
        var v = Curves[curve].Values;
        var best = -1;
        for (var i = 0; i < v.Length; i++)
        {
            if (Frequencies[i] < fromHz || v[i] == 0)
                continue;
            if (best < 0 || v[i] > v[best])
                best = i;
        }

        return best < 0 ? double.NaN : Frequencies[best];
    }
}

/// <summary>Options of the FFT window: bins as a power of two, the lowest frequency drawn, dB or magnitude.</summary>
public readonly record struct FftOptions(int Bins = 10, double StartFrequency = 5, bool Decibels = true)
{
    public const int MinBins = 4;
    public const int MaxBins = 16;

    public int N => 1 << Bins;
}

/// <summary>
/// Mission Planner's IMU FFT (Controls/fftui.cs): Hann-windowed FFTs of N = 2^bins samples
/// (<see cref="FFT2"/>), averaged over the log, bins below the start frequency left out.
/// </summary>
public static class FftAnalysis
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Frequency of each of the N/2 bins.</summary>
    public static double[] Frequencies(int n, double sampleRate)
    {
        var f = new double[n / 2];
        for (var i = 0; i < f.Length; i++)
            f[i] = i * sampleRate / n;
        return f;
    }

    /// <summary>The spectrum of exactly N = 2^bins samples (N/2 values).</summary>
    public static double[] Spectrum(ReadOnlySpan<double> samples, int bins, bool decibels)
    {
        var n = 1 << bins;
        if (samples.Length != n)
            throw new ArgumentException($"An FFT of 2^{bins} needs {n} samples.", nameof(samples));
        var copy = samples.ToArray();
        return new FFT2().rin(copy, (uint)bins, decibels);
    }

    /// <summary>
    /// Mission Planner's average over a log: consecutive blocks of N samples, the last (partial)
    /// one left out, each bin the sum of the block spectra over the number of whole blocks; bins
    /// under the start frequency are zero. Null when there are not more than N samples.
    /// </summary>
    public static double[]? AverageSpectrum(List<double> samples, double sampleRate, FftOptions options)
    {
        var n = options.N;
        if (samples.Count <= n)
            return null;
        var freq = Frequencies(n, sampleRate);
        var avg = new double[n / 2];
        var span = CollectionsMarshal.AsSpan(samples);
        var count = samples.Count / n;
        var blocks = count;
        for (var done = 0; done < blocks - 1; done++)
        {
            var spectrum = Spectrum(span.Slice(n * done, n), options.Bins, options.Decibels);
            for (var b = 0; b < avg.Length; b++)
            {
                if (freq[b] < options.StartFrequency)
                    continue;
                avg[b] += spectrum[b] / count;
            }
        }

        return avg;
    }

    /// <summary>The graph of one sensor (x, y, z), or null when it has too few samples or no rate.</summary>
    public static FftPlot? Plot(FftSensor sensor, FftOptions options, string fileName)
    {
        var rate = sensor.SampleRate > 0 ? sensor.SampleRate : sensor.MeasuredRate;
        if (rate <= 0 || sensor.X.Count <= options.N)
            return null;
        var curves = new List<FftCurve>();
        foreach (var (axis, data) in new[] { ("x", sensor.X), ("y", sensor.Y), ("z", sensor.Z) })
        {
            if (data.Count <= options.N)
                continue;
            if (AverageSpectrum(data, rate, options) is { } avg)
                curves.Add(new FftCurve($"{sensor.Type} {axis}", avg));
        }

        if (curves.Count == 0)
            return null;
        var title = $"FFT {sensor.Type} - {fileName} - {rate.ToString("0.#", Inv)}hz input";
        return new FftPlot(sensor.Type, title, rate, Frequencies(options.N, rate), curves);
    }

    /// <summary>Mission Planner's SetScale: the gyro graphs share one Y range, the accel graphs another.</summary>
    public static void ShareScales(IReadOnlyList<FftPlot> plots)
    {
        foreach (var plot in plots)
        {
            var (min, max) = Range(plot);
            plot.YMin = min;
            plot.YMax = max;
        }

        foreach (var group in plots.Where(p => p.Group.Length > 0).GroupBy(p => p.Group))
        {
            var min = group.Min(p => p.YMin);
            var max = group.Max(p => p.YMax);
            foreach (var p in group)
            {
                p.YMin = min;
                p.YMax = max;
            }
        }
    }

    private static (double Min, double Max) Range(FftPlot plot)
    {
        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        foreach (var c in plot.Curves)
        {
            for (var i = 0; i < c.Values.Length; i++)
            {
                var v = c.Values[i];
                // the zeroed bins under the start frequency are not data
                if (v == 0 || double.IsNaN(v) || double.IsInfinity(v))
                    continue;
                min = Math.Min(min, v);
                max = Math.Max(max, v);
            }
        }

        if (double.IsInfinity(min))
            return (0, 1);
        var pad = Math.Max(1e-6, (max - min) * 0.05);
        return (min - pad, max + pad);
    }

    // ---- reading DataFlash logs ---------------------------------------------------------------

    /// <summary>Reads the samples a source needs from a .bin/.log; the sensors in a stable order (gyros first).</summary>
    public static List<FftSensor> Read(string path, FftSource source, CancellationToken cancel = default)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var file = new DFLogBuffer(stream);
        return source switch
        {
            FftSource.BatchSampler => ReadBatch(file, cancel),
            FftSource.AccGyr => ReadAccGyr(file, cancel, firstOnly: false),
            FftSource.FirstImu => ReadAccGyr(file, cancel, firstOnly: true),
            FftSource.ImuMessages => ReadImu(file, cancel),
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };
    }

    private static double Num(object? value) => value switch
    {
        null => double.NaN,
        double d => d,
        float f => f,
        string s => double.TryParse(s, NumberStyles.Float, Inv, out var v) ? v : double.NaN,
        IConvertible c => c.ToDouble(Inv),
        _ => double.NaN,
    };

    /// <summary>The sensor number: the instance field, else the digit of ACC1/GYR2, else 0.</summary>
    private static int InstanceOf(DFLog.DFItem item)
    {
        var inst = item.instance;
        if (!string.IsNullOrEmpty(inst) && int.TryParse(inst, NumberStyles.Integer, Inv, out var i))
            return i;
        var digits = item.msgtype.Length > 3 ? item.msgtype[3..] : "";
        return int.TryParse(digits, NumberStyles.Integer, Inv, out var n) ? n - 1 : 0;
    }

    private static List<FftSensor> ReadAccGyr(DFLogBuffer file, CancellationToken cancel, bool firstOnly)
    {
        var gyro = new SortedDictionary<int, FftSensor>();
        var accel = new SortedDictionary<int, FftSensor>();
        var offsets = new Dictionary<string, (int X, int Y, int Z, int T, double Scale)>();
        var types = firstOnly ? new[] { "ACC1", "GYR1", "ACC", "GYR" } : new[] { "ACC1", "GYR1", "ACC2", "GYR2", "ACC3", "GYR3", "ACC4", "GYR4", "ACC", "GYR" };
        long n = 0;
        foreach (var item in file.GetEnumeratorType(types))
        {
            if ((++n & 0xfff) == 0)
                cancel.ThrowIfCancellationRequested();
            var type = item.msgtype;
            if (string.IsNullOrEmpty(type))
                continue;
            var isAcc = type.StartsWith("ACC", StringComparison.Ordinal);
            var isGyr = type.StartsWith("GYR", StringComparison.Ordinal);
            if (!isAcc && !isGyr)
                continue;
            var instance = InstanceOf(item);
            if (firstOnly && instance != 0)
                continue;

            if (!offsets.TryGetValue(type, out var o))
            {
                var p = isAcc ? "Acc" : "Gyr";
                o = (file.dflog.FindMessageOffset(type, p + "X"), file.dflog.FindMessageOffset(type, p + "Y"),
                    file.dflog.FindMessageOffset(type, p + "Z"), file.dflog.FindMessageOffset(type, "TimeUS"), 1000.0);
                if (o.T < 0)
                    o = (o.X, o.Y, o.Z, file.dflog.FindMessageOffset(type, "TimeMS"), 1.0);
                offsets[type] = o;
            }

            if (o.X < 0 || o.Y < 0 || o.Z < 0)
                continue;

            var set = isAcc ? accel : gyro;
            if (!set.TryGetValue(instance, out var s))
                set[instance] = s = new FftSensor(type);
            var raw = item.raw;
            if (o.T >= 0)
            {
                if (!s.Stamp(Num(raw[o.T]) / o.Scale))
                    continue;
            }

            s.X.Add(Num(raw[o.X]));
            s.Y.Add(Num(raw[o.Y]));
            s.Z.Add(Num(raw[o.Z]));
        }

        return gyro.Values.Concat(accel.Values).ToList();
    }

    private static List<FftSensor> ReadImu(DFLogBuffer file, CancellationToken cancel)
    {
        var gyro = new SortedDictionary<int, FftSensor>();
        var accel = new SortedDictionary<int, FftSensor>();
        long n = 0;
        foreach (var item in file.GetEnumeratorType(new[] { "IMU", "IMU2", "IMU3" }))
        {
            if ((++n & 0xfff) == 0)
                cancel.ThrowIfCancellationRequested();
            var type = item.msgtype;
            if (string.IsNullOrEmpty(type) || !type.StartsWith("IMU", StringComparison.Ordinal))
                continue;
            // IMU2/IMU3 in old logs; one IMU message with an instance field in newer ones
            var sensor = type == "IMU2" ? 1 : type == "IMU3" ? 2 : 0;
            var name = type;
            if (type == "IMU" && !string.IsNullOrEmpty(item.instance) && int.TryParse(item.instance, NumberStyles.Integer, Inv, out var inst))
            {
                sensor = inst;
                name = "IMU" + (inst == 0 ? "" : (inst + 1).ToString(Inv));
            }

            var ax = file.dflog.FindMessageOffset(type, "AccX");
            var ay = file.dflog.FindMessageOffset(type, "AccY");
            var az = file.dflog.FindMessageOffset(type, "AccZ");
            var gx = file.dflog.FindMessageOffset(type, "GyrX");
            var gy = file.dflog.FindMessageOffset(type, "GyrY");
            var gz = file.dflog.FindMessageOffset(type, "GyrZ");
            var to = file.dflog.FindMessageOffset(type, "TimeUS");
            var scale = 1000.0;
            if (to < 0)
            {
                to = file.dflog.FindMessageOffset(type, "TimeMS");
                scale = 1;
            }

            var raw = item.raw;
            var time = to >= 0 ? Num(raw[to]) / scale : double.NaN;
            if (ax >= 0 && ay >= 0 && az >= 0)
            {
                if (!accel.TryGetValue(sensor, out var a))
                    accel[sensor] = a = new FftSensor(name + " ACC");
                if (double.IsNaN(time) || a.Stamp(time))
                {
                    a.X.Add(Num(raw[ax]));
                    a.Y.Add(Num(raw[ay]));
                    a.Z.Add(Num(raw[az]));
                }
            }

            if (gx >= 0 && gy >= 0 && gz >= 0)
            {
                if (!gyro.TryGetValue(sensor, out var g))
                    gyro[sensor] = g = new FftSensor(name + " GYR");
                if (double.IsNaN(time) || g.Stamp(time))
                {
                    g.X.Add(Num(raw[gx]));
                    g.Y.Add(Num(raw[gy]));
                    g.Z.Add(Num(raw[gz]));
                }
            }
        }

        return gyro.Values.Concat(accel.Values).ToList();
    }

    /// <summary>The batch sampler: ISBH heads each batch (sensor type, instance, rate, multiplier), ISBD carry int16 x/y/z.</summary>
    private static List<FftSensor> ReadBatch(DFLogBuffer file, CancellationToken cancel)
    {
        var sensors = new SortedDictionary<int, FftSensor>();
        var headerN = -1;
        var key = -1;
        var multiplier = 1.0;
        int ox = -1, oy = -1, oz = -1, ot = -1, on = -1;
        long n = 0;
        foreach (var item in file.GetEnumeratorType(new[] { "ISBH", "ISBD" }))
        {
            if ((++n & 0xfff) == 0)
                cancel.ThrowIfCancellationRequested();
            var type = item.msgtype;
            if (type == "ISBH")
            {
                headerN = (int)Num(item.raw[file.dflog.FindMessageOffset(type, "N")]);
                var sensorType = (int)Num(item.raw[file.dflog.FindMessageOffset(type, "type")]);
                var instance = (int)Num(item.raw[file.dflog.FindMessageOffset(type, "instance")]);
                multiplier = Num(item.raw[file.dflog.FindMessageOffset(type, "mul")]);
                if (!(multiplier > 0) && !(multiplier < 0))
                    multiplier = 1;
                // gyros first, as the other sources
                key = (sensorType == 1 ? 0 : 100) + instance;
                if (!sensors.TryGetValue(key, out var s))
                    sensors[key] = s = new FftSensor((sensorType == 0 ? "ACC" : sensorType == 1 ? "GYR" : "SENS" + sensorType) + instance.ToString(Inv));
                s.SampleRate = Num(item.raw[file.dflog.FindMessageOffset(type, "smp_rate")]);
            }
            else if (type == "ISBD")
            {
                if (key < 0 || !sensors.TryGetValue(key, out var s))
                    continue;
                if (ox < 0)
                {
                    ox = file.dflog.FindMessageOffset(type, "x");
                    oy = file.dflog.FindMessageOffset(type, "y");
                    oz = file.dflog.FindMessageOffset(type, "z");
                    ot = file.dflog.FindMessageOffset(type, "TimeUS");
                    on = file.dflog.FindMessageOffset(type, "N");
                }

                if (on >= 0 && (int)Num(item.raw[on]) != headerN)
                    continue;
                if (ot >= 0 && !s.Stamp(Num(item.raw[ot]) / 1000.0))
                    continue;
                if (item.raw[ox] is not BinaryLog.UnionArray ux || item.raw[oy] is not BinaryLog.UnionArray uy ||
                    item.raw[oz] is not BinaryLog.UnionArray uz)
                    continue;
                foreach (var v in ux.Shorts)
                    s.X.Add(v / multiplier);
                foreach (var v in uy.Shorts)
                    s.Y.Add(v / multiplier);
                foreach (var v in uz.Shorts)
                    s.Z.Add(v / multiplier);
            }
        }

        return sensors.Values.ToList();
    }

    // ---- WAV ----------------------------------------------------------------------------------

    /// <summary>The 16-bit samples of a WAV file (the first channel) and its rate; a headerless file is read as raw 16-bit at <paramref name="defaultRate"/>.</summary>
    public static (List<double> Samples, int SampleRate) ReadWav(Stream stream, int defaultRate)
    {
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        var samples = new List<double>();
        var rate = defaultRate;
        var channels = 1;
        var bits = 16;
        long dataLength = stream.Length;

        var header = reader.ReadBytes(12);
        if (header.Length == 12 && Encoding.ASCII.GetString(header, 0, 4) == "RIFF" && Encoding.ASCII.GetString(header, 8, 4) == "WAVE")
        {
            while (stream.Position + 8 <= stream.Length)
            {
                var id = Encoding.ASCII.GetString(reader.ReadBytes(4));
                var size = reader.ReadUInt32();
                if (id == "fmt ")
                {
                    var start = stream.Position;
                    reader.ReadUInt16(); // format
                    channels = Math.Max((int)reader.ReadUInt16(), 1);
                    rate = (int)reader.ReadUInt32();
                    reader.ReadUInt32();
                    reader.ReadUInt16();
                    bits = reader.ReadUInt16();
                    stream.Position = start + size;
                }
                else if (id == "data")
                {
                    dataLength = Math.Min(size, stream.Length - stream.Position);
                    break;
                }
                else
                {
                    stream.Position += size + (size & 1);
                }
            }

            if (bits != 16)
                throw new InvalidDataException($"The WAV file has {bits}-bit samples; 16-bit is needed.");
        }
        else
        {
            // Mission Planner read any file as raw 16-bit mono
            stream.Position = 0;
        }

        var end = stream.Position + dataLength;
        var frame = 2 * channels;
        var buf = new byte[frame];
        while (stream.Position + frame <= end && stream.Read(buf, 0, frame) == frame)
            samples.Add(BitConverter.ToInt16(buf, 0));
        return (samples, rate);
    }

    /// <summary>
    /// The WAV spectrum as Mission Planner drew it: blocks of N with 50% overlap, a running mean of
    /// their spectra, bins under the start frequency zeroed.
    /// </summary>
    public static FftPlot? WavPlot(List<double> samples, int sampleRate, FftOptions options, string fileName)
    {
        var n = options.N;
        if (samples.Count < n || sampleRate <= 0)
            return null;
        var span = CollectionsMarshal.AsSpan(samples);
        var avg = new double[n / 2];
        var freq = Frequencies(n, sampleRate);
        double[] last = [];
        var k = 0;
        for (var start = 0; start + n <= samples.Count; start += n / 2)
        {
            k++;
            last = Spectrum(span.Slice(start, n), options.Bins, options.Decibels);
            for (var b = 0; b < avg.Length; b++)
                avg[b] = avg[b] * (1.0 - 1.0 / k) + last[b] / k;
        }

        for (var b = 0; b < avg.Length && freq[b] < options.StartFrequency; b++)
            avg[b] = 0;
        return new FftPlot("WAV", $"FFT - {fileName} - {sampleRate.ToString(Inv)}hz input", sampleRate, freq,
            [new FftCurve("Avg", avg), new FftCurve("Last block", last)]);
    }
}
