using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.ViewModels.ConfigScreens;
using MissionPlanner.App.Tests.Sitl;
using MissionPlanner.ArduPilot;

namespace MissionPlanner.App.Tests.ConfigScreens;

/// <summary>
/// The tuning pages against ArduPilot SITL: change values the way a user does, write them, read
/// them back from the vehicle and put the originals back. Opt in with MP_TEST_SITL=1 (the binaries
/// are looked up as for the other SITL tests: MP_SITL_DIR or the data folder's sitl/); the SITL
/// instance is MP_TEST_SITL_INSTANCE (default 8, TCP 5760 + 10 × instance).
/// </summary>
[TestClass]
[TestCategory("Sitl")]
public class TuningSitlTests
{
    /// <summary>Drops posted work (the metadata download on connect); the test drives the pages itself.</summary>
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

    private static int Instance => int.TryParse(Environment.GetEnvironmentVariable("MP_TEST_SITL_INSTANCE"), out var i) ? i : 8;

    private static string Binary(string name)
    {
        if (Environment.GetEnvironmentVariable("MP_TEST_SITL") != "1")
            Assert.Inconclusive("set MP_TEST_SITL=1 to run against ArduPilot SITL");
        var binary = SitlProcess.FindBinary(name);
        if (binary == null)
            Assert.Inconclusive(name + " SITL binary not found (MP_SITL_DIR or <data dir>/sitl/)");
        return binary!;
    }

    /// <summary>Starts SITL, connects and downloads the parameters; the caller disposes both.</summary>
    private static async Task<(SitlProcess Sitl, AppServices Services, FakeDialogs Dialogs)> StartAsync(string binary, string model, string defaults)
    {
        var sitl = await SitlProcess.StartAsync(binary, model, defaults, Instance);
        var dialogs = new FakeDialogs();
        var services = new AppServices(new QuietDispatcher(), dialogs, new FakeWindows());
        try
        {
            await services.Session.ConnectAsync(LinkSpec.Tcp("127.0.0.1", sitl.Port), getParams: false);
            await services.Params.RefreshAsync();
            Assert.IsGreaterThan(500, services.Session.Vehicle.param.Count);
            return (sitl, services, dialogs);
        }
        catch
        {
            await services.Session.DisposeAsync();
            sitl.Dispose();
            throw;
        }
    }

    /// <summary>A fresh read of the parameter from the vehicle (not the cached list).</summary>
    private static async Task<double> ReadBack(AppServices s, string name)
    {
        var v = s.Session.Vehicle;
        return await Task.Run(() => s.Session.Mav.GetParam(v.sysid, v.compid, name));
    }

    private static async Task Restore(AppServices s, IReadOnlyDictionary<string, double> originals)
    {
        var result = await s.Params.SetManyAsync(originals);
        Assert.IsEmpty(result.Failed, "restore");
        foreach (var (name, value) in originals)
            Assert.AreEqual(value, await ReadBack(s, name), 1e-4, name + " restored");
    }

