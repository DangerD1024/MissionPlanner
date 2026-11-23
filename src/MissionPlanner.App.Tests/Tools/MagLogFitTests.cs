using MissionPlanner.App.Core.ViewModels.Logs;
using MissionPlanner.App.Core.ViewModels.Tools;
using MissionPlanner.App.Tests.Logs;

namespace MissionPlanner.App.Tests.Tools;

[TestClass]
public class MagLogFitTests
{
    /// <summary>Points on a sphere of radius r around c, all directions, with a little noise.</summary>
    private static List<(double X, double Y, double Z)> Sphere((double X, double Y, double Z) c, double r, int n = 600, int seed = 1)
    {
        var rnd = new Random(seed);
        var list = new List<(double, double, double)>();
        for (var i = 0; i < n; i++)
        {
            double x, y, z, len;
            do
            {
                x = rnd.NextDouble() * 2 - 1;
                y = rnd.NextDouble() * 2 - 1;
                z = rnd.NextDouble() * 2 - 1;
                len = Math.Sqrt(x * x + y * y + z * z);
            }
            while (len < 0.1 || len > 1);
            var noise = (rnd.NextDouble() - 0.5) * 4;
            list.Add((c.X + x / len * (r + noise), c.Y + y / len * (r + noise), c.Z + z / len * (r + noise)));
        }

        return list;
    }

    [TestMethod]
    public void SphereFitFindsTheCentreAndRadius()
    {
        var fit = MagLogFit.Sphere(Sphere((120, -45, 300), 480))!.Value;
        Assert.AreEqual(120, fit.X, 1.5);
        Assert.AreEqual(-45, fit.Y, 1.5);
        Assert.AreEqual(300, fit.Z, 1.5);
        Assert.AreEqual(480, fit.R, 1.5);
        Assert.IsLessThan(2.0, fit.Rms);
        Assert.IsNull(MagLogFit.Sphere([(1, 1, 1), (2, 2, 2)]), "too few points");
    }

    [TestMethod]
    public void ThinningKeepsAFewSamplesPerCell()
    {
        var still = Enumerable.Repeat((100.0, 100.0, 100.0), 500).ToList();
        Assert.HasCount(3, MagLogFit.Thin(still));
        Assert.IsEmpty(MagLogFit.Thin([(0, 0, 0)]), "zero readings are dropped");
    }

    [TestMethod]
    public void DataFlashLogGivesOffsetsForEachCompass()
    {
        // the vehicle logged MAG = raw field + its current offsets (10, 20, 30); the true offsets are -(c)
        var w = new LogTestFiles.BinWriter();
        w.Fmt(130, "MAG", "QffffffB", "TimeUS,MagX,MagY,MagZ,OfsX,OfsY,OfsZ,Health");
        w.Fmt(131, "MAG2", "QffffffB", "TimeUS,MagX,MagY,MagZ,OfsX,OfsY,OfsZ,Health");
        var raw1 = Sphere((120, -45, 300), 450);
        var raw2 = Sphere((-60, 80, -20), 400, seed: 2);
        for (var i = 0; i < raw1.Count; i++)
        {
            var (x, y, z) = raw1[i];
            w.Msg(130, "QffffffB", (ulong)(i * 10000), (float)(x + 10), (float)(y + 20), (float)(z + 30), 10f, 20f, 30f, (byte)1);
            var (x2, y2, z2) = raw2[i];
            w.Msg(131, "QffffffB", (ulong)(i * 10000), (float)x2, (float)y2, (float)z2, 0f, 0f, 0f, (byte)1);
        }

        var path = Path.Combine(TestEnvironment.DataRoot, "mag-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, w.ToArray());
        using var log = LogSource.Open(path);
        var fits = MagLogFit.FromLog(log);

        Assert.HasCount(2, fits);
        Assert.AreEqual(1, fits[0].Compass);
        Assert.AreEqual(-120, fits[0].OfsX, 3);
        Assert.AreEqual(45, fits[0].OfsY, 3);
        Assert.AreEqual(-300, fits[0].OfsZ, 3);
        Assert.IsTrue(fits[0].IsGood, fits[0].Text);
        Assert.AreEqual(2, fits[1].Compass);
        Assert.AreEqual(60, fits[1].OfsX, 3);
        Assert.AreEqual(("COMPASS_OFS2_X", "COMPASS_OFS2_Y", "COMPASS_OFS2_Z"), MagLogFit.ParamNames(2));
        Assert.AreEqual(("COMPASS_OFS_X", "COMPASS_OFS_Y", "COMPASS_OFS_Z"), MagLogFit.ParamNames(1));
    }
}
