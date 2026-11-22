using GMap.NET.MapProviders;
using MissionPlanner.App.Core.Maps;

namespace MissionPlanner.App.Tests.Maps;

[TestClass]
public class GoogleVersionsTests
{
    [TestMethod]
    public void SatelliteVersionIsReadFromTheMapsApiPage()
    {
        // as the page has it: the tile URL inside a JSON array, with escaped ampersands
        const string page = "[0.0099,[null,[[\"https://khms0.googleapis.com/kh?v=1017\\u0026hl=en-US\\u0026\",\"https://khms1.googleapis.com/kh?v=1017\"]]]]";
        var v = GoogleVersions.Parse(page);
        Assert.AreEqual("1017", v.Satellite);
        Assert.IsNull(v.Road);
        Assert.IsNull(v.Labels);

        var old = GMapProviders.GoogleSatelliteMap.Version;
        try
        {
            GoogleVersions.Apply(v);
            Assert.AreEqual("1017", GMapProviders.GoogleSatelliteMap.Version);
            Assert.AreEqual("s@1017", GMapProviders.GoogleChinaSatelliteMap.Version);
        }
        finally
        {
            GMapProviders.GoogleSatelliteMap.Version = old;
        }

        Assert.AreEqual((null, "m@123", "h@456"),
            GoogleVersions.Parse("http://mt0.google.com/vt?lyrs=m@123 http://mt1.google.com/vt?lyrs=h@456"));
        Assert.AreEqual((null, null, null), GoogleVersions.Parse("<html>nothing</html>"));
    }
}
