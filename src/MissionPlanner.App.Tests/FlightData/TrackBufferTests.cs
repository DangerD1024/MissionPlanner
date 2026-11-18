using MissionPlanner.App.Core.ViewModels.FlightData;

namespace MissionPlanner.App.Tests.FlightData;

[TestClass]
public class TrackBufferTests
{
    private const double MetrePerDegLat = 1 / 111_319.49;

    [TestMethod]
    public void PointsCloserThanTheSpacingAreDropped()
    {
        var t = new TrackBuffer(capacity: 100, minSpacingMetres: 2);
        Assert.IsTrue(t.Add(-35, 149));
        Assert.IsFalse(t.Add(-35 + 1 * MetrePerDegLat, 149), "1 m away is below the 2 m spacing");
        Assert.IsFalse(t.Add(-35 + 1.9 * MetrePerDegLat, 149));
        Assert.IsTrue(t.Add(-35 + 2.1 * MetrePerDegLat, 149));
        Assert.AreEqual(2, t.Count);
    }

    [TestMethod]
    public void SpacingIsMeasuredFromTheLastStoredPoint()
    {
        // slow drift: each step is below the spacing, but they add up
        var t = new TrackBuffer(capacity: 100, minSpacingMetres: 2);
        t.Add(-35, 149);
        var stored = 0;
        for (var i = 1; i <= 10; i++)
        {
            if (t.Add(-35 + i * 0.5 * MetrePerDegLat, 149))
                stored++;
        }

        // 5 m of drift in 0.5 m steps: a point every 2 m or so
        Assert.IsTrue(stored is >= 2 and <= 3, $"stored {stored}");
    }

    [TestMethod]
    public void LongitudeSpacingAccountsForLatitude()
    {
        // at 60° a degree of longitude is half as long
        var t = new TrackBuffer(capacity: 10, minSpacingMetres: 3);
        t.Add(60, 10);
        Assert.IsFalse(t.Add(60, 10 + 4 * MetrePerDegLat), "4 m of longitude-degree-at-equator is 2 m here");
        Assert.IsTrue(t.Add(60, 10 + 8 * MetrePerDegLat));
    }

    [TestMethod]
    public void NoFixAndNonsenseAreIgnored()
    {
        var t = new TrackBuffer();
        Assert.IsFalse(t.Add(0, 0));
        Assert.IsFalse(t.Add(double.NaN, 149));
        Assert.IsFalse(t.Add(91, 0));
        Assert.AreEqual(0, t.Count);
        Assert.IsNull(t.Last);
    }

    [TestMethod]
    public void FullBufferKeepsTheNewestPointsInOrder()
    {
        var t = new TrackBuffer(capacity: 5, minSpacingMetres: 0.5);
        for (var i = 0; i < 12; i++)
            Assert.IsTrue(t.Add(10 + i * 10 * MetrePerDegLat, 20));

        Assert.AreEqual(5, t.Count);
        for (var i = 0; i < 5; i++)
            Assert.AreEqual(10 + (7 + i) * 10 * MetrePerDegLat, t[i].Lat, 1e-12, $"index {i}");
        Assert.AreEqual(t[4], t.Last);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = t[5]);
    }

    [TestMethod]
    public void VersionMovesOnlyWhenStoredPointsChange()
    {
        var t = new TrackBuffer(capacity: 10, minSpacingMetres: 2);
        var v0 = t.Version;
        t.Add(1, 1);
        var v1 = t.Version;
        Assert.AreNotEqual(v0, v1);
        t.Add(1, 1);
        Assert.AreEqual(v1, t.Version, "a dropped point is not a change");
        t.Clear();
        Assert.AreNotEqual(v1, t.Version);
        Assert.AreEqual(0, t.Count);
        Assert.IsTrue(t.Add(1, 1), "after Clear the next point is stored even if it equals the old last one");
    }

    [TestMethod]
    public void DistanceIsCloseToGreatCircleForShortHops()
    {
        var d = TrackBuffer.DistanceMetres(-35.363261, 149.165230, -35.364261, 149.166230);
        var exact = new MissionPlanner.Utilities.PointLatLngAlt(-35.363261, 149.165230)
            .GetDistance(new MissionPlanner.Utilities.PointLatLngAlt(-35.364261, 149.166230));
        Assert.AreEqual(exact, d, exact * 0.01);
    }
}
