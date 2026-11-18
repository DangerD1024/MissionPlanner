using MissionPlanner.App.Core.ViewModels.Logs;

namespace MissionPlanner.App.Tests.Logs;

[TestClass]
public class SeriesDownsamplerTests
{
    [TestMethod]
    public void ShortSeriesIsUnchanged()
    {
        double[] x = [0, 1, 2], y = [5, 6, 7];
        var (ox, oy) = SeriesDownsampler.MinMax(x, y, 10);
        Assert.AreSame(x, ox);
        Assert.AreSame(y, oy);
    }

    [TestMethod]
    public void LongSeriesKeepsSpikesEndsAndOrder()
    {
        const int n = 1_000_000;
        var x = new double[n];
        var y = new double[n];
        for (var i = 0; i < n; i++)
        {
            x[i] = i * 0.001;
            y[i] = Math.Sin(i * 0.0001);
        }

        y[123_457] = 50;   // spike up
        y[876_543] = -50;  // spike down

        var (ox, oy) = SeriesDownsampler.MinMax(x, y, 10_000);
        Assert.IsTrue(ox.Length <= 10_000, "too many points: " + ox.Length);
        Assert.IsTrue(ox.Length > 9_000, "too few points: " + ox.Length);
        Assert.AreEqual(x[0], ox[0]);
        Assert.AreEqual(x[^1], ox[^1]);
        Assert.AreEqual(50, oy.Max());
        Assert.AreEqual(-50, oy.Min());
        Assert.AreEqual(x[123_457], ox[Array.IndexOf(oy, 50.0)]);
        for (var i = 1; i < ox.Length; i++)
            Assert.IsTrue(ox[i] > ox[i - 1], "x must stay ascending at " + i);
    }

    [TestMethod]
    public void StrideKeepsFirstAndLast()
    {
        var points = Enumerable.Range(0, 1001).ToList();
        var thinned = SeriesDownsampler.Stride(points, 11);
        CollectionAssert.AreEqual(new[] { 0, 100, 200, 300, 400, 500, 600, 700, 800, 900, 1000 }, thinned.ToArray());
        Assert.AreSame(points, SeriesDownsampler.Stride(points, 5000));
    }
}
