using System.Net;
using System.Net.Sockets;
using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.ViewModels.ConfigScreens;

namespace MissionPlanner.App.Tests.ConfigScreens;

/// <summary>
/// The write flow against a scripted vehicle over TCP: download, edit, confirm, PARAM_SET in
/// Mission Planner's order, and the page updated from the vehicle's answers.
/// </summary>
[TestClass]
public class ParamWriteVehicleTests
{
    /// <summary>
    /// Drops posted work: on connect ParamService posts a metadata download, and tests make no
    /// network calls. The test drives the page's refreshes itself.
    /// </summary>
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
    public async Task WritesConfirmedEditsInOrderAndUpdatesTheRows()
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
            [("BATT_CAPACITY", 3300), ("FENCE_ENABLE", 0), ("FENCE_RADIUS", 150), ("WPNAV_SPEED", 1000)]);
        await connecting;
        try
        {
            await services.Params.RefreshAsync();
            Assert.AreEqual(4, services.Params.Count);

            var page = new FullParameterListViewModel(services);
            await page.RebuildAsync();
            page.Tick();
            Assert.IsTrue(page.IsConnected);
            Assert.IsTrue(page.WriteCommand.CanExecute(null));

            Assert.IsTrue(await page.CommitEditAsync(page.AllRows.Single(r => r.Name == "BATT_CAPACITY"), "5200"));
            Assert.IsTrue(await page.CommitEditAsync(page.AllRows.Single(r => r.Name == "FENCE_ENABLE"), "1"));

            // declined: nothing is sent
            dialogs.ConfirmAnswer = false;
            await page.WriteCommand.ExecuteAsync(null);
            Assert.IsTrue(vehicle.Sets.IsEmpty);
            Assert.AreEqual(2, page.PendingCount);
            var confirm = dialogs.Confirms.Last().Message;
            Assert.IsTrue(confirm.Contains("FENCE_ENABLE: 0 -> 1"), confirm);
            Assert.IsTrue(confirm.Contains("BATT_CAPACITY: 3300 -> 5200"), confirm);

            dialogs.ConfirmAnswer = true;
            await page.WriteCommand.ExecuteAsync(null);

            // *_ENABLE first: enabling a feature can create the others
            CollectionAssert.AreEqual(new[] { ("FENCE_ENABLE", 1f), ("BATT_CAPACITY", 5200f) }, vehicle.Sets.ToArray());
            Assert.AreEqual(5200f, vehicle.Value("BATT_CAPACITY"));
            Assert.AreEqual(0, page.PendingCount);
            var row = page.AllRows.Single(r => r.Name == "BATT_CAPACITY");
            Assert.IsFalse(row.IsModified);
            Assert.AreEqual(5200, row.Value);
            Assert.AreEqual(5200, services.Params.Get("BATT_CAPACITY"));
            Assert.IsTrue(dialogs.Messages.Any(m => m.Title == "Saved" && m.Message.Contains("2 parameters")),
                string.Join(" | ", dialogs.Messages.Select(m => m.Title + ": " + m.Message)));
        }
        finally
        {
            await services.Session.DisconnectAsync();
        }
    }
}
