namespace MissionPlanner.App.Core.ViewModels.Logs;

/// <summary>
/// Thins a long series for drawing while keeping its shape: the points are split into equal
/// buckets and each bucket keeps its lowest and highest sample (in time order), so spikes and
/// steps survive. A series at or under the limit comes back as it is.
/// </summary>
public static class SeriesDownsampler
{
    /// <summary>Points a graphed series is thinned to.</summary>
    public const int DefaultMaxPoints = 200_000;

    public static (double[] X, double[] Y) MinMax(double[] x, double[] y, int maxPoints = DefaultMaxPoints)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        if (x.Length != y.Length)
            throw new ArgumentException("x and y differ in length");
        if (maxPoints < 4)
            throw new ArgumentOutOfRangeException(nameof(maxPoints));

        var n = x.Length;
        if (n <= maxPoints)
            return (x, y);

        // first and last points are kept as they are, so the time range does not shrink
        var buckets = (maxPoints - 2) / 2;
        var outX = new double[buckets * 2 + 2];
        var outY = new double[outX.Length];
        var o = 0;
        outX[o] = x[0];
        outY[o++] = y[0];

        var inner = n - 2;
        for (var b = 0; b < buckets; b++)
        {
            var start = 1 + (int)((long)inner * b / buckets);
            var end = 1 + (int)((long)inner * (b + 1) / buckets);
            if (end <= start)
                continue;

            int lo = start, hi = start;
            for (var i = start + 1; i < end; i++)
            {
                if (y[i] < y[lo])
                    lo = i;
                if (y[i] > y[hi])
                    hi = i;
            }

            var first = Math.Min(lo, hi);
            var second = Math.Max(lo, hi);
            outX[o] = x[first];
            outY[o++] = y[first];
            if (second != first)
            {
                outX[o] = x[second];
                outY[o++] = y[second];
            }
        }

        outX[o] = x[n - 1];
        outY[o++] = y[n - 1];

        if (o != outX.Length)
        {
            Array.Resize(ref outX, o);
            Array.Resize(ref outY, o);
        }

        return (outX, outY);
    }

    /// <summary>Every k-th point so at most <paramref name="maxPoints"/> remain, first and last included.</summary>
    public static IReadOnlyList<T> Stride<T>(IReadOnlyList<T> points, int maxPoints)
    {
        if (points.Count <= maxPoints || maxPoints < 2)
            return points;

        var step = (double)(points.Count - 1) / (maxPoints - 1);
        var result = new List<T>(maxPoints);
        for (var i = 0; i < maxPoints; i++)
            result.Add(points[(int)Math.Round(i * step)]);
        return result;
    }
}
