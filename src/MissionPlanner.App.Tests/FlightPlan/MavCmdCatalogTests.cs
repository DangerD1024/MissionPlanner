using System.Text;
using MissionPlanner.App.Core.ViewModels.FlightPlan;
using MissionPlanner.ArduPilot;

namespace MissionPlanner.App.Tests.FlightPlan;

[TestClass]
public class MavCmdCatalogTests
{
    private static MavCmdCatalog Real(MavCmdSection section) =>
        MavCmdCatalog.Parse(PlanTestSupport.RepoFile(MavCmdCatalog.FileName), section);

    [TestMethod]
    public void FirmwarePicksTheSectionMissionPlannerUses()
    {
        Assert.AreEqual(MavCmdSection.Plane, MavCmdCatalog.SectionFor(Firmwares.ArduPlane));
        Assert.AreEqual(MavCmdSection.Plane, MavCmdCatalog.SectionFor(Firmwares.Ateryx));
        Assert.AreEqual(MavCmdSection.Rover, MavCmdCatalog.SectionFor(Firmwares.ArduRover));
        Assert.AreEqual(MavCmdSection.Copter, MavCmdCatalog.SectionFor(Firmwares.ArduCopter2));
        Assert.AreEqual(MavCmdSection.Copter, MavCmdCatalog.SectionFor(Firmwares.ArduSub));
        Assert.AreEqual(MavCmdSection.Copter, MavCmdCatalog.SectionFor(Firmwares.PX4));
        Assert.AreEqual("AC2", MavCmdCatalog.ElementName(MavCmdSection.Copter));
        Assert.AreEqual("APM", MavCmdCatalog.ElementName(MavCmdSection.Plane));
        Assert.AreEqual("APRover", MavCmdCatalog.ElementName(MavCmdSection.Rover));
    }

    [TestMethod]
    public void CopterHeadersComeFromTheAc2Section()
    {
        var c = Real(MavCmdSection.Copter);
        CollectionAssert.AreEqual(new[] { "Delay", "", "", "", "Lat", "Long", "Alt (m)" }, c.HeadersFor(MAVLink.MAV_CMD.WAYPOINT));
        CollectionAssert.AreEqual(new[] { "Turns", "", "Radius", "", "Lat", "Long", "Alt (m)" },
            c.HeadersFor(MAVLink.MAV_CMD.LOITER_TURNS));
        CollectionAssert.AreEqual(new[] { "Type (0:horiz,2:up,3:down)", "Speed m/s", "", "", "", "", "" },
            c.HeadersFor(MAVLink.MAV_CMD.DO_CHANGE_SPEED));
        Assert.AreEqual(MavCmdSection.Copter, c.Section);
    }

    [TestMethod]
    public void PlaneAndRoverHaveTheirOwnLabels()
    {
        var plane = Real(MavCmdSection.Plane);
        CollectionAssert.AreEqual(new[] { "", "Acc radius", "Pass by dist", "", "Lat", "Long", "Alt (m)" },
            plane.HeadersFor(MAVLink.MAV_CMD.WAYPOINT));
        Assert.AreEqual("Pitch Angle", plane.HeadersFor(MAVLink.MAV_CMD.TAKEOFF)[0]);
        CollectionAssert.Contains(plane.MissionCommands.ToList(), MAVLink.MAV_CMD.VTOL_TAKEOFF);

        var rover = Real(MavCmdSection.Rover);
        Assert.AreEqual("Direction (0=F,1=R)", rover.HeadersFor(MAVLink.MAV_CMD.DO_SET_REVERSE)[0]);
        CollectionAssert.DoesNotContain(rover.MissionCommands.ToList(), MAVLink.MAV_CMD.TAKEOFF);

        var copter = Real(MavCmdSection.Copter);
        CollectionAssert.DoesNotContain(copter.MissionCommands.ToList(), MAVLink.MAV_CMD.VTOL_TAKEOFF);
        CollectionAssert.DoesNotContain(copter.MissionCommands.ToList(), MAVLink.MAV_CMD.DO_SET_REVERSE);
    }

    [TestMethod]
    public void EverySectionParsesWithSevenLabelsAndFileOrder()
    {
        foreach (var section in Enum.GetValues<MavCmdSection>())
        {
            var c = Real(section);
            Assert.IsGreaterThan(30, c.Commands.Count, section.ToString());
            Assert.AreEqual("WAYPOINT", c.Commands[0].Name, "file order kept");
            Assert.AreEqual(MAVLink.MAV_CMD.WAYPOINT, c.MissionCommands[0]);
            foreach (var cmd in c.Commands)
                Assert.HasCount(MavCmdCatalog.ValueCount, cmd.Labels, cmd.Name);
            Assert.AreEqual(c.MissionCommands.Count, c.MissionCommands.Distinct().Count(), "no duplicates");
        }
    }

