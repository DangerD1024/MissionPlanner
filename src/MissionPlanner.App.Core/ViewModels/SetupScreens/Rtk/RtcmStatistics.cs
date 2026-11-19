namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Rtk;

public sealed record RtcmMessageStat(int Type, string Description, long Count, long Bytes, TimeSpan Age);

public sealed record ConstellationStat(GnssSystem System, int Satellites, TimeSpan Age, bool Active);

public sealed record SatelliteStat(GnssSystem System, int Prn, double Snr);

/// <summary>A consistent copy of everything the RTCM stream showed so far.</summary>
public sealed record RtcmStatus(
    IReadOnlyList<RtcmMessageStat> Messages,
    IReadOnlyList<ConstellationStat> Constellations,
    IReadOnlyList<SatelliteStat> Satellites,
    RtcmBasePosition? Base,
    TimeSpan? BaseAge,
    bool BaseActive,
    TimeSpan? LastMessageAge,
    long Frames);

/// <summary>
/// Counts RTCM messages per type and keeps what the page shows: base position, satellites per
/// constellation and their signal strength. Thread-safe: the injector records on its worker,
/// the page reads snapshots on the UI thread. Like Mission Planner, observations count as live
/// for 5 s and the base position for 20 s.
/// </summary>
public sealed class RtcmStatistics
{
    public static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan BaseTimeout = TimeSpan.FromSeconds(20);

    private readonly object _gate = new();
    private readonly Dictionary<int, (long Count, long Bytes, DateTime Last)> _types = new();
    private readonly Dictionary<GnssSystem, (int Count, DateTime Last)> _systems = new();
    private readonly Dictionary<GnssSystem, Dictionary<int, (int Count, DateTime Last)>> _systemTypes = new();
    private readonly Dictionary<GnssSystem, Dictionary<int, double>> _snr = new();
    private RtcmBasePosition? _base;
    private DateTime _baseLast;
    private DateTime _lastBasePositionMessage;
    private DateTime _last;
    private long _frames;

    public void Clear()
    {
        lock (_gate)
        {
            _types.Clear();
            _systems.Clear();
            _systemTypes.Clear();
            _snr.Clear();
            _base = null;
            _baseLast = default;
            _lastBasePositionMessage = default;
            _last = default;
            _frames = 0;
        }
    }

    /// <summary>Records a frame; returns the base position when it carried one.</summary>
    public RtcmBasePosition? Record(RtcmFrame frame, DateTime now)
    {
        var type = frame.MessageType;
        var basePos = RtcmMessages.TryGetBasePosition(frame);
        var system = RtcmMessages.ObservationSystem(type);
        var sats = 0;
        if (RtcmMessages.MsmSystem(type) != null)
            sats = RtcmMessages.MsmSatellites(frame.Bytes).Count;
        else if (system != null)
            sats = RtcmMessages.LegacySatelliteCount(frame.Bytes, type);
        var snr = system != null ? RtcmMessages.TryGetSignalStrengths(frame) : Array.Empty<RtcmSatellite>();

        lock (_gate)
        {
            _frames++;
            _last = now;
            _types.TryGetValue(type, out var t);
            _types[type] = (t.Count + 1, t.Bytes + frame.Length, now);

            if (RtcmMessages.IsBasePosition(type))
                _lastBasePositionMessage = now;
            if (basePos != null)
            {
                _base = basePos;
                _baseLast = now;
            }

            if (system is { } sys)
            {
                // several message types (MSM4 + MSM7, L1 + L1/L2) may describe one constellation:
                // its count is the largest among the types still arriving
                if (!_systemTypes.TryGetValue(sys, out var perType))
                    _systemTypes[sys] = perType = new Dictionary<int, (int, DateTime)>();
                perType[type] = (sats, now);
                var count = perType.Values.Where(v => now - v.Last < ObservationTimeout).Select(v => v.Count).DefaultIfEmpty(sats).Max();
                _systems[sys] = (count, now);

                if (snr.Count > 0)
                {
                    var table = new Dictionary<int, double>();
                    foreach (var s in snr)
                        table[s.Prn] = s.Snr;
                    _snr[sys] = table;
                }
            }
        }

        return basePos;
    }

    public RtcmStatus Snapshot(DateTime now)
    {
        lock (_gate)
        {
            var messages = _types.OrderBy(kv => kv.Key)
                .Select(kv => new RtcmMessageStat(kv.Key, RtcmMessages.Describe(kv.Key), kv.Value.Count, kv.Value.Bytes, now - kv.Value.Last))
                .ToList();

            var systems = _systems.OrderBy(kv => kv.Key)
                .Select(kv => new ConstellationStat(kv.Key, kv.Value.Count, now - kv.Value.Last, now - kv.Value.Last < ObservationTimeout))
                .ToList();

            var sats = new List<SatelliteStat>();
            foreach (var (sys, table) in _snr.OrderBy(kv => kv.Key))
            {
                // signal bars of a constellation that stopped arriving are stale
                if (!_systems.TryGetValue(sys, out var seen) || now - seen.Last >= ObservationTimeout)
                    continue;
                sats.AddRange(table.OrderBy(kv => kv.Key).Select(kv => new SatelliteStat(sys, kv.Key, kv.Value)));
            }

            var baseSeen = _lastBasePositionMessage == default ? (DateTime?)null : _lastBasePositionMessage;
            return new RtcmStatus(
                messages,
                systems,
                sats,
                _base,
                _base != null ? now - _baseLast : null,
                baseSeen != null && now - baseSeen.Value < BaseTimeout,
                _frames > 0 ? now - _last : null,
                _frames);
        }
    }
}
