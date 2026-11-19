using System.Text;
using MissionPlanner.App.Core.ViewModels.FlightData;

namespace MissionPlanner.App.Tests.FlightData;

[TestClass]
public class AdsbTrafficTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static MAVLink.mavlink_adsb_vehicle_t Plane(uint icao, string callsign = "ABC", double lat = -35, double lng = 149,
        ushort flags = (ushort)(MAVLink.ADSB_FLAGS.VALID_COORDS | MAVLink.ADSB_FLAGS.VALID_SQUAWK), byte emitter = 1)
    {
        var cs = new byte[9];
        Encoding.ASCII.GetBytes(callsign).CopyTo(cs, 0);
        return new MAVLink.mavlink_adsb_vehicle_t
        {
            ICAO_address = icao,
            lat = (int)(lat * 1e7),
            lon = (int)(lng * 1e7),
            altitude = 1524_000,
            heading = 27050,
            hor_velocity = 5144,
            ver_velocity = -254,
            flags = flags,
            squawk = 0x1200,
            callsign = cs,
            emitter_type = emitter,
        };
    }

    [TestMethod]
    public void ContactsConvertUnits()
    {
        var store = new AdsbTrafficStore();
        store.Update(Plane(0x123), T0);
        var c = store.Snapshot(T0).Single();
        Assert.AreEqual(-35, c.Lat, 1e-7);
        Assert.AreEqual(149, c.Lng, 1e-7);
        Assert.AreEqual(1524, c.AltitudeM, 1e-9, "mm → m");
        Assert.AreEqual(270.5, c.HeadingDeg, 1e-9, "cdeg → deg");
        Assert.AreEqual(51.44, c.SpeedMs, 1e-9, "cm/s → m/s");
        Assert.AreEqual(-2.54, c.VerticalSpeedMs, 1e-9);
        Assert.AreEqual("ABC", c.Callsign);
        Assert.AreEqual("000123", c.IcaoText);
        Assert.AreEqual((ushort)0x1200, c.Squawk);
        Assert.IsFalse(c.IsStale);
    }

    [TestMethod]
    public void ContactsGoStaleAfterTenSecondsAndAreDroppedAfterThirty()
    {
        var store = new AdsbTrafficStore();
        store.Update(Plane(1, "OLD"), T0);
        store.Update(Plane(2, "NEW"), T0.AddSeconds(8));

        var at9 = store.Snapshot(T0.AddSeconds(9.9));
        Assert.HasCount(2, at9);
        Assert.IsTrue(at9.All(c => !c.IsStale));

        var at10 = store.Snapshot(T0.AddSeconds(10));
        Assert.IsTrue(at10.Single(c => c.Icao == 1).IsStale, "10 s without a message");
        Assert.IsFalse(at10.Single(c => c.Icao == 2).IsStale);
        Assert.AreEqual(2u, at10[0].Icao, "freshest first");

        // a new message refreshes it
        store.Update(Plane(1, "OLD", lat: -35.1), T0.AddSeconds(12));
        var at12 = store.Snapshot(T0.AddSeconds(12));
        Assert.IsFalse(at12.Single(c => c.Icao == 1).IsStale);
        Assert.AreEqual(-35.1, at12.Single(c => c.Icao == 1).Lat, 1e-7);

        var v = store.Version;
        var at39 = store.Snapshot(T0.AddSeconds(39));
        Assert.AreEqual(1u, at39.Single().Icao, "contact 2 (last heard at 8 s) is gone after 30 s");
        Assert.AreEqual(1, store.Count);
        Assert.IsGreaterThan(v, store.Version, "removal moves the version");
        Assert.IsEmpty(store.Snapshot(T0.AddSeconds(43)));
    }

    [TestMethod]
    public void ObstacleDatabaseAndPositionlessReportsAreNotTraffic()
    {
        var store = new AdsbTrafficStore();
        store.Update(Plane(5, "OA_DB", emitter: 255), T0);
        store.Update(Plane(6, "X", lat: 0, lng: 0, flags: 0), T0);
        Assert.AreEqual(0, store.Count);
        // OA_DB is only special with emitter 255
        store.Update(Plane(7, "OA_DB", emitter: 1), T0);
        Assert.AreEqual(1, store.Count);
    }

    [TestMethod]
    public void SquawkOnlyWhenFlaggedValid()
    {
        var store = new AdsbTrafficStore();
        store.Update(Plane(8, flags: (ushort)MAVLink.ADSB_FLAGS.VALID_COORDS), T0);
        Assert.AreEqual((ushort)0, store.Snapshot(T0).Single().Squawk);
    }

    [TestMethod]
    public void CollisionMessagesSetTheThreatLevel()
    {
        var store = new AdsbTrafficStore();
        store.Update(Plane(9), T0);
        var v = store.Version;
        store.SetThreat(9, MAVLink.MAV_COLLISION_THREAT_LEVEL.HIGH, T0);
        Assert.AreEqual(MAVLink.MAV_COLLISION_THREAT_LEVEL.HIGH, store.Snapshot(T0).Single().Threat);
        Assert.IsGreaterThan(v, store.Version);
        store.SetThreat(99, MAVLink.MAV_COLLISION_THREAT_LEVEL.HIGH, T0); // unknown aircraft: ignored
        Assert.AreEqual(1, store.Count);
    }

    [TestMethod]
    public void PacketsAreDecodedFromTheLink()
    {
        var parse = new MAVLink.MavlinkParse();
        var bytes = parse.GenerateMAVLinkPacket20(MAVLink.MAVLINK_MSG_ID.ADSB_VEHICLE, Plane(0xABCDEF, "QFA1"), false, 1, 1, 0);
        var store = new AdsbTrafficStore();
        store.OnPacket(new MAVLink.MAVLinkMessage(bytes), T0);
        var hb = parse.GenerateMAVLinkPacket20(MAVLink.MAVLINK_MSG_ID.HEARTBEAT, new MAVLink.mavlink_heartbeat_t(), false, 1, 1, 1);
        store.OnPacket(new MAVLink.MAVLinkMessage(hb), T0);
        var c = store.Snapshot(T0).Single();
        Assert.AreEqual("QFA1", c.Label);
        Assert.AreEqual("ABCDEF", c.IcaoText);
    }
}
