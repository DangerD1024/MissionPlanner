using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.Mission;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.ViewModels;
using MissionPlanner.App.Core.ViewModels.ConfigScreens;
using MissionPlanner.App.Core.ViewModels.FlightData;
using MissionPlanner.App.Tests.FlightData;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Tests.ConfigScreens;

/// <summary>
/// SETTINGS: the speech card (the same switch as DATA > Actions) and the connection card (GCS ID,
/// serial resets, load waypoints on connect, no RC receiver), all under Mission Planner's keys.
/// </summary>
[TestClass]
public class PlannerSettingsOptionsTests
{
    private static readonly string[] Keys =
    [
        SpeechOptions.EnableKey, SpeechOptions.SeverityKey, ConnectOptions.GcsIdKey, ConnectOptions.ResetOnConnectKey,
        ConnectOptions.Esp32RtsKey, ConnectOptions.LoadWaypointsKey, ConnectOptions.NoRcReceiverKey,
        "comport", "baudrate", "next_tcp",
    ];

    private Dictionary<string, string?> _saved = new();

    [ClassInitialize]
    public static void Init(TestContext _) =>
        Core.Maps.MapTiles.Initialize(Path.Combine(TestEnvironment.DataRoot, "gmapcache") + Path.DirectorySeparatorChar);

    [TestInitialize]
    public void Save()
    {
        _saved = Keys.ToDictionary(k => k, k => Settings.Instance[k]);
        foreach (var k in Keys)
            Settings.Instance.Remove(k);
        ConnectOptions.ApplyGcsId();
    }

    [TestCleanup]
    public void Restore()
    {
        foreach (var (k, v) in _saved)
        {
            if (v == null)
                Settings.Instance.Remove(k);
            else
                Settings.Instance[k] = v;
        }

        ConnectOptions.ApplyGcsId();
        AppSpeech.ApplySettings();
    }

    private static List<string> Changes(INotifyPropertyChanged vm)
    {
        var list = new List<string>();
        vm.PropertyChanged += (_, e) => list.Add(e.PropertyName ?? "");
        return list;
    }

    [TestMethod]
    public void SpeechSwitchIsOneSwitchOnSettingsAndData()
    {
        var services = FakeUi.Services(out _);
        var settings = new PlannerSettingsViewModel(services);
        var data = new FlightDataViewModel(services);
        Assert.IsFalse(settings.SpeechEnabled, "off unless the user turned it on");
        Assert.IsFalse(data.SpeechEnabled);
        var settingsChanges = Changes(settings);
        var dataChanges = Changes(data);

        settings.SpeechEnabled = true;
        Assert.AreEqual("True", Settings.Instance[SpeechOptions.EnableKey], "Mission Planner's key and spelling");
        Assert.IsTrue(AppSpeech.Engine.speechEnable, "applied to the speech engine");
        Assert.IsTrue(data.SpeechEnabled);
        Assert.Contains(nameof(FlightDataViewModel.SpeechEnabled), dataChanges, "the DATA > Actions check box follows");

        settingsChanges.Clear();
        data.SpeechEnabled = false;
        Assert.AreEqual("False", Settings.Instance[SpeechOptions.EnableKey]);
        Assert.IsFalse(AppSpeech.Engine.speechEnable);
        Assert.IsFalse(settings.SpeechEnabled);
        Assert.Contains(nameof(PlannerSettingsViewModel.SpeechEnabled), settingsChanges, "the SETTINGS check box follows");
    }

    [TestMethod]
    public void SpeechSeverityIsTheDialogsSetting()
    {
        var services = FakeUi.Services(out var ui);
        var settings = new PlannerSettingsViewModel(services);
        var data = new FlightDataViewModel(services);
        Assert.AreEqual(4, settings.SpeechSeverity, "Mission Planner's default: Warning");
        CollectionAssert.AreEqual(new SpeechSettingsViewModel(services).Severities.ToList(), settings.SpeechSeverities.ToList());

        settings.SpeechSeverity = 2;
        Assert.AreEqual("2", Settings.Instance[SpeechOptions.SeverityKey], "the key the library reads for spoken status texts");
        Assert.AreEqual(2, new SpeechSettingsViewModel(services).StatusSeverity, "the Speech dialog shows it");
        settings.SpeechSeverity = -1;
        Assert.AreEqual("2", Settings.Instance[SpeechOptions.SeverityKey], "a combo box losing its selection is no choice");

        // saved in the dialog: SETTINGS and DATA follow
        var dialog = new SpeechSettingsViewModel(services) { Enabled = true, StatusSeverity = 6 };
        var settingsChanges = Changes(settings);
        var dataChanges = Changes(data);
        dialog.SaveCommand.Execute(null);
        Assert.IsTrue(settings.SpeechEnabled);
        Assert.IsTrue(data.SpeechEnabled);
        Assert.AreEqual(6, settings.SpeechSeverity);
        Assert.Contains(nameof(PlannerSettingsViewModel.SpeechSeverity), settingsChanges);
        Assert.Contains(nameof(FlightDataViewModel.SpeechEnabled), dataChanges);

        // the button opens that dialog
        settings.OpenSpeechSettingsCommand.Execute(null);
        Assert.IsInstanceOfType<SpeechSettingsViewModel>(ui.Dialogs.Last());
    }

