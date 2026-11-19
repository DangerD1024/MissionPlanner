using System.Diagnostics;
using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.Session;

namespace MissionPlanner.App.Tests.Sitl;

/// <summary>A TCP link to SITL survives the simulator restarting: reconnecting, then resumed.</summary>
[TestClass]
[TestCategory("Sitl")]
public class SitlReconnectTests
{
    private const int Instance = 10;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public async Task SitlRestartIsReconnectedOverTcp()
    {
        if (Environment.GetEnvironmentVariable("MP_TEST_SITL") != "1")
            Assert.Inconclusive("set MP_TEST_SITL=1 to run against ArduPilot SITL");
        var binary = SitlProcess.FindBinary("ArduCopter");
        if (binary == null)
            Assert.Inconclusive("ArduCopter SITL binary not found");

        var sitl = await SitlProcess.StartAsync(binary, "+", "copter.parm", Instance);
        await using var session = new VehicleSession();
        var lostEvents = 0;
        session.LinkLost += (_, _) => Interlocked.Increment(ref lostEvents);
        try
        {
            await session.ConnectAsync(LinkSpec.Tcp("127.0.0.1", sitl.Port), getParams: false);
            await WaitFor(() => session.Cs.yaw != 0, TimeSpan.FromSeconds(20), "telemetry");

            sitl.Dispose();
            var sw = Stopwatch.StartNew();
            await WaitFor(() => session.IsReconnecting, TimeSpan.FromSeconds(8), "reconnecting");
            TestContext.WriteLine($"reconnecting after {sw.ElapsedMilliseconds} ms");
            Assert.AreEqual(LinkState.Connected, session.State);

            sitl = await SitlProcess.StartAsync(binary, "+", "copter.parm", Instance);
            sw.Restart();
            await WaitFor(() => !session.IsReconnecting && session.SinceLastPacket < TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(30), "resumed");
            TestContext.WriteLine($"resumed {sw.ElapsedMilliseconds} ms after SITL restarted");
            Assert.AreEqual(0, lostEvents);
        }
        finally
        {
            sitl.Dispose();
        }
    }

    private static async Task WaitFor(Func<bool> condition, TimeSpan timeout, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout)
                Assert.Fail("timed out waiting for " + what);
            await Task.Delay(100);
        }
    }
}
