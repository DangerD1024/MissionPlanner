using MissionPlanner.App.Core.Swarm;
using MissionPlanner.App.Core.ViewModels.Tools;
using MissionPlanner.App.Tests.FlightData;
using MissionPlanner.Comms;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Tests.Tools;

/// <summary>A swarm vehicle with settable telemetry that records the calls made to it.</summary>
internal sealed class FakeSwarmVehicle(byte sysid, byte compid = 1) : ISwarmVehicle
{
    public List<string> Calls { get; } = new();

    public List<(double Lat, double Lng, double Alt, double Vx, double Vy, double Vz)> Targets { get; } = new();

    public List<MAVLink.mavlink_set_attitude_target_t> Attitudes { get; } = new();

    public Dictionary<string, double> Params { get; } = new();

    public List<Locationwp> Mission { get; set; } = new();

    public byte SysId { get; } = sysid;
    public byte CompId { get; } = compid;
    public string Name => $"MAV {SysId}-{CompId}";
    public double Lat { get; set; }
    public double Lng { get; set; }
    public double Alt { get; set; }
    public double Yaw { get; set; }
    public double Vx { get; set; }
    public double Vy { get; set; }
    public double Vz { get; set; }
    public double GroundSpeed { get; set; }
    public double TurnRadius { get; set; }
    public bool Armed { get; set; }
    public string Mode { get; set; } = "STABILIZE";
    public int GpsFix { get; set; } = 3;
    public int GpsFix2 { get; set; }
    public bool IsPlane { get; set; }
    public PointLatLngAlt Home { get; set; } = PointLatLngAlt.Zero;
    public PointLatLngAlt? GuidedTarget { get; set; }

    /// <summary>Arming succeeds and sets <see cref="Armed"/>.</summary>
    public bool ArmWorks { get; set; } = true;

    public FakeSwarmVehicle At(PointLatLngAlt p)
    {
        Lat = p.Lat;
        Lng = p.Lng;
        Alt = p.Alt;
        return this;
    }

    public double? GetParam(string name) => Params.TryGetValue(name, out var v) ? v : null;

    public bool Arm(bool arm)
    {
        Calls.Add(arm ? "arm" : "disarm");
        if (ArmWorks)
            Armed = arm;
        return ArmWorks;
    }

    public void SetMode(string mode)
    {
        Calls.Add("mode " + mode);
        Mode = mode;
    }

    public bool Command(MAVLink.MAV_CMD cmd, float p1, float p2, float p3, float p4, float p5, float p6, float p7, bool requireAck = true)
    {
        Calls.Add(cmd == MAVLink.MAV_CMD.TAKEOFF ? $"takeoff {p7}" : cmd == MAVLink.MAV_CMD.CONDITION_YAW ? $"yaw {p1}" : cmd.ToString());
        return true;
    }

    public void SetPositionTarget(bool pos, bool vel, bool yaw, double lat, double lng, double alt, double vx, double vy, double vz, double yawRad, double yawRate)
    {
        Calls.Add("target");
        Targets.Add((lat, lng, alt, vx, vy, vz));
        GuidedTarget = new PointLatLngAlt(lat, lng, alt);
    }

    public void GuidedWaypoint(double lat, double lng, double alt)
    {
        Calls.Add("goto");
        Targets.Add((lat, lng, alt, 0, 0, 0));
    }

    public void MountControl(double pitch, double roll, double yaw) => Calls.Add($"mount {yaw}");

    public void RequestPositionRate(int hz, bool attitude = false) => Calls.Add($"rate {hz}");

    public bool SetParam(string name, double value)
    {
        Calls.Add($"param {name} {value}");
        Params[name] = value;
        return true;
    }

    public void SendAttitudeTarget(MAVLink.mavlink_set_attitude_target_t target)
    {
        Calls.Add("attitude");
        Attitudes.Add(target);
    }

    public IReadOnlyList<Locationwp> DownloadMission() => Mission;
}

internal sealed class FakeFleet(params ISwarmVehicle[] vehicles) : ISwarmFleet
{
    public List<ISwarmVehicle> List { get; } = vehicles.ToList();

    public IReadOnlyList<ISwarmVehicle> Vehicles => List.ToList();
}

