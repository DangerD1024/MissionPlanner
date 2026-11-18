using System.Net;
using System.Net.Sockets;
using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.ViewModels.ConfigScreens;

namespace MissionPlanner.App.Tests.ConfigScreens;

/// <summary>The tuning pages against a scripted copter over TCP: what reaches the vehicle.</summary>
[TestClass]
public class TuningVehicleTests
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

    [TestMethod]
    [Timeout(90000, CooperativeCancellation = true)]
    public async Task SlidersAndLockedEditsReachTheVehicle()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var dialogs = new FakeDialogs();
        var services = new AppServices(new QuietDispatcher(), dialogs, new FakeWindows());

        var accept = listener.AcceptTcpClientAsync();
        var connecting = services.Session.ConnectAsync(LinkSpec.Tcp("127.0.0.1", port), getParams: false);
        using var client = await accept;
        listener.Stop();
        await using var vehicle = new ParamVehicle(client.GetStream(),
        [
            ("ATC_RAT_RLL_P", 0.135f), ("ATC_RAT_RLL_I", 0.135f), ("ATC_RAT_PIT_P", 0.135f), ("ATC_RAT_PIT_I", 0.135f),
            ("ATC_RAT_YAW_P", 0.18f), ("PSC_ACCZ_P", 0.5f), ("PSC_ACCZ_I", 1f),
        ]);
        await connecting;
        try
        {
            await services.Params.RefreshAsync();
            Assert.AreEqual(MissionPlanner.ArduPilot.Firmwares.ArduCopter2, services.Session.Cs.firmware);

            // Basic Tuning: climb sensitivity writes P and I = 2 × P
            var basic = new BasicTuningViewModel(services) { XmlPath = Path.Combine(TestEnvironment.DataRoot, "none.xml"), MetaSource = _ => null };
            basic.Rebuild();
            basic.Tick();
            Assert.IsTrue(basic.WriteCommand.CanExecute(null));
            basic.Item("PSC_ACCZ_P")!.BoxValue = 0.6m;
            await basic.WriteCommand.ExecuteAsync(null);

            CollectionAssert.AreEquivalent(new[] { ("PSC_ACCZ_I", 1.2f), ("PSC_ACCZ_P", 0.6f) }, vehicle.Sets.ToArray());
            Assert.AreEqual(0, basic.PendingCount);
            Assert.IsFalse(basic.Item("PSC_ACCZ_P")!.IsModified);
            Assert.AreEqual(0.6, basic.Item("PSC_ACCZ_P")!.Value, 1e-6);
            Assert.AreEqual(1.2, basic.Item("PSC_ACCZ_P")!.Links.Single().Current!.Value, 1e-6);

            // Extended Tuning: a locked roll edit writes pitch too; a large change is asked about first
            vehicle.Sets.Clear();
            var extended = new ExtendedTuningViewModel(services) { MetaSource = _ => null };
            extended.Rebuild();
            extended.Tick();
            Assert.IsTrue(extended.LockRollPitch);
            extended.Item("ATC_RAT_RLL_P")!.Editor.NumericValue = 0.3m;

            dialogs.ConfirmAnswer = false;
            await extended.WriteCommand.ExecuteAsync(null);
            Assert.IsTrue(vehicle.Sets.IsEmpty);
            Assert.AreEqual("Large Value", dialogs.Confirms.Last().Title);
            Assert.AreEqual(0, extended.PendingCount, "declined values are put back");
            Assert.AreEqual(0.135m, extended.Item("ATC_RAT_PIT_P")!.Editor.NumericValue);

            extended.Item("ATC_RAT_RLL_P")!.Editor.NumericValue = 0.15m;
            dialogs.ConfirmAnswer = true;
            await extended.WriteCommand.ExecuteAsync(null);
            Assert.AreEqual("Confirm Parameter Changes", dialogs.Confirms.Last().Title);
            CollectionAssert.AreEquivalent(new[] { ("ATC_RAT_PIT_P", 0.15f), ("ATC_RAT_RLL_P", 0.15f) }, vehicle.Sets.ToArray());
            Assert.AreEqual(0, extended.PendingCount);
            Assert.AreEqual(0.15, services.Params.Get("ATC_RAT_PIT_P")!.Value, 1e-6);

            // Refresh Screen reads the page's values one by one
            vehicle.Sets.Clear();
            await extended.RefreshScreenCommand.ExecuteAsync(null);
            StringAssert.Contains(extended.StatusText, "Read 7 parameters");
            Assert.IsTrue(vehicle.Sets.IsEmpty);
        }
        finally
        {
            await services.Session.DisconnectAsync();
        }
    }
}
