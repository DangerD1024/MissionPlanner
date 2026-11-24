using MissionPlanner.Utilities;
using Newtonsoft.Json;

namespace MissionPlanner.App.Core.Swarm;

/// <summary>
/// A show for Mission Planner's Sequence Swarm (<c>Swarm.Sequence.LayoutEditor</c>): named layouts, each an
/// offset per system id, and the steps, the layout names in the order they are flown. Saved as the same
/// JSON as Mission Planner's Save, so its files load here and the other way round.
/// </summary>
public sealed class SwarmSequence
{
    public List<SwarmLayout> Layouts { get; set; } = new();

    public List<string> Steps { get; set; } = new();

    public static SwarmSequence Load(string path) =>
        JsonConvert.DeserializeObject<SwarmSequence>(File.ReadAllText(path)) ?? new SwarmSequence();

    public void Save(string path) => File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented));

    public SwarmLayout? Find(string id) => Layouts.FirstOrDefault(l => l.Id == id);
}

/// <summary>One formation of a sequence: east/north/altitude in metres per system id, from the start point.</summary>
public sealed class SwarmLayout
{
    public string Id { get; set; } = "";

    public int DelayStart { get; set; }

    public int DelayEnd { get; set; }

    // a field, as Mission Planner's Layout has it, so the JSON matches
    public Dictionary<uint, Vector3> Offset = new();

    public void AddOffset(uint sysid, Vector3 offset) => Offset[sysid] = offset;

    public override string ToString() => Id;
}

/// <summary>
/// Runs a <see cref="SwarmSequence"/>: each step sends every vehicle (matched by system id) to its offset
/// from the start point, the position of the first vehicle when the first step ran.
/// </summary>
public sealed class SequenceSwarm
{
    private bool _streamsRequested;

    public SwarmSequence Sequence { get; private set; } = new();

    /// <summary>The next step to run.</summary>
    public int Step { get; set; }

    /// <summary>Where the layouts are measured from; set by the first step.</summary>
    public PointLatLngAlt? StartPosition { get; set; }

    /// <summary>Takeoff altitude of the group takeoff (Mission Planner: 2 m).</summary>
    public float TakeoffAlt { get; set; } = 2;

    /// <summary>The system ids in the layouts (from the first one).</summary>
    public IReadOnlyList<uint> Drones => Sequence.Layouts.Count > 0
        ? Sequence.Layouts[0].Offset.Keys.OrderBy(k => k).ToList()
        : [];

    public void Load(SwarmSequence sequence)
    {
        Sequence = sequence;
        Step = 0;
    }

    /// <summary>
    /// A new layout, a copy of <paramref name="from"/> (or <paramref name="drones"/> vehicles in a line along
    /// east when there is none).
    /// </summary>
    public SwarmLayout NewLayout(string name, SwarmLayout? from, int drones)
    {
        var layout = new SwarmLayout { Id = name };
        if (from != null)
        {
            foreach (var item in from.Offset)
                layout.AddOffset(item.Key, new Vector3(item.Value.x, item.Value.y, item.Value.z));
        }
        else
        {
            var ids = Drones.Count > 0 ? Drones : Enumerable.Range(1, Math.Max(1, drones)).Select(i => (uint)i).ToList();
            foreach (var id in ids)
                layout.AddOffset(id, new Vector3(id, 0, 0));
        }

        Sequence.Layouts.Add(layout);
        return layout;
    }

    /// <summary>
    /// Adds or removes vehicles in every layout: a new one takes the next system id and sits at x = its id;
    /// removing drops the highest id.
    /// </summary>
    public void SetDroneCount(int count)
    {
        count = Math.Max(1, count);
        if (Sequence.Layouts.Count == 0)
            return;

        while (Drones.Count < count)
        {
            var sysid = Drones.Count > 0 ? Drones.Max() + 1 : 1u;
            foreach (var layout in Sequence.Layouts)
                layout.AddOffset(sysid, new Vector3(sysid, 0, 0));
        }

        while (Drones.Count > count)
        {
            var sysid = Drones.Max();
            foreach (var layout in Sequence.Layouts)
                layout.Offset.Remove(sysid);
        }
    }

    public void AddStep(string layoutId) => Sequence.Steps.Add(layoutId);

    public void RemoveStep(int index)
    {
        if (index >= 0 && index < Sequence.Steps.Count)
            Sequence.Steps.RemoveAt(index);
    }

    /// <summary>Moves a step up (-1) or down (+1); returns its new index.</summary>
    public int MoveStep(int index, int by)
    {
        var to = index + by;
        if (index < 0 || index >= Sequence.Steps.Count || to < 0 || to >= Sequence.Steps.Count)
            return index;
        var s = Sequence.Steps[index];
        Sequence.Steps.RemoveAt(index);
        Sequence.Steps.Insert(to, s);
        return to;
    }

    private void RequestStreams(IReadOnlyList<ISwarmVehicle> vehicles)
    {
        if (_streamsRequested)
            return;
        _streamsRequested = true;
        foreach (var v in vehicles)
            v.RequestPositionRate(10);
    }

    /// <summary>
    /// Sends the vehicles to the layout of the next step and moves on. Returns the layout flown, or null at
    /// the end of the steps (or when the step names a missing layout).
    /// </summary>
    public SwarmLayout? RunStep(IReadOnlyList<ISwarmVehicle> vehicles)
    {
        if (Step >= Sequence.Steps.Count || vehicles.Count == 0)
            return null;

        RequestStreams(vehicles);

        StartPosition ??= vehicles.First().Position();

        var layout = Sequence.Find(Sequence.Steps[Step]);
        Step++;
        if (layout == null)
            return null;

        foreach (var (sysid, offset) in layout.Offset)
        {
            var drone = vehicles.FirstOrDefault(v => v.SysId == sysid);
            if (drone == null)
                continue;
            drone.SendPositionVelocity(TargetFor(StartPosition, offset), Vector3.Zero);
        }

        return layout;
    }

    /// <summary>The point of an offset (east, north, altitude above home) from the start point.</summary>
    public static PointLatLngAlt TargetFor(PointLatLngAlt start, Vector3 offset)
    {
        var p = start.gps_offset(offset.x, offset.y);
        p.Alt = offset.z;
        return p;
    }

    /// <summary>Guided, armed and a takeoff for every vehicle (Mission Planner's Takeoff).</summary>
    public void Takeoff(IReadOnlyList<ISwarmVehicle> vehicles)
    {
        _streamsRequested = false;
        RequestStreams(vehicles);

        Parallel.ForEach(vehicles, v =>
        {
            if (!v.Mode.Equals("guided", StringComparison.OrdinalIgnoreCase))
                v.SetMode("GUIDED");
        });

        foreach (var v in vehicles)
        {
            if (!v.Armed)
                v.Arm(true);
        }

        Parallel.ForEach(vehicles, v => v.Command(MAVLink.MAV_CMD.TAKEOFF, 0, 0, 0, 0, 0, 0, TakeoffAlt, false));
    }
}