/// <summary>The swarm tools (Mission Planner's Ctrl+F Swarm, Follow the leader, Sequence Swarm, Follow Swarm).</summary>
[TestClass]
public class SwarmTests
{
    private static readonly PointLatLngAlt Origin = new(-35.363261, 149.165230, 10);

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var key in new[] { SwarmFollowLeaderViewModel.DistanceKey, SwarmSequenceViewModel.FileKey })
            Settings.Instance.Remove(key);
        foreach (var k in new[] { "separation", "lead", "offpath", "altsep", "accel", "v", "interleave" })
            Settings.Instance.Remove(SwarmFollowSwarmViewModel.KeyPrefix + k);
        for (var i = 1; i < 6; i++)
            Settings.Instance.Remove($"{SwarmFormationViewModel.OffsetKeyPrefix}{i}_1");
    }

    // ---- formation maths ----------------------------------------------------------------------

    [TestMethod]
    public void OffsetsAreRightAheadUp_TurnedWithTheLeader()
    {
        // heading north: x is east, y is north
        var t = SwarmGeometry.OffsetTarget(Origin, 0, new Vector3(10, 0, 5));
        Assert.AreEqual(10, Origin.GetDistance(t), 0.05);
        Assert.AreEqual(90, Origin.GetBearing(t), 0.5);
        Assert.AreEqual(15, t.Alt, 1e-9);

        // heading east: right is south, ahead is east
        t = SwarmGeometry.OffsetTarget(Origin, 90, new Vector3(10, 0, 0));
        Assert.AreEqual(180, Origin.GetBearing(t), 0.5);
        t = SwarmGeometry.OffsetTarget(Origin, 90, new Vector3(0, -20, 0));
        Assert.AreEqual(270, Origin.GetBearing(t), 0.5);
        Assert.AreEqual(20, Origin.GetDistance(t), 0.05);
    }

    [TestMethod]
    public void OffsetFromLeaderUndoesTheTarget()
    {
        foreach (var yaw in new[] { 0.0, 37, 90, 181, 300 })
        {
            var offset = new Vector3(-12.5, 30, 0);
            var target = SwarmGeometry.OffsetTarget(Origin, yaw, offset);
            var back = SwarmGeometry.OffsetFromLeader(Origin, yaw, target);
            Assert.AreEqual(offset.x, back.x, 0.02, $"yaw {yaw}");
            Assert.AreEqual(offset.y, back.y, 0.02, $"yaw {yaw}");
        }
    }

    // ---- Swarm (formation) --------------------------------------------------------------------

    [TestMethod]
    public void SetLeaderKeepsTheShape_AndTheLoopSendsFollowersTheirPlaces()
    {
        var a = new FakeSwarmVehicle(1).At(Origin);
        var b = new FakeSwarmVehicle(2).At(Origin);
        var c = new FakeSwarmVehicle(3).At(Origin);
        var f = new FormationSwarm();
        f.SetOffset(a, 0, 0, 0);
        f.SetOffset(b, 5, 0, 0);
        f.SetOffset(c, 10, -5, 2);

        f.SetLeader([a, b, c], b);
        Assert.AreSame(b, f.Leader);
        Assert.AreEqual(-5, f.GetOffset(a).x);
        Assert.AreEqual(0, f.GetOffset(b).x);
        Assert.AreEqual(5, f.GetOffset(c).x);
        Assert.AreEqual(-5, f.GetOffset(c).y);
        Assert.AreEqual(2, f.GetOffset(c).z);

        b.Yaw = 90;
        b.Vx = 1;
        b.Vy = 2;
        a.Yaw = 90;
        c.Yaw = 10;
        f.Begin();
        f.Step([a, b, c]);

        CollectionAssert.Contains(b.Calls, "rate 10");
        Assert.IsEmpty(b.Targets, "the leader is flown by the pilot");
        Assert.HasCount(1, a.Targets);
        var expected = SwarmGeometry.OffsetTarget(Origin, 90, new Vector3(-5, 0, 0));
        Assert.AreEqual(expected.Lat, a.Targets[0].Lat, 1e-9);
        Assert.AreEqual(expected.Lng, a.Targets[0].Lng, 1e-9);
        Assert.AreEqual(1, a.Targets[0].Vx);
        Assert.AreEqual(2, a.Targets[0].Vy);
        Assert.DoesNotContain("yaw 90", a.Calls, "within 3 degrees of the leader: no yaw");
        CollectionAssert.Contains(c.Calls, "yaw 90");
        Assert.AreEqual(12, c.Targets[0].Alt, 1e-9);

        f.Gimbal = true;
        f.Step([a, b, c]);
        CollectionAssert.Contains(c.Calls, "mount 90");
    }

    [TestMethod]
    public void NewVehiclesLineUpAndUpdatePositionTakesWhereTheyAre()
    {
        var lead = new FakeSwarmVehicle(1).At(Origin);
        var near = new FakeSwarmVehicle(2).At(Origin.gps_offset(0, 15));
        var far = new FakeSwarmVehicle(3).At(Origin.gps_offset(500, 0));
        var f = new FormationSwarm();
        Assert.AreEqual(0, f.GetOffset(near).x);
        f.SetOffset(near, 1, 1, 1);
        Assert.AreEqual(1, f.GetOffset(far).x, "a vehicle without an offset is placed at x = number of offsets");

        f.SetLeader([lead, near, far], lead);
        Assert.AreEqual(1, f.CaptureOffsets([lead, near, far]), "the one 500 m off keeps its offset");
        Assert.AreEqual(0, f.GetOffset(near).x, 0.05);
        Assert.AreEqual(15, f.GetOffset(near).y, 0.05);
    }

    [TestMethod]
    public void GroupCommandsSkipTheLeaderButLandEveryone()
    {
        var lead = new FakeSwarmVehicle(1);
        var other = new FakeSwarmVehicle(2);
        var f = new FormationSwarm { Leader = lead };
        f.Arm([lead, other]);
        f.Takeoff([lead, other]);
        f.Auto([lead, other]);
        f.Land([lead, other]);
        CollectionAssert.AreEqual(new[] { "mode Land" }, lead.Calls);
        CollectionAssert.AreEqual(new[] { "arm", "mode GUIDED", "takeoff 5", "mode AUTO", "mode Land" }, other.Calls);
    }

    [TestMethod]
    public void APlaneInFormationGetsAttitudeTargets()
    {
        var lead = new FakeSwarmVehicle(1) { IsPlane = true, Yaw = 0, GroundSpeed = 20 }.At(Origin);
        var plane = new FakeSwarmVehicle(2) { IsPlane = true, Yaw = 0, GroundSpeed = 20 }.At(Origin.gps_offset(0, -60));
        var f = new FormationSwarm();
        f.SetLeader([lead, plane], lead);
        f.SetOffset(plane, 20, -20, 0);
        f.Step([lead, plane]);

        Assert.IsEmpty(plane.Targets);
        Assert.HasCount(1, plane.Attitudes);
        var att = plane.Attitudes[0];
        Assert.AreEqual(2, att.target_system);
        Assert.AreEqual(0b00111000, att.type_mask, "roll, pitch, yaw angle and thrust used");
        Assert.IsTrue(att.thrust is >= 0.1f and <= 1f);
        Assert.HasCount(4, att.q);
    }

    // ---- Follow the leader --------------------------------------------------------------------

    [TestMethod]
    public void FollowersTakePlacesBehindTheLeaderAlongItsTrack()
    {
        var lead = new FakeSwarmVehicle(1);
        var f1 = new FakeSwarmVehicle(2);
        var f2 = new FakeSwarmVehicle(3);
        var follow = new FollowPathSwarm { Leader = lead, FollowDistance = 2 };

        // the leader flies 20 m north in 0.5 m steps
        for (var i = 0; i <= 40; i++)
        {
            lead.At(Origin.gps_offset(0, i * 0.5));
            follow.Step([lead, f1, f2]);
        }

        Assert.AreEqual(41, follow.TrailCount);
        var places = follow.PlanMove();
        Assert.IsGreaterThanOrEqualTo(6, places.Count);
        var here = lead.Position();
        Assert.AreEqual(2.5, here.GetDistance(places[0]), 0.05, "the first point more than 2 m back");
        Assert.AreEqual(5, here.GetDistance(places[1]), 0.05);

        Assert.IsEmpty(lead.Targets);
        var last1 = f1.Targets[^1];
        var last2 = f2.Targets[^1];
        Assert.AreEqual(places[0].Lat, last1.Lat, 1e-9);
        Assert.AreEqual(places[1].Lat, last2.Lat, 1e-9, "each follower its own place");

        // standing still adds nothing
        follow.Step([lead, f1, f2]);
        Assert.AreEqual(41, follow.TrailCount);
    }

    // ---- Sequence Swarm -----------------------------------------------------------------------

    [TestMethod]
    public void SequenceFilesLoadInMissionPlannersFormat()
    {
        // what Mission Planner's LayoutEditor writes (Newtonsoft over its Sequence/Layout classes)
        const string json = """
            {
              "Layouts": [
                { "Offset": { "1": { "x": 1.0, "y": 2.0, "z": 3.0, "xd": 1.0, "yd": 2.0, "zd": 3.0, "X": 1.0, "Y": 2.0, "Z": 3.0 },
                              "2": { "x": 4.0, "y": 0.0, "z": 5.0 } },
                  "Id": "Line", "DelayStart": 0, "DelayEnd": 0 },
                { "Offset": { "1": { "x": 0.0, "y": 0.0, "z": 10.0 }, "2": { "x": 0.0, "y": 10.0, "z": 10.0 } },
                  "Id": "Column", "DelayStart": 0, "DelayEnd": 0 }
              ],
              "Steps": [ "Line", "Column" ]
            }
            """;
        var path = Path.Combine(Path.GetTempPath(), "mp-seq-" + Guid.NewGuid() + ".txt");
        try
        {
            File.WriteAllText(path, json);
            var seq = SwarmSequence.Load(path);
            Assert.HasCount(2, seq.Layouts);
            Assert.AreEqual(3, seq.Find("Line")!.Offset[1].z);
            Assert.AreEqual(4, seq.Find("Line")!.Offset[2].x);
            CollectionAssert.AreEqual(new[] { "Line", "Column" }, seq.Steps);

            seq.Save(path);
            var again = SwarmSequence.Load(path);
            Assert.AreEqual(10, again.Find("Column")!.Offset[2].y);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void LayoutsCopyAndTheDroneCountChangesEveryLayout()
    {
        var s = new SequenceSwarm();
        var first = s.NewLayout("A", null, 3);
        CollectionAssert.AreEqual(new uint[] { 1, 2, 3 }, s.Drones.ToList());
        Assert.AreEqual(2, first.Offset[2].x, "a new vehicle sits at x = its id");

        first.AddOffset(1, new Vector3(7, 8, 9));
        var second = s.NewLayout("B", first, 3);
        Assert.AreEqual(7, second.Offset[1].x);
        second.AddOffset(1, new Vector3(0, 0, 0));
        Assert.AreEqual(7, first.Offset[1].x, "the copy does not share offsets");

        s.SetDroneCount(5);
        Assert.HasCount(5, first.Offset);
        Assert.HasCount(5, second.Offset);
        s.SetDroneCount(2);
        CollectionAssert.AreEqual(new uint[] { 1, 2 }, second.Offset.Keys.OrderBy(k => k).ToList());

        s.AddStep("A");
        s.AddStep("B");
        s.AddStep("A");
        Assert.AreEqual(0, s.MoveStep(1, -1));
        CollectionAssert.AreEqual(new[] { "B", "A", "A" }, s.Sequence.Steps);
        s.RemoveStep(2);
        CollectionAssert.AreEqual(new[] { "B", "A" }, s.Sequence.Steps);
    }

    [TestMethod]
    public void StepsSendEachVehicleToItsOffsetFromTheStartPoint()
    {
        var one = new FakeSwarmVehicle(1).At(Origin);
        var two = new FakeSwarmVehicle(2).At(Origin.gps_offset(3, 3));
        var s = new SequenceSwarm();
        var a = s.NewLayout("A", null, 2);
        a.AddOffset(1, new Vector3(0, 10, 5));
        a.AddOffset(2, new Vector3(10, 0, 8));
        s.AddStep("A");
        s.AddStep("missing");

        Assert.AreSame(a, s.RunStep([one, two]));
        CollectionAssert.Contains(one.Calls, "rate 10");
        var t1 = new PointLatLngAlt(one.Targets[0].Lat, one.Targets[0].Lng);
        Assert.AreEqual(10, Origin.GetDistance(t1), 0.05);
        Assert.AreEqual(0, Origin.GetBearing(t1), 0.5);
        Assert.AreEqual(5, one.Targets[0].Alt);
        var t2 = new PointLatLngAlt(two.Targets[0].Lat, two.Targets[0].Lng);
        Assert.AreEqual(90, Origin.GetBearing(t2), 0.5, "measured from the first vehicle, not from each one");
        Assert.AreEqual(8, two.Targets[0].Alt);
        Assert.AreEqual(0, two.Targets[0].Vx);

        Assert.IsNull(s.RunStep([one, two]), "a step naming a missing layout sends nothing");
        Assert.AreEqual(2, s.Step);
        Assert.IsNull(s.RunStep([one, two]), "past the last step");
        Assert.HasCount(1, one.Targets);
    }

    [TestMethod]
    public void SequenceTakeoffIsGuidedArmAndClimbToTwoMetres()
    {
        var one = new FakeSwarmVehicle(1);
        var two = new FakeSwarmVehicle(2) { Mode = "GUIDED", Armed = true };
        new SequenceSwarm().Takeoff([one, two]);
        CollectionAssert.AreEqual(new[] { "rate 10", "mode GUIDED", "arm", "takeoff 2" }, one.Calls);
        CollectionAssert.AreEqual(new[] { "rate 10", "takeoff 2" }, two.Calls);
    }

    // ---- Follow Swarm -------------------------------------------------------------------------

    private static List<Locationwp> NorthMission(double length = 100, float alt = 10) =>
    [
        new() { id = (ushort)MAVLink.MAV_CMD.WAYPOINT, lat = Origin.Lat, lng = Origin.Lng, alt = 0 },
        new() { id = (ushort)MAVLink.MAV_CMD.TAKEOFF, lat = 0, lng = 0, alt = alt },
        new() { id = (ushort)MAVLink.MAV_CMD.WAYPOINT, lat = Origin.gps_offset(0, 1).Lat, lng = Origin.Lng, alt = alt },
        new() { id = (ushort)MAVLink.MAV_CMD.WAYPOINT, lat = Origin.gps_offset(0, length).Lat, lng = Origin.Lng, alt = alt },
    ];

    [TestMethod]
    public void ThePathIsTheMissionInTenCentimetreSteps()
    {
        var path = WaypointLeaderSwarm.GeneratePath(NorthMission());
        Assert.AreEqual(1000, path.Count, 3);
        Assert.AreEqual(10, path[^1].Alt, 0.01);
        Assert.AreEqual(10, path[0].Alt, 0.01, "the first leg from home climbs from the waypoint height");
        Assert.AreEqual(0.1, path[0].GetDistance(path[1]), 0.005);

        // lead 20 m, then every 5 m back
        var at = Origin.gps_offset(0, 50);
        var places = WaypointLeaderSwarm.GetLocations(path, at, 20, 5);
        Assert.IsGreaterThanOrEqualTo(5, places.Count);
        Assert.AreEqual(20, at.GetDistance(places[0]), 0.2);
        Assert.AreEqual(15, at.GetDistance(places[1]), 0.2);
        Assert.AreEqual(10, at.GetDistance(places[2]), 0.2);
        Assert.IsGreaterThan(at.Lat, places[0].Lat, "ahead along the path");

        // a V: the front, then pairs either side
        var v = WaypointLeaderSwarm.GetLocationsV(path, at, 20, 5);
        Assert.AreEqual(1 + 2 * (places.Count - 1), v.Count);

        var wps = NorthMission().Where(w => w.lat != 0).Select(w => new PointLatLngAlt(w)).ToList();
        Assert.AreEqual(7, WaypointLeaderSwarm.GetOffPathDistance(wps, Origin.gps_offset(7, 50)), 0.1);
    }

    [TestMethod]
    public void IdleSetsUpTheDronesThenTakeoffArmsAndClimbsThem()
    {
        var ground = new FakeSwarmVehicle(1).At(Origin.gps_offset(0, 30));
        var air = new FakeSwarmVehicle(2) { Home = new PointLatLngAlt(Origin) }.At(new PointLatLngAlt(Origin.Lat, Origin.Lng, 0));
        var third = new FakeSwarmVehicle(3).At(new PointLatLngAlt(Origin.gps_offset(2, 0).Lat, Origin.gps_offset(2, 0).Lng, 0));
        air.Params["RTL_ALT"] = 1500;
        air.Params["WPNAV_ACCEL"] = 250;
        third.Params["RTL_ALT_M"] = 15;
        third.Params["WP_ACC"] = 3;
        ground.Params["RTL_ALT"] = 1500;

        var g = new WaypointLeaderSwarm { GroundMaster = ground, AirMaster = air, Sleep = _ => { } };
        g.SetDrones([ground, air, third]);
        g.SetMission(NorthMission());
        Assert.IsTrue(g.IsReady);

        g.Update();
        Assert.AreEqual(WaypointLeaderSwarm.Mode.takeoff, g.CurrentMode);
        CollectionAssert.Contains(air.Calls, "param RTL_ALT 0");
        CollectionAssert.Contains(air.Calls, "param WPNAV_ACCEL 100");
        CollectionAssert.Contains(third.Calls, "param RTL_ALT_M 0");
        CollectionAssert.Contains(third.Calls, "param WP_ACC 1");
        CollectionAssert.AreEqual(new[] { "rate 5" }, ground.Calls, "the ground master is only listened to");

        g.Update();
        CollectionAssert.IsSubsetOf(new[] { "mode GUIDED", "arm" }, air.Calls);
        Assert.IsTrue(air.Calls.Any(c => c.StartsWith("takeoff ")));
        Assert.IsTrue(third.Calls.Any(c => c.StartsWith("takeoff ")));
        Assert.IsFalse(ground.Calls.Any(c => c.StartsWith("takeoff ") || c == "arm"));
        var airAlt = float.Parse(air.Calls.First(c => c.StartsWith("takeoff ")).Split(' ')[1]);
        var thirdAlt = float.Parse(third.Calls.First(c => c.StartsWith("takeoff ")).Split(' ')[1]);
        Assert.AreEqual(2, thirdAlt - airAlt, 0.01, "heights staggered by the altitude separation");
        Assert.AreEqual(WaypointLeaderSwarm.Mode.takeoff, g.CurrentMode, "still climbing");
    }

    [TestMethod]
    public void TwoDronesAtTheSameHeightTooCloseSeparate()
    {
        var ground = new FakeSwarmVehicle(1).At(Origin);
        var air = new FakeSwarmVehicle(2) { Armed = true }.At(Origin.gps_offset(0, 40));
        var third = new FakeSwarmVehicle(3) { Armed = true }.At(Origin.gps_offset(1, 40));
        third.Alt += 0.5;
        var g = new WaypointLeaderSwarm { GroundMaster = ground, AirMaster = air, Separation = 5, CurrentMode = WaypointLeaderSwarm.Mode.followuser };
        g.SetDrones([ground, air, third]);
        g.SetMission(NorthMission());

        g.Update();
        Assert.IsEmpty(air.Targets);
        Assert.HasCount(1, third.Targets, "the higher one climbs, nothing else is sent this pass");
        Assert.AreEqual(third.Alt + 2, third.Targets[0].Alt, 1e-6);
    }

    [TestMethod]
    public void FollowingPutsTheLineAheadOfTheGroundMaster()
    {
        var ground = new FakeSwarmVehicle(1) { Vx = 3 }.At(Origin.gps_offset(0, 30));
        var air = new FakeSwarmVehicle(2) { Armed = true }.At(Origin.gps_offset(0, 50));
        var third = new FakeSwarmVehicle(3) { Armed = true }.At(Origin.gps_offset(0, 45));
        var g = new WaypointLeaderSwarm { GroundMaster = ground, AirMaster = air, Separation = 5, Lead = 20, CurrentMode = WaypointLeaderSwarm.Mode.followuser };
        g.SetDrones([ground, air, third]);
        g.SetMission(NorthMission());

        g.Update();
        var gp = ground.Position();
        Assert.AreEqual(20, gp.GetDistance(new PointLatLngAlt(air.Targets[0].Lat, air.Targets[0].Lng)), 0.2);
        Assert.AreEqual(15, gp.GetDistance(new PointLatLngAlt(third.Targets[0].Lat, third.Targets[0].Lng)), 0.2);
        Assert.AreEqual(1, air.Targets[0].Vx, 1e-9, "a third of the ground master's velocity");
        Assert.IsEmpty(ground.Targets);
        Assert.AreEqual(WaypointLeaderSwarm.Mode.followuser, g.CurrentMode);

        // the user walks off the path: home
        ground.At(Origin.gps_offset(30, 30));
        g.Update();
        Assert.AreEqual(WaypointLeaderSwarm.Mode.RTH, g.CurrentMode);
    }

    // ---- pages --------------------------------------------------------------------------------

    [TestMethod]
    public void FormationPageListsTheVehicles_SetsTheLeader_AndKeepsEditedOffsets()
    {
        var a = new FakeSwarmVehicle(1).At(Origin);
        var b = new FakeSwarmVehicle(2) { Armed = true, Mode = "GUIDED" }.At(Origin);
        var fleet = new FakeFleet(a, b);
        var vm = new SwarmFormationViewModel(FakeUi.Services(out _), fleet);
        vm.OnShown();

        Assert.HasCount(2, vm.Vehicles);
        Assert.IsTrue(vm.ShowOffsets);
        Assert.AreEqual("Armed", vm.Vehicles[1].Armed);
        Assert.AreEqual("GUIDED", vm.Vehicles[1].Mode);
        Assert.AreEqual("OK 3", vm.Vehicles[1].Gps);

        vm.Selected = vm.Vehicles[0];
        vm.SetLeaderCommand.Execute(null);
        Assert.IsTrue(vm.HasLeader);
        Assert.AreEqual("Leader", vm.Vehicles[0].Role);
        Assert.IsFalse(vm.Vehicles[0].CanEditOffset);

        vm.Vehicles[1].OffsetX = 4;
        vm.Vehicles[1].OffsetY = -6;
        Assert.AreEqual(4, vm.Formation.GetOffset(b).x);
        Assert.AreEqual(-6, vm.Formation.GetOffset(b).y);
        Assert.AreEqual("4,-6,0", Settings.Instance[$"{SwarmFormationViewModel.OffsetKeyPrefix}2_1"]);

        // another page (next start) picks the offset up again
        var again = new SwarmFormationViewModel(FakeUi.Services(out _), new FakeFleet(a, b));
        again.OnShown();
        Assert.AreEqual(4, again.Vehicles[1].OffsetX);

        // a vehicle leaving the link leaves the list
        fleet.List.Remove(b);
        vm.RefreshVehicles();
        Assert.HasCount(1, vm.Vehicles);
    }

    [TestMethod]
    public async Task FormationLoopRunsUntilStopped()
    {
        var a = new FakeSwarmVehicle(1).At(Origin);
        var b = new FakeSwarmVehicle(2).At(Origin);
        var vm = new SwarmFormationViewModel(FakeUi.Services(out var ui), new FakeFleet(a, b));
        vm.OnShown();
        vm.Selected = vm.Vehicles[0];
        vm.SetLeaderCommand.Execute(null);

        await vm.StartStopCommand.ExecuteAsync(null);
        Assert.IsTrue(vm.IsRunning);
        CollectionAssert.Contains(ui.Confirms, "Swarm");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (b.Targets.Count < 3 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        await vm.StartStopCommand.ExecuteAsync(null);
        Assert.IsFalse(vm.IsRunning);
        await vm.LoopTask!;
        Assert.IsGreaterThanOrEqualTo(3, b.Targets.Count);
        Assert.IsEmpty(a.Targets);
    }

    [TestMethod]
    public void FollowPagesKeepTheirSettings()
    {
        var vm = new SwarmFollowLeaderViewModel(FakeUi.Services(out _), new FakeFleet());
        vm.FollowDistance = 4.5m;
        Assert.AreEqual(4.5, vm.Follow.FollowDistance);
        Assert.AreEqual(4.5m, new SwarmFollowLeaderViewModel(FakeUi.Services(out _), new FakeFleet()).FollowDistance);

        var fs = new SwarmFollowSwarmViewModel(FakeUi.Services(out _), new FakeFleet());
        Assert.AreEqual(5, fs.Group.Separation);
        Assert.AreEqual(20, fs.Group.Lead);
        fs.Separation = 8;
        fs.V = true;
        var fs2 = new SwarmFollowSwarmViewModel(FakeUi.Services(out _), new FakeFleet());
        Assert.AreEqual(8, fs2.Group.Separation);
        Assert.IsTrue(fs2.Group.V);
    }

    [TestMethod]
    public async Task FollowSwarmPageSetsTheMastersAndReadsThePath()
    {
        var ground = new FakeSwarmVehicle(1);
        var air = new FakeSwarmVehicle(2) { Mission = NorthMission() };
        var vm = new SwarmFollowSwarmViewModel(FakeUi.Services(out _), new FakeFleet(ground, air));
        vm.OnShown();
        vm.Selected = vm.Vehicles[0];
        vm.SetGroundMasterCommand.Execute(null);
        vm.Selected = vm.Vehicles[1];
        await vm.SetAirMasterCommand.ExecuteAsync(null);

        Assert.IsTrue(vm.IsReady);
        Assert.AreEqual("Ground master", vm.Vehicles[0].Role);
        Assert.AreEqual("Air master", vm.Vehicles[1].Role);
        Assert.AreEqual(1000, vm.Group.Path.Count, 3);
        StringAssert.Contains(vm.PathText, "100 m");

        vm.RtlCommand.Execute(null);
        Assert.AreEqual(WaypointLeaderSwarm.Mode.LandAlt, vm.Group.CurrentMode);
        await vm.ResetModeCommand.ExecuteAsync(null);
        Assert.AreEqual("idle", vm.ModeText);
    }

    [TestMethod]
    public async Task SequencePageBuildsLayoutsAndSteps()
    {
        var services = FakeUi.Services(out var ui);
        var vm = new SwarmSequenceViewModel(services, new FakeFleet());
        vm.DroneCount = 3;
        ui.PromptAnswer = "Line";
        await vm.NewLayoutCommand.ExecuteAsync(null);
        Assert.AreEqual("Line", vm.SelectedLayout!.Id);
        Assert.HasCount(3, vm.LayoutRows);
        vm.LayoutRows[0].North = 12;
        Assert.AreEqual(12, vm.SelectedLayout.Offset[1].y);

        ui.PromptAnswer = "Box";
        await vm.NewLayoutCommand.ExecuteAsync(null);
        Assert.AreEqual(12, vm.SelectedLayout!.Offset[1].y, "a copy of the layout shown");
        vm.AddStepCommand.Execute(null);
        vm.SelectedLayout = vm.Layouts[0];
        vm.AddStepCommand.Execute(null);
        CollectionAssert.AreEqual(new[] { "Box", "Line" }, vm.Steps.ToList());

        vm.MoveStepUpCommand.Execute(null);
        CollectionAssert.AreEqual(new[] { "Line", "Box" }, vm.Steps.ToList());
        Assert.AreEqual(0, vm.SelectedStepIndex);
        StringAssert.Contains(vm.NextStepText, "Line");

        vm.DroneCount = 4;
        Assert.HasCount(4, vm.LayoutRows);
        Assert.HasCount(4, vm.Layouts[1].Offset);
    }

    // ---- the library calls --------------------------------------------------------------------

    [TestMethod]
    public void ARealVehicleSendsItsTargetsToItsOwnSystemId()
    {
        var written = new List<byte>();
        var link = new CommsInjection();
        link.Open();
        link.WriteCallback += (_, bytes) =>
        {
            lock (written)
                written.AddRange(bytes);
        };
        using var mav = new MAVLinkInterface { BaseStream = link };
        var state = mav.MAVlist[7, 1];
        var v = new MavSwarmVehicle(state);
        Assert.AreEqual(7, v.SysId);
        Assert.AreEqual("MAV 7-1", v.Name);

        v.SendPositionVelocity(new PointLatLngAlt(-35.1, 149.2, 12), new Vector3(1, 2, 0));

        var parse = new MAVLink.MavlinkParse();
        byte[] data;
        lock (written)
            data = written.ToArray();
        var msg = parse.ReadPacket(new MemoryStream(data));
        Assert.IsNotNull(msg);
        Assert.AreEqual((uint)MAVLink.MAVLINK_MSG_ID.SET_POSITION_TARGET_GLOBAL_INT, msg.msgid);
        var t = (MAVLink.mavlink_set_position_target_global_int_t)msg.data;
        Assert.AreEqual(7, t.target_system);
        Assert.AreEqual(1, t.target_component);
        Assert.AreEqual((byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT_INT, t.coordinate_frame);
        Assert.AreEqual(-351000000, t.lat_int);
        Assert.AreEqual(12f, t.alt);
        Assert.AreEqual(2f, t.vy);
        Assert.IsNotNull(v.GuidedTarget);
    }
}
