using MissionPlanner.App.Core.ViewModels.FlightData;

namespace MissionPlanner.App.Tests.FlightData;

[TestClass]
public class CameraFootprintTests
{
    private const double MetresPerDegLat = 6378137.0 * Math.PI / 180;

    private static (double N, double E) Offset(TrackPoint p, double lat0, double lng0) =>
        ((p.Lat - lat0) * MetresPerDegLat, (p.Lng - lng0) * MetresPerDegLat * Math.Cos(lat0 * Math.PI / 180));

    [TestMethod]
    public void LevelPhotoCoversASquareUnderTheVehicle()
    {
        // 90 degree lens both ways at 100 m: 100 m each side, front-left first
        var c = CameraFootprints.Corners(-35, 149, 100, 0, 0, 0, 90, 90);
        Assert.HasCount(4, c);
        var (n, e) = Offset(c[0], -35, 149);
        Assert.AreEqual(100, n, 0.5, "front");
        Assert.AreEqual(-100, e, 0.5, "left");
        (n, e) = Offset(c[2], -35, 149);
        Assert.AreEqual(-100, n, 0.5, "back");
        Assert.AreEqual(100, e, 0.5, "right");
    }

    [TestMethod]
    public void HeadingTurnsTheFootprint_AndPitchMovesItForward()
    {
        // facing east: the front-left corner is north-east
        var east = CameraFootprints.Corners(-35, 149, 100, 0, 0, 90, 90, 90);
        var (n, e) = Offset(east[0], -35, 149);
        Assert.AreEqual(100, n, 0.5);
        Assert.AreEqual(100, e, 0.5);

        // nose up 20 degrees tilts the down-looking camera forward: the front edge moves out
        var up = CameraFootprints.Corners(-35, 149, 100, 0, 20, 0, 40, 40);
        var level = CameraFootprints.Corners(-35, 149, 100, 0, 0, 0, 40, 40);
        Assert.IsGreaterThan(Offset(level[0], -35, 149).N, Offset(up[0], -35, 149).N);

        Assert.IsEmpty(CameraFootprints.Corners(-35, 149, 0.2, 0, 0, 0, 60, 45), "on the ground: nothing");
    }

    [TestMethod]
    public void PhotosComeFromCameraFeedback_AndClearWithTheTrack()
    {
        var store = new PhotoStore();
        var parse = new MAVLink.MavlinkParse();
        var bytes = parse.GenerateMAVLinkPacket20(MAVLink.MAVLINK_MSG_ID.CAMERA_FEEDBACK, new MAVLink.mavlink_camera_feedback_t
        {
            lat = -353600000, lng = 1491600000, alt_rel = 80, img_idx = 7,
        }, false, 1, 1);
        store.OnPacket(parse.ReadPacket(new MemoryStream(bytes)));
        var photo = store.Snapshot().Single();
        Assert.AreEqual(7, photo.Index);
        Assert.AreEqual(-35.36, photo.Lat, 1e-7);
        Assert.HasCount(4, photo.Corners);
        store.Clear();
        Assert.IsEmpty(store.Snapshot());
    }
}
