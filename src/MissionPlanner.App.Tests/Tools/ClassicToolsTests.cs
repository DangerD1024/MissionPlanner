using System.Net;
using System.Net.Sockets;
using System.Xml.Linq;
using MissionPlanner.App.Core.ViewModels.Tools;
using MissionPlanner.App.Tests.FlightData;

namespace MissionPlanner.App.Tests.Tools;

/// <summary>The classic Ctrl+F screen's own pieces: sensor grid, actions, firmware rip, SSH path, driver list, combiner.</summary>
[TestClass]
public class ClassicToolsTests
{
    [TestMethod]
    public void SensorGridFollowsSysStatus()
    {
        var rows = SensorStatusRow.All();
        Assert.AreEqual("3D GYRO", rows[0].Name);
        Assert.IsTrue(rows.All(r => r.Bit != 0 && (r.Bit & (r.Bit - 1)) == 0), "one bit each");
        Assert.AreEqual(rows.Count, rows.Select(r => r.Bit).Distinct().Count());

        var gyro = rows[0];
        gyro.Update(enabled: 1, present: 1, health: 0);
        Assert.AreEqual(("En", "Present", "Bad"), (gyro.EnabledText, gyro.PresentText, gyro.HealthText));
        gyro.Update(0, 0, 1);
        Assert.AreEqual(("Dis", "No", "Ok"), (gyro.EnabledText, gyro.PresentText, gyro.HealthText));
    }

    [TestMethod]
    public void ToolsScreenGridReadsTheVehicle()
    {
        var services = FakeUi.Services(out _);
        var tools = new ToolsViewModel(services);
        services.Session.Cs.sensors_present.Value = 0b11;
        services.Session.Cs.sensors_enabled.Value = 0b01;
        services.Session.Cs.sensors_health.Value = 0b10;
        tools.OnShown();
        Assert.IsTrue(tools.Sensors[0].Present && tools.Sensors[0].Enabled && !tools.Sensors[0].Healthy);
        Assert.IsTrue(tools.Sensors[1].Present && !tools.Sensors[1].Enabled && tools.Sensors[1].Healthy);
    }

    [TestMethod]
    public void SafetySwitchAndVideoUrlAsMissionPlanner()
    {
        Assert.AreEqual(1u, ClassicActionsViewModel.SafetyCustomMode(true, true), "motor outputs on: custom_mode 1, as Mission Planner sends");
        Assert.AreEqual(0u, ClassicActionsViewModel.SafetyCustomMode(false, true));
        Assert.AreEqual(0u, ClassicActionsViewModel.SafetyCustomMode(true, false), "nothing heard yet");
        StringAssert.StartsWith(ClassicActionsViewModel.UrlPipeline("rtsp://cam/x"), "uridecodebin uri=rtsp://cam/x ! ");
        StringAssert.Contains(ClassicActionsViewModel.UrlPipeline("rtsp://cam/x"), "appsink name=outsink");
    }

    [TestMethod]
    public void DriverCleanPicksMissionPlannersDriversOnly()
    {
        const string output = """
            Microsoft PnP Utility

            Published Name:     oem3.inf
            Original Name:      px4fmu.inf
            Provider Name:      3D Robotics
            Class Name:         Ports (COM & LPT)
            Signer Name:        Microsoft Windows Hardware Compatibility Publisher

            Published Name:     oem7.inf
            Original Name:      lpro564s.inf
            Provider Name:      Logitech
            Signer Name:        Microsoft Windows Hardware Compatibility Publisher

            Published Name:     oem9.inf
            Original Name:      ardupilot.inf
            Provider Name:      ArduPilot
            Signer Name:        Michael Oborne

            Published Name:     oem12.inf
            Original Name:      cubeorange.inf
            Provider Name:      Hex Technology Limited
            """;
        CollectionAssert.AreEqual(new[] { "oem3.inf", "oem9.inf", "oem12.inf" }, DriverCleaner.Find(output.Replace("\n", "\r\n")).ToArray());
    }

    [TestMethod]
    public async Task RipAllFirmwareDownloadsEveryUrlAndWritesALocalList()
    {
        var list = XDocument.Parse("""
            <options>
              <Firmware>
                <software>
                  <name>ArduCopter Quad</name>
                  <url>https://firmware.ardupilot.org/Copter/stable/apm2-quad/ArduCopter.hex</url>
                  <url2560>https://firmware.ardupilot.org/Copter/stable/apm2/ArduCopter.hex</url2560>
                  <urlpx4v2></urlpx4v2>
                  <desc>quad</desc>
                </software>
                <software>
                  <name>Plane</name>
                  <urlfmuv3>https://firmware.ardupilot.org/Plane/stable/fmuv3/arduplane.apj</urlfmuv3>
                </software>
              </Firmware>
            </options>
            """);
        var dir = Path.Combine(TestEnvironment.DataRoot, "rip-" + Guid.NewGuid().ToString("N"));
        var fetched = new List<string>();
        var (files, failed) = await FirmwareArchiveViewModel.RipListAsync(list, (url, path, _) =>
        {
            fetched.Add(url);
            if (url.Contains("apm2/"))
                throw new HttpRequestException("404");
            File.WriteAllText(path, url);
            return Task.CompletedTask;
        }, dir, null, CancellationToken.None);

        Assert.AreEqual(2, files);
        Assert.AreEqual(1, failed);
        Assert.AreEqual(3, fetched.Count, "empty url fields are skipped");
        Assert.IsTrue(File.Exists(Path.Combine(dir, "Plane", "stable", "fmuv3", "arduplane.apj")));
        var local = XDocument.Load(Path.Combine(dir, "firmware2.xml"));
        Assert.AreEqual("Copter/stable/apm2-quad/ArduCopter.hex", local.Descendants("url").Single().Value, "the list points at the local copy");
        Assert.AreEqual("quad", local.Descendants("desc").Single().Value);
    }

    [TestMethod]
    public void ScpPathSplitsLikeMissionPlanners()
    {
        var p = ScpLogDownloadViewModel.ParseScpPath("user:edison@10.0.1.128:/home/user/dflogger/dataflash/")!.Value;
        Assert.AreEqual(("user", "edison", "10.0.1.128", "/home/user/dflogger/dataflash/"), p);
        Assert.IsNull(ScpLogDownloadViewModel.ParseScpPath("::::"));
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task CombinerWithoutSitlFindsNothingAndListensNowhere()
    {
        // a port with nothing listening: no vehicles, and no server left open
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var free = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        using var combiner = new SitlCombiner();
        Assert.AreEqual(0, await combiner.StartAsync(firstPort: free, listenPort: free, maxVehicles: 1));
        Assert.IsFalse(combiner.IsRunning);
    }

    [TestMethod]
    public void EveryClassicButtonHasSomewhereToGo()
    {
        var tools = new ToolsViewModel(FakeUi.Services(out _));
        foreach (var b in tools.ClassicButtons)
        {
            Assert.IsNotNull(b.Command, b.Title);
            if (b.Parameter is Core.ViewModels.MenuItemViewModel item)
                Assert.IsTrue(tools.Items.Contains(item), b.Title);
        }
    }
}