    [TestMethod]
    public void ConnectionOptionsWriteMissionPlannersKeys()
    {
        var services = FakeUi.Services(out _);
        var settings = new PlannerSettingsViewModel(services);
        Assert.IsFalse(settings.ResetOnConnect || settings.Esp32RtsFix || settings.LoadWaypointsOnConnect || settings.NoRcReceiver,
            "all off unless set, as in Mission Planner");

        settings.ResetOnConnect = true;
        settings.Esp32RtsFix = true;
        settings.LoadWaypointsOnConnect = true;
        settings.NoRcReceiver = true;
        Assert.AreEqual("True", Settings.Instance["CHK_resetapmonconnect"]);
        Assert.AreEqual("True", Settings.Instance["CHK_rtsresetesp32"]);
        Assert.AreEqual("True", Settings.Instance["loadwpsonconnect"]);
        Assert.AreEqual("True", Settings.Instance["norcreceiver"]);
        Assert.IsTrue(ConnectOptions.ResetOnConnect && ConnectOptions.Esp32RtsFix && ConnectOptions.LoadWaypoints && ConnectOptions.NoRcReceiver);

        settings.LoadWaypointsOnConnect = false;
        Assert.AreEqual("False", Settings.Instance["loadwpsonconnect"]);

        var again = new PlannerSettingsViewModel(services);
        Assert.IsTrue(again.ResetOnConnect && again.Esp32RtsFix && again.NoRcReceiver);
        Assert.IsFalse(again.LoadWaypointsOnConnect);
    }