    [TestMethod]
    [Timeout(240000, CooperativeCancellation = true)]
    public async Task CopterBasicSlidersAndExtendedCardsWriteAndReadBack()
    {
        var (sitl, s, dialogs) = await StartAsync(Binary("ArduCopter"), "+", "copter.parm");
        try
        {
            Assert.AreEqual(Firmwares.ArduCopter2, s.Session.Cs.firmware);
            var originals = new[] { "ATC_RAT_RLL_P", "ATC_RAT_RLL_I", "ATC_RAT_PIT_P", "ATC_RAT_PIT_I", "ATC_RAT_YAW_P", "PSC_D_ACC_P", "PSC_D_ACC_I" }
                .ToDictionary(n => n, n => s.Params.Get(n)!.Value);

            // Basic Tuning: the roll/pitch sensitivity slider writes P and its three related values when it rests
            var basic = new BasicTuningViewModel(s) { MetaSource = s.Params.Meta, WriteDelay = Timeout.InfiniteTimeSpan };
            basic.Rebuild();
            basic.Tick();
            CollectionAssert.AreEqual(new[] { "ATC_INPUT_TC", "ATC_RAT_RLL_P", "MOT_THST_HOVER", "PSC_D_ACC_P" }, basic.Sliders.Select(x => x.Name).ToArray());
            var rate = basic.Slider("ATC_RAT_RLL_P")!;
            var target = rate.Snap(originals["ATC_RAT_RLL_P"] + 0.02);
            rate.Position = rate.ToPosition(target);
            await basic.FlushAsync();
            TestContext.WriteLine(basic.InfoText);
            foreach (var name in new[] { "ATC_RAT_RLL_P", "ATC_RAT_RLL_I", "ATC_RAT_PIT_P", "ATC_RAT_PIT_I" })
                Assert.AreEqual(target, await ReadBack(s, name), 1e-4, name);
            StringAssert.StartsWith(basic.InfoText, "set ATC_RAT_RLL_P ");
            Assert.IsEmpty(dialogs.Confirms, "the sliders write without asking, as Mission Planner's");

            // climb sensitivity: I at twice P
            var climb = basic.Slider("PSC_D_ACC_P")!;
            var climbTarget = climb.Snap(originals["PSC_D_ACC_P"] + 0.01);
            climb.BoxValue = (decimal)climbTarget;
            await basic.FlushAsync();
            Assert.AreEqual(climbTarget, await ReadBack(s, "PSC_D_ACC_P"), 1e-4);
            Assert.AreEqual(climbTarget * 2, await ReadBack(s, "PSC_D_ACC_I"), 1e-4);

            await Restore(s, originals);

            // Extended Tuning: a locked roll edit writes pitch too, after the confirmation
            var ext = new ExtendedTuningViewModel(s) { MetaSource = s.Params.Meta };
            ext.Rebuild();
            ext.Tick();
            Assert.IsTrue(ext.LockRollPitch);
            Assert.IsTrue(ext.WriteCommand.CanExecute(null));
            var p = (decimal)originals["ATC_RAT_RLL_P"] + 0.01m;
            var yaw = (decimal)originals["ATC_RAT_YAW_P"] + 0.02m;
            ext.Item("ATC_RAT_RLL_P")!.Editor.NumericValue = p;
            ext.Item("ATC_RAT_YAW_P")!.Editor.NumericValue = yaw;
            Assert.AreEqual(3, ext.PendingCount);
            await ext.WriteCommand.ExecuteAsync(null);
            Assert.AreEqual("Confirm Parameter Changes", dialogs.Confirms.Last().Title);
            Assert.AreEqual(0, ext.PendingCount, ext.StatusText);
            Assert.AreEqual((double)p, await ReadBack(s, "ATC_RAT_RLL_P"), 1e-4);
            Assert.AreEqual((double)p, await ReadBack(s, "ATC_RAT_PIT_P"), 1e-4);
            Assert.AreEqual((double)yaw, await ReadBack(s, "ATC_RAT_YAW_P"), 1e-4);

            // Refresh Screen reads them back into the page
            await ext.RefreshScreenCommand.ExecuteAsync(null);
            StringAssert.StartsWith(ext.StatusText, "Read ");
            Assert.AreEqual(p, ext.Item("ATC_RAT_PIT_P")!.Editor.NumericValue);

            await Restore(s, originals);
        }
        finally
        {
            await s.Session.DisposeAsync();
            sitl.Dispose();
        }
    }

    [TestMethod]
    [Timeout(240000, CooperativeCancellation = true)]
    public async Task PlaneBasicTuningCardsWriteAndReadBack()
    {
        var (sitl, s, dialogs) = await StartAsync(Binary("ArduPlane"), "plane", "plane.parm");
        try
        {
            Assert.AreEqual(Firmwares.ArduPlane, s.Session.Cs.firmware);
            var names = new[] { "RLL_RATE_P", "TECS_TIME_CONST", "AIRSPEED_CRUISE", "THR_MAX" };
            var originals = names.ToDictionary(n => n, n => s.Params.Get(n)!.Value);

            var basic = new BasicTuningViewModel(s) { MetaSource = s.Params.Meta };
            basic.Rebuild();
            basic.Tick();
            Assert.IsTrue(basic.HasCards);
            Assert.IsFalse(basic.HasSliders);
            Assert.AreEqual("Servo Roll Pid", basic.Rows[0].Cells[0].Groups[0].Title);

            var targets = new Dictionary<string, decimal>
            {
                ["RLL_RATE_P"] = (decimal)originals["RLL_RATE_P"] + 0.01m,
                ["TECS_TIME_CONST"] = (decimal)originals["TECS_TIME_CONST"] + 1,
                ["AIRSPEED_CRUISE"] = (decimal)originals["AIRSPEED_CRUISE"] - 1,
                ["THR_MAX"] = (decimal)originals["THR_MAX"] - 5,
            };
            foreach (var (name, value) in targets)
                basic.Item(name)!.Editor.NumericValue = value;
            Assert.AreEqual(4, basic.PendingCount);

            await basic.WriteCommand.ExecuteAsync(null);
            Assert.AreEqual("Confirm Parameter Changes", dialogs.Confirms.Last().Title);
            Assert.AreEqual(0, basic.PendingCount, basic.StatusText);
            foreach (var (name, value) in targets)
                Assert.AreEqual((double)value, await ReadBack(s, name), 1e-4, name);

            // the plane has no Extended Tuning without QuadPlane
            var ext = new ExtendedTuningViewModel(s) { MetaSource = s.Params.Meta };
            ext.Rebuild();
            Assert.IsTrue(ext.IsEmpty);

            await Restore(s, originals);
        }
        finally
        {
            await s.Session.DisposeAsync();
            sitl.Dispose();
        }
    }
}