    [TestMethod]
    public void FenceAndRallyListsUseFixedLabels()
    {
        var c = Real(MavCmdSection.Copter);
        CollectionAssert.AreEqual(new[] { "Points", "", "", "", "Lat", "Long", "" },
            c.HeadersFor(MAVLink.MAV_CMD.FENCE_POLYGON_VERTEX_INCLUSION, MAVLink.MAV_MISSION_TYPE.FENCE));
        CollectionAssert.AreEqual(new[] { "Radius (m)", "", "", "", "Lat", "Long", "" },
            c.HeadersFor(MAVLink.MAV_CMD.FENCE_CIRCLE_EXCLUSION, MAVLink.MAV_MISSION_TYPE.FENCE));
        CollectionAssert.AreEqual(new[] { "Radius (m)", "", "", "", "", "", "" },
            c.HeadersFor(MAVLink.MAV_CMD.FENCE_HOME_CIRCLE_INCLUSION, MAVLink.MAV_MISSION_TYPE.FENCE));
        CollectionAssert.AreEqual(new[] { "", "", "", "", "Lat", "Long", "Alt (m)" },
            c.HeadersFor(MAVLink.MAV_CMD.RALLY_POINT, MAVLink.MAV_MISSION_TYPE.RALLY));

        CollectionAssert.AreEqual(MavCmdCatalog.FenceCommands.ToList(), c.CommandsFor(MAVLink.MAV_MISSION_TYPE.FENCE).ToList());
        CollectionAssert.AreEqual(new[] { MAVLink.MAV_CMD.RALLY_POINT }, c.CommandsFor(MAVLink.MAV_MISSION_TYPE.RALLY).ToList());
        Assert.AreSame(c.MissionCommands, c.CommandsFor(MAVLink.MAV_MISSION_TYPE.MISSION));
    }

    [TestMethod]
    public void UnknownCommandGetsGenericHeaders()
    {
        var c = Real(MavCmdSection.Copter);
        // a plane-only command in a copter mission still shows usable columns
        CollectionAssert.AreEqual(MavCmdCatalog.GenericHeaders.ToList(), c.HeadersFor(MAVLink.MAV_CMD.DO_VTOL_TRANSITION));
    }

    [TestMethod]
    public void UnitTypesAndLegacyAltitudeAreLabelled()
    {
        const string xml = """
            <?xml version="1.0"?>
            <CMD>
              <AC2>
                <WAYPOINT><P1>Delay</P1><P2></P2><P3></P3><P4></P4><X>Lat</X><Y>Long</Y><Z>Alt</Z></WAYPOINT>
                <DO_CHANGE_SPEED><P1>Type</P1><P2 unitType="speed">Speed</P2><P3 unitType="dist">Gap</P3><P4></P4><X></X><Y></Y><Z></Z></DO_CHANGE_SPEED>
                <MY_CUSTOM_THING><P1>A</P1><P2>B</P2></MY_CUSTOM_THING>
              </AC2>
            </CMD>
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml.Trim()));
        var c = MavCmdCatalog.Parse(stream, MavCmdSection.Copter);

        Assert.AreEqual("Alt (m)", c.HeadersFor(MAVLink.MAV_CMD.WAYPOINT)[6], "legacy Z=Alt is an altitude");
        var speed = c.HeadersFor(MAVLink.MAV_CMD.DO_CHANGE_SPEED);
        Assert.AreEqual("Speed (m/s)", speed[1]);
        Assert.AreEqual("Gap (m)", speed[2]);

        var custom = c.Find("MY_CUSTOM_THING");
        Assert.IsNotNull(custom);
        Assert.IsNull(custom.Command, "not a MAV_CMD member");
        CollectionAssert.AreEqual(new[] { "A", "B", "", "", "", "", "" }, custom.Labels, "short entries are padded");
        CollectionAssert.AreEqual(new[] { MAVLink.MAV_CMD.WAYPOINT, MAVLink.MAV_CMD.DO_CHANGE_SPEED }, c.MissionCommands.ToList());
    }

    [TestMethod]
    public void MissingSectionIsAnErrorAndLoadFallsBack()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("<CMD><AC2></AC2></CMD>"));
        Assert.ThrowsExactly<InvalidDataException>(() => MavCmdCatalog.Parse(stream, MavCmdSection.Plane));

        var fallback = MavCmdCatalog.Load(MavCmdSection.Plane, Path.Combine(TestEnvironment.DataRoot, "no-such-mavcmd.xml"));
        Assert.IsNull(fallback.Source);
        CollectionAssert.Contains(fallback.MissionCommands.ToList(), MAVLink.MAV_CMD.WAYPOINT);
        Assert.AreEqual("Alt (m)", fallback.HeadersFor(MAVLink.MAV_CMD.WAYPOINT)[6]);
        Assert.AreEqual("", fallback.HeadersFor(MAVLink.MAV_CMD.DO_CHANGE_SPEED)[4], "no location columns for a DO command");
    }
}
