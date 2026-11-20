using MissionPlanner.App.Core.ViewModels.Simulation;

namespace MissionPlanner.App.Tests.Simulation;

[TestClass]
public class SitlArmingCheckTests
{
    private static readonly DateTime Now = new(2026, 11, 20, 12, 0, 0);

    private static (DateTime, string) Msg(int secondsAgo, string text) => (Now.AddSeconds(-secondsAgo), text);

    [TestMethod]
    public void SavedRealAircraftParametersAreReported()
    {
        var messages = new[]
        {
            Msg(50, "PreArm: AHRS: waiting for home"),
            Msg(20, "Arm: Airspeed: 1 need pre-flight calibration"),
            Msg(20, "Arm: RELAY1_PIN=82 invalid"),
            Msg(19, "Arm: Compass 1 not healthy"),
            Msg(5, "Arm: Airspeed: 1 need pre-flight calibration"),
        };

        var blockers = SitlArmingCheck.Blockers(TimeSpan.FromSeconds(60), false, 6, messages, Now);

        CollectionAssert.AreEquivalent(
            new[] { "Airspeed: 1 need pre-flight calibration", "RELAY1_PIN=82 invalid", "Compass 1 not healthy" },
            blockers.ToList(), "start-up messages (waiting for home) are not blockers; repeats count once");
    }

    [TestMethod]
    public void AStartingOrReadySimulatorIsLeftAlone()
    {
        var messages = new[] { Msg(5, "PreArm: Airspeed: 1 need pre-flight calibration") };
        Assert.IsEmpty(SitlArmingCheck.Blockers(TimeSpan.FromSeconds(20), false, 6, messages, Now), "still in its grace time");
        Assert.IsEmpty(SitlArmingCheck.Blockers(TimeSpan.FromSeconds(60), false, 1, messages, Now), "no GPS fix yet");
        Assert.IsEmpty(SitlArmingCheck.Blockers(TimeSpan.FromSeconds(60), true, 6, messages, Now), "ready to arm");
        Assert.IsEmpty(SitlArmingCheck.Blockers(TimeSpan.FromSeconds(60), false, 6,
            [Msg(5, "PreArm: Waiting for RC"), Msg(5, "PreArm: GPS 1: Bad fix"), Msg(90, "PreArm: Throttle too high")], Now),
            "only start-up messages, and one too old to matter");
    }

    [TestMethod]
    public void StateIsBackedUpBeforeAWipe()
    {
        var dir = Path.Combine(TestEnvironment.DataRoot, "sitl-backup-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        Assert.IsNull(SitlArmingCheck.BackUpState(dir, Now), "nothing saved yet");

        File.WriteAllBytes(Path.Combine(dir, "eeprom.bin"), [1, 2, 3]);
        var backup = SitlArmingCheck.BackUpState(dir, Now);

        Assert.AreEqual(Path.Combine(dir, "eeprom-20261120-120000.bin"), backup);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(backup!));
    }
}
