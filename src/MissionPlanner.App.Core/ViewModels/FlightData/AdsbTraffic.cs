using System.Globalization;
using System.Text;

namespace MissionPlanner.App.Core.ViewModels.FlightData;

/// <summary>
/// One aircraft as the map draws it (a snapshot; the store keeps updating its own copy).
/// Altitude in metres (barometric or GNSS, per the message), speeds in m/s (vertical: up positive).
/// </summary>
public sealed record AdsbContact(
    uint Icao,
    string Callsign,
    double Lat,
    double Lng,
    double AltitudeM,
    double HeadingDeg,
    double SpeedMs,
    double VerticalSpeedMs,
    ushort Squawk,
    byte EmitterType,
    MAVLink.MAV_COLLISION_THREAT_LEVEL Threat,
    TimeSpan Age,
    bool IsStale)
{
    public string IcaoText => Icao.ToString("X6", CultureInfo.InvariantCulture);

    /// <summary>Callsign, or the ICAO address when there is none.</summary>
    public string Label => Callsign.Length > 0 ? Callsign : IcaoText;
}

/// <summary>
/// ADS-B traffic from ADSB_VEHICLE (and threat levels from COLLISION), fed on the link's reader
/// thread and read by the UI. A contact not heard for <see cref="StaleAfter"/> is shown as stale;
/// after <see cref="RemoveAfter"/> it is dropped (Mission Planner drops them after 30 s).
/// </summary>
public sealed class AdsbTrafficStore
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan RemoveAfter = TimeSpan.FromSeconds(30);

    private sealed class Entry
    {
        public MAVLink.mavlink_adsb_vehicle_t Msg;
        public string Callsign = "";
        public MAVLink.MAV_COLLISION_THREAT_LEVEL Threat;
        public DateTime Seen;
    }

    private readonly object _gate = new();
    private readonly Dictionary<uint, Entry> _entries = new();
    private long _version;

    /// <summary>Moves on every update; the UI rebuilds its snapshot only when it changed (or ages cross a limit).</summary>
    public long Version => Interlocked.Read(ref _version);

    public int Count
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    /// <summary>Feeds a received packet; anything but ADSB_VEHICLE and COLLISION is ignored. Cheap: called for every packet.</summary>
    public void OnPacket(MAVLink.MAVLinkMessage msg, DateTime now)
    {
        if (msg.msgid == (uint)MAVLink.MAVLINK_MSG_ID.ADSB_VEHICLE && msg.data is MAVLink.mavlink_adsb_vehicle_t adsb)
            Update(adsb, now);
        else if (msg.msgid == (uint)MAVLink.MAVLINK_MSG_ID.COLLISION && msg.data is MAVLink.mavlink_collision_t coll)
            SetThreat(coll.id, (MAVLink.MAV_COLLISION_THREAT_LEVEL)coll.threat_level, now);
    }

    public void Update(MAVLink.mavlink_adsb_vehicle_t adsb, DateTime now)
    {
        var callsign = Callsign(adsb.callsign);
        // ArduPilot's object avoidance database goes out as emitter 255 "OA_DB": not traffic
        if (adsb.emitter_type == 255 && callsign == "OA_DB")
            return;
        if ((adsb.flags & (ushort)MAVLink.ADSB_FLAGS.VALID_COORDS) == 0 && adsb.lat == 0 && adsb.lon == 0)
            return;

        lock (_gate)
        {
            if (!_entries.TryGetValue(adsb.ICAO_address, out var e))
            {
                e = new Entry();
                _entries[adsb.ICAO_address] = e;
            }

            e.Msg = adsb;
            e.Callsign = callsign;
            e.Seen = now;
        }

        Interlocked.Increment(ref _version);
    }

    public void SetThreat(uint icao, MAVLink.MAV_COLLISION_THREAT_LEVEL threat, DateTime now)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(icao, out var e) || e.Threat == threat)
                return;
            e.Threat = threat;
        }

        Interlocked.Increment(ref _version);
    }

    public void Clear()
    {
        lock (_gate)
            _entries.Clear();
        Interlocked.Increment(ref _version);
    }

    /// <summary>Contacts heard within <see cref="RemoveAfter"/>, nearest-in-time first; older ones are dropped.</summary>
    public IReadOnlyList<AdsbContact> Snapshot(DateTime now)
    {
        var list = new List<AdsbContact>();
        var removed = false;
        lock (_gate)
        {
            List<uint>? drop = null;
            foreach (var (icao, e) in _entries)
            {
                var age = now - e.Seen;
                if (age > RemoveAfter)
                {
                    (drop ??= new()).Add(icao);
                    continue;
                }

                list.Add(ToContact(e, age < TimeSpan.Zero ? TimeSpan.Zero : age));
            }

            if (drop != null)
            {
                foreach (var icao in drop)
                    _entries.Remove(icao);
                removed = true;
            }
        }

        if (removed)
            Interlocked.Increment(ref _version);
        list.Sort((a, b) => a.Age.CompareTo(b.Age));
        return list;
    }

    private static AdsbContact ToContact(Entry e, TimeSpan age)
    {
        var m = e.Msg;
        var flags = (MAVLink.ADSB_FLAGS)m.flags;
        return new AdsbContact(
            m.ICAO_address,
            e.Callsign,
            m.lat / 1e7,
            m.lon / 1e7,
            m.altitude / 1000.0,
            m.heading / 100.0,
            m.hor_velocity / 100.0,
            m.ver_velocity / 100.0,
            flags.HasFlag(MAVLink.ADSB_FLAGS.VALID_SQUAWK) ? m.squawk : (ushort)0,
            m.emitter_type,
            e.Threat,
            age,
            age >= StaleAfter);
    }

    internal static string Callsign(byte[]? raw) =>
        raw == null ? "" : Encoding.ASCII.GetString(raw).Split('\0')[0].Trim();
}
