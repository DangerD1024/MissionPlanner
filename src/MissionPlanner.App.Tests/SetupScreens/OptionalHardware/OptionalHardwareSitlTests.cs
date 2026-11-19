using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.ViewModels.SetupScreens;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;
using MissionPlanner.App.Tests.ConfigScreens;

namespace MissionPlanner.App.Tests.SetupScreens.OptionalHardware;

/// <summary>
/// Write round trips of the Optional Hardware pages against ArduPilot SITL. Opt in with
/// MP_TEST_SITL=tcp:127.0.0.1:5760 (an ArduCopter SITL with RNGFND1_TYPE, OSD_TYPE, MNT1_TYPE and
/// CHUTE_ENABLED set, or an ArduPlane SITL for the airspeed test; the others are then inconclusive) and run with --filter TestCategory=Sitl. Every value
/// changed is put back.
/// </summary>
[TestClass]
[TestCategory("Sitl")]
public sealed class OptionalHardwareSitlTests
{
    /// <summary>Drops posted work (metadata downloads); the tests drive the pages themselves.</summary>
    private sealed class QuietDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action action)
        {
        }

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();
    }

    public TestContext TestContext { get; set; } = null!;

    private static async Task<(AppServices Services, FakeDialogs Dialogs)> ConnectAsync()
    {
        var link = Environment.GetEnvironmentVariable("MP_TEST_SITL");
        if (string.IsNullOrWhiteSpace(link))
            Assert.Inconclusive("set MP_TEST_SITL (e.g. tcp:127.0.0.1:5760) to run against SITL");
        var dialogs = new FakeDialogs();
        var services = new AppServices(new QuietDispatcher(), dialogs, new FakeWindows());
        await services.Session.ConnectAsync(LinkSpec.Parse(link), getParams: true);
        Assert.IsTrue(services.Params.HasParams, "parameters downloaded");
        return (services, dialogs);
    }

    /// <summary>Reads the value from the vehicle (not the cache).</summary>
    private static Task<float> ReadAsync(AppServices s, string name)
    {
        var v = s.Session.Vehicle;
        return Task.Run(() => s.Session.Mav.GetParam(v.sysid, v.compid, name));
    }

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task Rangefinder_WritesTheMaximumRange()
    {
        var (s, _) = await ConnectAsync();
        try
        {
            if (!s.Params.Has("RNGFND1_TYPE"))
                Assert.Inconclusive("no rangefinder parameters");
            var page = new RangefinderViewModel(s);
            page.OnShown();
            var first = page.Instances[0];
            var max = first.Main.FirstOrDefault(f => f.Name is "RNGFND1_MAX" or "RNGFND1_MAX_CM");
            if (max?.Value == null)
            {
                Assert.Inconclusive("RNGFND1_MAX not present (set RNGFND1_TYPE and reboot)");
                return;
            }

            var original = max.Value.Value;
            var target = max.Name.EndsWith("_CM") ? original + 50 : Math.Round(original + 0.5, 2);
            Assert.IsTrue(await max.WriteAsync(target));
            Assert.AreEqual(target, await ReadAsync(s, max.Name), 1e-3);
            Assert.IsTrue(await max.WriteAsync(original));
            Assert.AreEqual(original, await ReadAsync(s, max.Name), 1e-3);
        }
        finally
        {
            await s.Session.DisconnectAsync();
        }
    }

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task Osd_WritesAMovedElement()
    {
        var (s, _) = await ConnectAsync();
        try
        {
            if (!s.Params.Has("OSD1_ALTITUDE_X"))
                Assert.Inconclusive("no OSD parameters");
            var page = new OsdViewModel(s);
            page.OnShown();
            page.AutoWriteOnLeave = false;
            var alt = page.Screens.Single(x => x.Number == 1).Items.Single(i => i.Name == "ALTITUDE");
            var (x0, y0, en0) = (alt.X, alt.Y, alt.Enabled);

            page.Move(alt, x0 == 5 ? 6 : 5, y0 == 7 ? 8 : 7);
            alt.Enabled = !en0;
            Assert.AreEqual(1, page.ChangeCount, "one element changed");
            await page.WriteCommand.ExecuteAsync(null);
            Assert.AreEqual(0, page.ChangeCount, page.Status);
            Assert.AreEqual(alt.X, await ReadAsync(s, "OSD1_ALTITUDE_X"));
            Assert.AreEqual(alt.Y, await ReadAsync(s, "OSD1_ALTITUDE_Y"));
            Assert.AreEqual(alt.Enabled ? 1 : 0, await ReadAsync(s, "OSD1_ALTITUDE_EN"));

            alt.X = x0;
            alt.Y = y0;
            alt.Enabled = en0;
            await page.WriteCommand.ExecuteAsync(null);
            Assert.AreEqual(x0, await ReadAsync(s, "OSD1_ALTITUDE_X"));
            Assert.AreEqual(en0 ? 1 : 0, await ReadAsync(s, "OSD1_ALTITUDE_EN"));
        }
        finally
        {
            await s.Session.DisconnectAsync();
        }
    }

    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public async Task InitialParams_WritesTheCalculatedTune()
    {
        var (s, dialogs) = await ConnectAsync();
        try
        {
            var page = new InitialParamsViewModel(s);
            page.OnShown();
            page.PropInches = 7;
            page.Cells = 3;
            page.CalculateCommand.Execute(null);
            Assert.IsTrue(page.HasPreview, page.InputError);
            var originals = page.Rows.Where(r => r.Include).ToDictionary(r => r.Name, r => r.Current!.Value);
            Assert.IsNotEmpty(originals, "a 7\" 3S tune differs from the SITL defaults");
            var expected = page.Rows.Where(r => r.Include).ToDictionary(r => r.Name, r => r.Value);

            dialogs.ConfirmAnswer = true;
            await page.WriteCommand.ExecuteAsync(null);
            try
            {
                foreach (var (name, value) in expected)
                    Assert.AreEqual(value, await ReadAsync(s, name), Math.Max(1e-3, Math.Abs(value) * 1e-6), name);
                Assert.IsTrue(page.Rows.Where(r => expected.ContainsKey(r.Name)).All(r => !r.IsChange), "the preview shows the vehicle's new values");
                StringAssert.Contains(dialogs.Messages.Last().Message, "successfully updated");
            }
            finally
            {
                var restore = await s.Params.SetManyAsync(originals);
                Assert.IsEmpty(restore.Failed);
            }
        }
        finally
        {
            await s.Session.DisconnectAsync();
        }
    }

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task Parachute_AssignsTheReleaseOutput()
    {
        var (s, _) = await ConnectAsync();
        try
        {
            if (!s.Params.Has("CHUTE_TYPE"))
                Assert.Inconclusive("parachute not enabled (CHUTE_ENABLED=1, then refresh)");
            var page = new ParachuteViewModel(s);
            page.OnShown();
            var picker = page.Output!;
            var before = picker.Channel;
            var target = picker.Channels.Last(c => c != before);
            var targetOriginal = s.Params.Get(target + "_FUNCTION")!.Value;

            picker.Selected = picker.Options.Single(o => o.Channel == target);
            for (var i = 0; i < 50 && picker.IsBusy; i++)
                await Task.Delay(100);
            Assert.AreEqual(27, await ReadAsync(s, target + "_FUNCTION"));
            if (before != null)
                Assert.AreEqual(0, await ReadAsync(s, before + "_FUNCTION"), "the old output is cleared");

            // put it back
            Assert.IsTrue(await s.Params.SetAsync(target + "_FUNCTION", targetOriginal));
            if (before != null)
                Assert.IsTrue(await s.Params.SetAsync(before + "_FUNCTION", 27));
        }
        finally
        {
            await s.Session.DisconnectAsync();
        }
    }

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task Airspeed_WritesTheRatio_AndCalibratesTheOffset()
    {
        var (s, dialogs) = await ConnectAsync();
        try
        {
            if (!s.Params.Has("ARSPD_TYPE"))
                Assert.Inconclusive("no airspeed parameters (ArduPlane SITL)");
            var page = new AirspeedViewModel(s);
            page.OnShown();
            var ratio = page.Sensors[0].Main.Single(f => f.Name == "ARSPD_RATIO");
            var original = ratio.Value!.Value;
            var target = Math.Round(original + 0.25, 2);
            Assert.IsTrue(await ratio.WriteAsync(target));
            Assert.AreEqual(target, await ReadAsync(s, "ARSPD_RATIO"), 1e-3);
            Assert.IsTrue(await ratio.WriteAsync(original));

            dialogs.ConfirmAnswer = true;
            await page.CalibrateCommand.ExecuteAsync(null);
            Assert.AreEqual("Calibration done: the offset is updated.", page.CalibrationNote);
        }
        finally
        {
            await s.Session.DisconnectAsync();
        }
    }

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task Mount_WritesAnAngleLimitInDegrees()
    {
        var (s, _) = await ConnectAsync();
        try
        {
            if (!s.Params.Has("MNT1_PITCH_MIN"))
                Assert.Inconclusive("no MNT1 parameters (MNT1_TYPE=1, reboot)");
            var page = new MountViewModel(s);
            page.OnShown();
            var min = page.Mounts[0].Axes[1].Min!;
            var original = min.Field.Value!.Value;
            min.Value = (decimal)(original == -80 ? -70 : -80);
            await Task.Delay(ParamField.NumberDebounce + TimeSpan.FromSeconds(2));
            Assert.AreEqual((double)min.Value!.Value, await ReadAsync(s, "MNT1_PITCH_MIN"), 1e-3);
            Assert.IsTrue(await min.Field.WriteAsync(original));
        }
        finally
        {
            await s.Session.DisconnectAsync();
        }
    }
}
