using MissionPlanner.App.Core.ViewModels.FlightData;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Tests.FlightData;

/// <summary>An ISpeech that records.</summary>
internal sealed class RecordingSpeech : ISpeech
{
    public bool speechEnable { get; set; } = true;
    public bool IsReady { get; set; } = true;
    public List<string> Said { get; } = new();

    public void SpeakAsync(string text) => Said.Add(text);

    public void SpeakAsyncCancelAll() => Said.Clear();
}

[TestClass]
public class SpeechTests
{
    private static readonly string[] Keys =
    [
        SpeechOptions.EnableKey, SpeechOptions.ArmedOnlyKey, SpeechOptions.Arm.Enabled, SpeechOptions.Arm.Text, SpeechOptions.DisarmKey,
        SpeechOptions.Battery.Enabled, SpeechOptions.BatteryVoltKey, SpeechOptions.BatteryPercentKey, SpeechOptions.Altitude.Enabled,
        SpeechOptions.AltitudeHeightKey, SpeechOptions.Custom.Enabled, SpeechOptions.LinkLostKey, SpeechOptions.LowGroundSpeed.Enabled,
        SpeechOptions.LowGroundSpeedTriggerKey, SpeechOptions.LowAirSpeedTriggerKey,
    ];

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var k in Keys)
            Settings.Instance.Remove(k);
    }

    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static AnnouncerInput State(bool armed = false, double alt = 0, double volts = 12.6, double percent = 100,
        double sinceLastPacket = 0, double sinceConnect = 100, bool open = true, double gs = 10, double air = 10) =>
        new(open, armed, alt, air, gs, volts, percent, TimeSpan.FromSeconds(sinceLastPacket), TimeSpan.FromSeconds(sinceConnect));

    [TestMethod]
    public void BackendPerPlatform()
    {
        string? Has(string name, params string[] present) => present.Contains(name) ? "/usr/bin/" + name : null;

        var spd = SpeechBackend.For("hello", SpeechBackend.Os.Linux, n => Has(n, "spd-say", "espeak-ng"))!;
        Assert.AreEqual("spd-say", spd.Name);
        CollectionAssert.AreEqual(new[] { "--wait", "--", "hello" }, spd.Arguments.ToArray(), "text is one argument, never a shell string");

        Assert.AreEqual("espeak-ng", SpeechBackend.For("x", SpeechBackend.Os.Linux, n => Has(n, "espeak-ng", "espeak"))!.Name);
        Assert.AreEqual("espeak", SpeechBackend.For("x", SpeechBackend.Os.Linux, n => Has(n, "espeak"))!.Name);
        var festival = SpeechBackend.For("x", SpeechBackend.Os.Linux, n => Has(n, "festival"))!;
        Assert.AreEqual("x", festival.StandardInput, "festival reads the text from stdin");
        Assert.IsNull(SpeechBackend.For("x", SpeechBackend.Os.Linux, _ => null), "no program: no speech");

        var mac = SpeechBackend.For("a \"quoted\" text; rm -rf", SpeechBackend.Os.MacOs, n => Has(n, "say"))!;
        Assert.AreEqual("a \"quoted\" text; rm -rf", mac.Arguments[^1]);

        var win = SpeechBackend.For("hi", SpeechBackend.Os.Windows, n => n == "powershell.exe" ? @"C:\ps.exe" : null)!;
        Assert.AreEqual("hi", win.StandardInput, "Windows gets the text on stdin");
        Assert.IsTrue(win.Arguments.Any(a => a.Contains("System.Speech")));
    }

    [TestMethod]
    public void TextIsMadeSpeakableLikeMissionPlanner()
    {
        Assert.AreEqual("Pre Arm: Compass not calibrated", SpeechBackend.Normalize("PreArm: Compass not calibrated"));
        Assert.AreEqual("WARNING, low altitude 5 meters", SpeechBackend.Normalize("WARNING, low altitude 5m"));
        Assert.AreEqual("Mode q Hover", SpeechBackend.Normalize("Mode QHover"));
        Assert.AreEqual("quick", SpeechBackend.Normalize("quick"));
    }

    [TestMethod]
    public void EngineQueuesAndNeverBlocksTheCaller()
    {
        var gate = new ManualResetEventSlim(false);
        var ran = new List<string>();
        using var engine = new SpeechEngine(t => new SpeechCommand("say", [t], null), (cmd, _) =>
        {
            gate.Wait(5000);
            lock (ran)
                ran.Add(cmd.Arguments[0]);
            return true;
        });

        engine.SpeakAsync("ignored while off");
        Assert.IsTrue(engine.IsReady);

        engine.speechEnable = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 10; i++)
            engine.SpeakAsync("message " + i);
        Assert.IsLessThan(500L, sw.ElapsedMilliseconds, "SpeakAsync only queues");
        Assert.IsFalse(engine.IsReady);

        gate.Set();
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!engine.IsReady && DateTime.UtcNow < until)
            Thread.Sleep(20);
        Assert.IsTrue(engine.IsReady);
        lock (ran)
        {
            Assert.IsLessThanOrEqualTo(5, ran.Count, "a burst is capped");
            Assert.AreEqual("message 9", ran[^1], "the newest is kept");
            Assert.IsFalse(ran.Contains("ignored while off"));
        }
    }

    [TestMethod]
    public void ArmAndDisarmAreAnnouncedOnChange()
    {
        Settings.Instance[SpeechOptions.Arm.Enabled] = "True";
        Settings.Instance[SpeechOptions.Arm.Text] = "Motors armed";
        var speech = new RecordingSpeech();
        var a = new SpeechAnnouncer(speech, t => t, T0);

        a.Step(State(armed: false), T0);
        Assert.IsEmpty(speech.Said, "the first state is not a change");
        a.Step(State(armed: true), T0.AddSeconds(1));
        a.Step(State(armed: true), T0.AddSeconds(2));
        a.Step(State(armed: false), T0.AddSeconds(3));
        CollectionAssert.AreEqual(new[] { "Motors armed", "Disarmed" }, speech.Said);

        speech.speechEnable = false;
        a.Step(State(armed: true), T0.AddSeconds(4));
        Assert.HasCount(2, speech.Said, "speech off: silent");
    }

    [TestMethod]
    public void LowBatteryEveryThirtySeconds()
    {
        Settings.Instance[SpeechOptions.Battery.Enabled] = "True";
        Settings.Instance[SpeechOptions.BatteryVoltKey] = "10.5";
        Settings.Instance[SpeechOptions.BatteryPercentKey] = "20";
        var speech = new RecordingSpeech();
        var a = new SpeechAnnouncer(speech, t => t.Replace("{batv}", "10.2"), T0);

        a.Step(State(volts: 10.2), T0.AddSeconds(10));
        Assert.IsEmpty(speech.Said, "not before 30 s");
        a.Step(State(volts: 10.2), T0.AddSeconds(31));
        Assert.HasCount(1, speech.Said);
        StringAssert.Contains(speech.Said[0], "10.2");
        a.Step(State(volts: 10.2), T0.AddSeconds(40));
        Assert.HasCount(1, speech.Said, "once per 30 s");
        a.Step(State(volts: 12.4, percent: 15), T0.AddSeconds(62));
        Assert.HasCount(2, speech.Said, "percentage below the threshold");
        a.Step(State(volts: 3, percent: 0), T0.AddSeconds(93));
        Assert.HasCount(2, speech.Said, "no battery monitor (under 5 V): silent");
    }

    [TestMethod]
    public void LowAltitudeOnlyAfterBeingHigherAndWhileArmed()
    {
        Settings.Instance[SpeechOptions.Altitude.Enabled] = "True";
        Settings.Instance[SpeechOptions.AltitudeHeightKey] = "5";
        var speech = new RecordingSpeech();
        var a = new SpeechAnnouncer(speech, t => t, T0);

        a.Step(State(armed: true, alt: 3), T0);
        Assert.IsEmpty(speech.Said, "climbing out: never been above 5 m");
        a.Step(State(armed: true, alt: 20), T0.AddSeconds(1));
        a.Step(State(armed: true, alt: 4), T0.AddSeconds(2));
        Assert.HasCount(1, speech.Said);
        a.Step(State(armed: true, alt: 4), T0.AddSeconds(4));
        Assert.HasCount(1, speech.Said, "repeats at most every 5 s");
        a.Step(State(armed: true, alt: 4), T0.AddSeconds(8));
        Assert.HasCount(2, speech.Said);
        a.Step(State(armed: false, alt: 4), T0.AddSeconds(20));
        Assert.HasCount(2, speech.Said, "disarmed: silent");
    }

    [TestMethod]
    public void NoDataWarningWhenTheLinkGoesQuietWhileArmed()
    {
        var speech = new RecordingSpeech();
        var a = new SpeechAnnouncer(speech, t => t, T0);

        a.Step(State(armed: true, sinceLastPacket: 4, sinceConnect: 10), T0);
        Assert.IsNull(a.LastWarning, "the first 30 s after connecting are ignored");
        a.Step(State(armed: true, sinceLastPacket: 2), T0.AddSeconds(1));
        Assert.IsNull(a.LastWarning, "under 3 s");
        a.Step(State(armed: true, sinceLastPacket: 4), T0.AddSeconds(2));
        Assert.AreEqual("WARNING No Data for 4 Seconds", a.LastWarning);
        Assert.Contains("WARNING No Data for 4 Seconds", speech.Said);
        a.Step(State(armed: true, sinceLastPacket: 6), T0.AddSeconds(4));
        Assert.IsNull(a.LastWarning, "every 5 s");
        a.Step(State(armed: true, sinceLastPacket: 9), T0.AddSeconds(8));
        Assert.IsNotNull(a.LastWarning);

        // shown on the HUD even with speech off
        speech.speechEnable = false;
        speech.Said.Clear();
        a.Step(State(armed: true, sinceLastPacket: 20), T0.AddSeconds(20));
        Assert.IsNotNull(a.LastWarning);
        Assert.IsEmpty(speech.Said);

        a.Step(State(armed: false, sinceLastPacket: 30), T0.AddSeconds(30));
        Assert.IsNull(a.LastWarning, "disarmed on the ground: no warning");
    }

    [TestMethod]
    public void ArmedOnlyMutesPeriodicAnnouncementsOnTheGround()
    {
        Settings.Instance[SpeechOptions.ArmedOnlyKey] = "True";
        Settings.Instance[SpeechOptions.Custom.Enabled] = "True";
        var speech = new RecordingSpeech();
        var a = new SpeechAnnouncer(speech, _ => "status", T0);
        a.Step(State(armed: false), T0.AddSeconds(31));
        Assert.IsEmpty(speech.Said);
        a.Step(State(armed: true), T0.AddSeconds(32));
        Assert.Contains("status", speech.Said);
    }

    [TestMethod]
    public void GaugeRangesGrowAndShrinkInRoundSteps()
    {
        Assert.AreEqual(20, GaugeScale.NiceMax(13, 10));
        Assert.AreEqual(50, GaugeScale.NiceMax(21, 10));
        Assert.AreEqual(100, GaugeScale.NiceMax(51, 10));
        Assert.AreEqual(10, GaugeScale.NiceMax(1, 10), "never below the minimum");
        Assert.AreEqual(20, GaugeScale.Next(20, 15, 10), "fits: unchanged");
        Assert.AreEqual(50, GaugeScale.Next(20, 19, 10), "10 % headroom");
        Assert.AreEqual(10, GaugeScale.Next(200, 3, 10), "well below: back down");
        Assert.AreEqual(20, GaugeScale.Next(20, double.NaN, 10));
    }

    [TestMethod]
    public void InstallSetsTheLibraryHooks()
    {
        var services = FakeUi.Services(out _);
        AppSpeech.Install(services.Session);
        Assert.AreSame(AppSpeech.Engine, MAVLinkInterface.Speech);
        Assert.AreSame(AppSpeech.Engine, CurrentState.Speech);
        Assert.IsFalse(AppSpeech.Engine.speechEnable, "off unless the user turned it on");

        Settings.Instance[SpeechOptions.EnableKey] = "True";
        AppSpeech.ApplySettings();
        Assert.IsTrue(AppSpeech.Engine.speechEnable);
        Settings.Instance[SpeechOptions.EnableKey] = "False";
        AppSpeech.ApplySettings();
        Assert.IsFalse(AppSpeech.Engine.speechEnable);
    }
}