    [TestMethod]
    public void GcsIdIsStoredAppliedAndKeptInRange()
    {
        var settings = new PlannerSettingsViewModel(FakeUi.Services(out _));
        Assert.AreEqual(255, settings.GcsId, "Mission Planner's default");
        Assert.AreEqual(255u, MAVLinkInterface.gcssysid);

        settings.GcsId = 200;
        Assert.AreEqual("200", Settings.Instance["gcsid"]);
        Assert.AreEqual(200u, MAVLinkInterface.gcssysid, "the link sends with it at once");

        settings.GcsId = 0;
        Assert.AreEqual(1, settings.GcsId);
        settings.GcsId = 300;
        Assert.AreEqual(255, settings.GcsId);
        Assert.AreEqual(255u, MAVLinkInterface.gcssysid);

        // at start-up the stored ID is applied (AppStartup → PlannerSettings.ApplyStored)
        Settings.Instance["gcsid"] = "201";
        PlannerSettings.ApplyStored();
        Assert.AreEqual(201u, MAVLinkInterface.gcssysid);
        Settings.Instance["gcsid"] = "rubbish";
        PlannerSettings.ApplyStored();
        Assert.AreEqual(255u, MAVLinkInterface.gcssysid);
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task GcsIdReachesTheVehicleAndTheMissionReadUsesIt()
    {
        var services = FakeUi.Services(out _);
        new PlannerSettingsViewModel(services).GcsId = 200;
        Settings.Instance[ConnectOptions.LoadWaypointsKey] = "True";

        await using var link = await Linked.ConnectAsync(services);
        await WaitFor(() => link.Vehicle.Received.Any(m => m.msgid == (uint)MAVLink.MAVLINK_MSG_ID.HEARTBEAT), "a GCS heartbeat");
        var heartbeat = link.Vehicle.Received.First(m => m.msgid == (uint)MAVLink.MAVLINK_MSG_ID.HEARTBEAT);
        Assert.AreEqual(200, (int)heartbeat.sysid, "the ground station's heartbeat carries the GCS ID");

        // the vehicle answers the ID that asked, and the library only accepts answers to its own
        Assert.IsTrue(await MissionOnConnect.RunAsync(services, ask: true));
        Assert.AreEqual(2, services.Mission.Mission.Count);
        Assert.IsTrue(link.Vehicle.Received.Where(m => m.msgid == (uint)MAVLink.MAVLINK_MSG_ID.MISSION_REQUEST_LIST).All(m => m.sysid == 200));
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task LoadWaypointsReadsTheMissionOnlyWhenOn()
    {
        var services = FakeUi.Services(out var ui);
        await using var link = await Linked.ConnectAsync(services);

        // off (the default): nothing is asked of the vehicle
        Assert.IsFalse(await MissionOnConnect.RunAsync(services, ask: true));
        await Task.Delay(300);
        Assert.IsFalse(link.Vehicle.Got(MAVLink.MAVLINK_MSG_ID.MISSION_REQUEST_LIST));
        Assert.AreEqual(0, services.Mission.Mission.Count);

        // on: read into the shared plan, home included
        Settings.Instance[ConnectOptions.LoadWaypointsKey] = "True";
        var toasts = new List<Notification>();
        services.Notified += (_, n) => toasts.Add(n);
        Assert.IsTrue(await MissionOnConnect.RunAsync(services, ask: true));
        Assert.IsTrue(link.Vehicle.Got(MAVLink.MAVLINK_MSG_ID.MISSION_REQUEST_LIST));
        CollectionAssert.AreEqual(new[] { MAVLink.MAV_CMD.TAKEOFF, MAVLink.MAV_CMD.WAYPOINT },
            services.Mission.Mission.Select(i => i.Command).ToList());
        Assert.AreEqual(-35.3640, services.Mission.Mission[1].Lat, 1e-6);
        Assert.AreEqual(-35.363261, services.Mission.HomeLat, 1e-6);
        Assert.IsEmpty(ui.Confirms, "nothing planned: nothing to ask");
        Assert.IsTrue(toasts.Any(t => t.Kind == MessageKind.Info), "reported quietly");

        // planned points: an auto-connect keeps them, a connect the user started asks as Read does
        services.Mission.Mission.RemoveAt(0);
        Assert.IsFalse(await MissionOnConnect.RunAsync(services, ask: false));
        Assert.AreEqual(1, services.Mission.Mission.Count);
        Assert.IsEmpty(ui.Confirms);

        ui.ConfirmAnswer = false;
        Assert.IsFalse(await MissionOnConnect.RunAsync(services, ask: true));
        Assert.AreEqual(1, services.Mission.Mission.Count, "declined: kept");
        Assert.HasCount(1, ui.Confirms);

        ui.ConfirmAnswer = true;
        Assert.IsTrue(await MissionOnConnect.RunAsync(services, ask: true));
        Assert.AreEqual(2, services.Mission.Mission.Count);
    }

    [TestMethod]
    [Timeout(90000, CooperativeCancellation = true)]
    public async Task ConnectingFromTheBarLoadsTheWaypointsWhenOn()
    {
        foreach (var on in new[] { false, true })
        {
            Settings.Instance[ConnectOptions.LoadWaypointsKey] = on.ToString();
            var services = FakeUi.Services(out var ui);
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            ui.PromptAnswer = "127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
            var bar = new ConnectionViewModel(services) { SelectedPort = ConnectionViewModel.Tcp };

            var accept = listener.AcceptTcpClientAsync();
            var connecting = bar.ToggleConnectCommand.ExecuteAsync(null);
            using var client = await accept;
            listener.Stop();
            await using var vehicle = new MissionFakeVehicle(client.GetStream());
            await connecting;
            try
            {
                Assert.IsTrue(services.Session.IsConnected, "connected with parameters");
                if (on)
                {
                    await WaitFor(() => services.Mission.Mission.Count == 2, "the mission read after the connect");
                    Assert.IsTrue(vehicle.Got(MAVLink.MAVLINK_MSG_ID.MISSION_REQUEST_LIST));
                }
                else
                {
                    await Task.Delay(1500);
                    Assert.IsFalse(vehicle.Got(MAVLink.MAVLINK_MSG_ID.MISSION_REQUEST_LIST), "off: no mission read");
                    Assert.AreEqual(0, services.Mission.Mission.Count);
                }
            }
            finally
            {
                await services.Session.DisconnectAsync();
            }
        }
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task NoRcReceiverSilencesTheLibrarysWarning()
    {
        var services = FakeUi.Services(out _);
        await using var link = await Linked.ConnectAsync(services);
        link.Vehicle.RcReceiverUnhealthy = true;
        var cs = services.Session.Cs;
        await WaitFor(() => cs.messageHigh == Strings.NORCReceiver, "the library's No RC Receiver warning");

        new PlannerSettingsViewModel(services).NoRcReceiver = true;
        cs.messageHigh = "sentinel";
        await Task.Delay(1500); // three SYS_STATUS
        Assert.AreEqual("sentinel", cs.messageHigh, "CurrentState reads norcreceiver itself");
    }

    private static async Task WaitFor(Func<bool> condition, string what, int seconds = 20)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(seconds))
                Assert.Fail("timed out waiting for " + what);
            await Task.Delay(20);
        }
    }

    /// <summary>A session connected (without parameters) over TCP to a <see cref="MissionFakeVehicle"/>.</summary>
    private sealed class Linked : IAsyncDisposable
    {
        private readonly AppServices _services;
        private readonly TcpClient _client;

        private Linked(AppServices services, TcpClient client, MissionFakeVehicle vehicle)
        {
            _services = services;
            _client = client;
            Vehicle = vehicle;
        }

        public MissionFakeVehicle Vehicle { get; }

        public static async Task<Linked> ConnectAsync(AppServices services)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accept = listener.AcceptTcpClientAsync();
            var connecting = services.Session.ConnectAsync(LinkSpec.Tcp("127.0.0.1", port), getParams: false);
            var client = await accept;
            listener.Stop();
            var vehicle = new MissionFakeVehicle(client.GetStream());
            await connecting;
            return new Linked(services, client, vehicle);
        }

        public async ValueTask DisposeAsync()
        {
            await _services.Session.DisconnectAsync();
            await Vehicle.DisposeAsync();
            _client.Dispose();
        }
    }
}
