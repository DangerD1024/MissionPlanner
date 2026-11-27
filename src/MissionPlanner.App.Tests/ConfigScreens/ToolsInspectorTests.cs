using System.Collections.ObjectModel;
using System.Reflection;
using System.Text;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.ViewModels.ConfigScreens;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Tests.ConfigScreens;

/// <summary>Builds real packets with the library's generator and parses them back, as the link does.</summary>
internal static class ToolsPackets
{
    private static readonly MAVLink.MavlinkParse Parse = new();
    private static int _seq;

    public static MAVLink.MAVLinkMessage Make(MAVLink.MAVLINK_MSG_ID id, object msg, byte sysid = 1, byte compid = 1)
    {
        var bytes = Parse.GenerateMAVLinkPacket20(id, msg, false, sysid, compid, Interlocked.Increment(ref _seq) & 0xff);
        return new MAVLink.MAVLinkMessage(bytes);
    }

    public static MAVLink.MAVLinkMessage Heartbeat(byte sysid = 1, byte compid = 1) =>
        Make(MAVLink.MAVLINK_MSG_ID.HEARTBEAT, new MAVLink.mavlink_heartbeat_t(7, (byte)MAVLink.MAV_TYPE.QUADROTOR,
            (byte)MAVLink.MAV_AUTOPILOT.ARDUPILOTMEGA, 81, (byte)MAVLink.MAV_STATE.ACTIVE, 3), sysid, compid);

    public static MAVLink.MAVLinkMessage Attitude(float roll, byte sysid = 1, byte compid = 1) =>
        Make(MAVLink.MAVLINK_MSG_ID.ATTITUDE, new MAVLink.mavlink_attitude_t(1234, roll, -0.25f, 3.0f, 0, 0, 0), sysid, compid);

    public static MAVLink.MAVLinkMessage ParamValue(string name, float value) =>
        Make(MAVLink.MAVLINK_MSG_ID.PARAM_VALUE, new MAVLink.mavlink_param_value_t(value, 900, 12,
            Encoding.ASCII.GetBytes(name).MakeSize(16), (byte)MAVLink.MAV_PARAM_TYPE.REAL32));
}

[TestClass]
public class ToolsInspectorFormatTests
{
    private static InspectorField Field(uint msgid, string name) =>
        InspectorFormat.FieldsOf(msgid).Single(f => f.Name == name);

    private static string Value(MAVLink.MAVLinkMessage m, string field) =>
        InspectorFormat.FormatField(Field(m.msgid, field), m.data);

    [TestMethod]
    public void PacketsRoundTripThroughTheParser()
    {
        var m = ToolsPackets.Attitude(0.5f, 3, 4);
        Assert.AreEqual(3u, m.sysid);
        Assert.AreEqual((byte)4, m.compid);
        Assert.AreEqual((uint)MAVLink.MAVLINK_MSG_ID.ATTITUDE, m.msgid);
        Assert.AreEqual("ATTITUDE", m.msgtypename);
        Assert.AreEqual(0.5f, ((MAVLink.mavlink_attitude_t)m.data).roll);
    }

    [TestMethod]
    public void FieldsComeInXmlOrderAndAreCached()
    {
        var fields = InspectorFormat.FieldsOf((uint)MAVLink.MAVLINK_MSG_ID.HEARTBEAT);
        Assert.AreEqual("type,autopilot,base_mode,custom_mode,system_status,mavlink_version", string.Join(",", fields.Select(f => f.Name)));
        Assert.AreSame(fields, InspectorFormat.FieldsOf((uint)MAVLink.MAVLINK_MSG_ID.HEARTBEAT));
        Assert.AreSame(fields, InspectorFormat.FieldsOf(typeof(MAVLink.mavlink_heartbeat_t)));
        Assert.AreEqual(0, InspectorFormat.FieldsOf(999_999).Count);
    }

    [TestMethod]
    public void FieldsDescribeTypeAndUnits()
    {
        var roll = Field((uint)MAVLink.MAVLINK_MSG_ID.ATTITUDE, "roll");
        Assert.AreEqual("float", roll.TypeName);
        Assert.AreEqual("rad", roll.Units);
        Assert.IsTrue(roll.IsNumeric);
        Assert.IsFalse(roll.IsArray);

        var id = Field((uint)MAVLink.MAVLINK_MSG_ID.PARAM_VALUE, "param_id");
        Assert.AreEqual("char[16]", id.TypeName);
        Assert.IsTrue(id.IsText);
        Assert.IsFalse(id.IsNumeric);

        var prn = Field((uint)MAVLink.MAVLINK_MSG_ID.GPS_STATUS, "satellite_prn");
        Assert.AreEqual("uint8_t[20]", prn.TypeName);
        Assert.IsTrue(prn.IsNumeric);
        Assert.IsTrue(prn.IsArray);
    }

    [TestMethod]
    public void ScalarsUseInvariantShortestForms()
    {
        var m = ToolsPackets.Attitude(0.1f);
        Assert.AreEqual("0.1", Value(m, "roll"));
        Assert.AreEqual("-0.25", Value(m, "pitch"));
        Assert.AreEqual("1234", Value(m, "time_boot_ms"));
        Assert.AreEqual("NaN", InspectorFormat.FormatScalar(float.NaN));
        Assert.AreEqual("1.234568E+07", InspectorFormat.FormatScalar(12345678.9f));

        var hb = ToolsPackets.Heartbeat();
        Assert.AreEqual("2", Value(hb, "type"));
        Assert.AreEqual("81", Value(hb, "base_mode"));
    }

    [TestMethod]
    public void CharArraysShowAsText()
    {
        Assert.AreEqual("ARMING_CHECK", Value(ToolsPackets.ParamValue("ARMING_CHECK", 1), "param_id"));

        var text = ToolsPackets.Make(MAVLink.MAVLINK_MSG_ID.STATUSTEXT, new MAVLink.mavlink_statustext_t
        {
            severity = 6,
            text = Encoding.ASCII.GetBytes("PreArm: Compass not calibrated").MakeSize(50),
        });
        Assert.AreEqual("PreArm: Compass not calibrated", Value(text, "text"));

        var named = ToolsPackets.Make(MAVLink.MAVLINK_MSG_ID.NAMED_VALUE_FLOAT,
            new MAVLink.mavlink_named_value_float_t(10, 2.5f, Encoding.ASCII.GetBytes("speed").MakeSize(10)));
        Assert.AreEqual("speed", Value(named, "name"));
        Assert.AreEqual("2.5", Value(named, "value"));
    }

    [TestMethod]
    public void OtherArraysAreCommaLists()
    {
        var prn = new byte[20];
        for (var i = 0; i < 4; i++)
            prn[i] = (byte)(i + 1);
        var m = ToolsPackets.Make(MAVLink.MAVLINK_MSG_ID.GPS_STATUS, new MAVLink.mavlink_gps_status_t
        {
            satellites_visible = 4,
            satellite_prn = prn,
            satellite_used = new byte[20],
            satellite_elevation = new byte[20],
            satellite_azimuth = new byte[20],
            satellite_snr = new byte[20],
        });
        Assert.AreEqual("1,2,3,4,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0", Value(m, "satellite_prn"));

        var controls = new float[16];
        controls[0] = 0.5f;
        controls[1] = -1;
        var hil = ToolsPackets.Make(MAVLink.MAVLINK_MSG_ID.HIL_ACTUATOR_CONTROLS,
            new MAVLink.mavlink_hil_actuator_controls_t { controls = controls });
        Assert.IsTrue(Value(hil, "controls").StartsWith("0.5,-1,0,0,"), Value(hil, "controls"));
    }

    [TestMethod]
    public void UnprintableBytesInATextFieldFallBackToNumbers()
    {
        var field = Field((uint)MAVLink.MAVLINK_MSG_ID.PARAM_VALUE, "param_id");
        Assert.AreEqual("1,200,0", InspectorFormat.FormatValue(field, new byte[] { 1, 200, 0 }));
        Assert.AreEqual("", InspectorFormat.FormatValue(field, new byte[16]));
        Assert.IsNull(InspectorFormat.TryText([65, 0, 66]), "bytes after the terminator are not text padding");
    }

    [TestMethod]
    public void UnixTimeIsShownAsUtc()
    {
        var m = ToolsPackets.Make(MAVLink.MAVLINK_MSG_ID.SYSTEM_TIME, new MAVLink.mavlink_system_time_t(1_700_000_000_123_000UL, 99));
        Assert.AreEqual("2023-11-14 22:13:20.123 UTC", Value(m, "time_unix_usec"));
        Assert.AreEqual("0", InspectorFormat.FormatUnixTime(0));
        Assert.AreEqual(ulong.MaxValue.ToString(), InspectorFormat.FormatUnixTime(ulong.MaxValue));
    }

    [TestMethod]
    public void NumbersAreExtractedForGraphs()
    {
        var into = new List<double>();
        Assert.IsTrue(InspectorFormat.TryGetNumbers(Field(30, "roll"), ToolsPackets.Attitude(0.75f).data, into));
        CollectionAssert.AreEqual(new[] { 0.75 }, into);

        var controls = new float[16];
        controls[2] = 3;
        var hil = ToolsPackets.Make(MAVLink.MAVLINK_MSG_ID.HIL_ACTUATOR_CONTROLS, new MAVLink.mavlink_hil_actuator_controls_t { controls = controls });
        Assert.IsTrue(InspectorFormat.TryGetNumbers(Field(hil.msgid, "controls"), hil.data, into));
        Assert.AreEqual(16, into.Count);
        Assert.AreEqual(3.0, into[2]);

        Assert.IsFalse(InspectorFormat.TryGetNumbers(Field(22, "param_id"), ToolsPackets.ParamValue("X", 1).data, into));
    }

    [TestMethod]
    public void RatesAndBandwidthFormat()
    {
        Assert.AreEqual("4.0 Hz", InspectorFormat.FormatRate(4));
        Assert.AreEqual("50 Hz", InspectorFormat.FormatRate(50.2));
        Assert.AreEqual("870 B/s", InspectorFormat.FormatBytesPerSecond(870));
        Assert.AreEqual("2.5 kB/s", InspectorFormat.FormatBytesPerSecond(2500));
        Assert.AreEqual("12 kB/s", InspectorFormat.FormatBytesPerSecond(12_300));
    }
}

[TestClass]
public class ToolsInspectorTreeTests
{
    private static InspectorSample Sample(MAVLink.MAVLinkMessage m, double rate = 10, double bps = 400) =>
        new(m.sysid, m.compid, m.msgid, m.msgtypename, m.data, rate, bps);

    private static string Names(IEnumerable<InspectorNode> nodes) =>
        string.Join(",", nodes.Select(n => n switch
        {
            InspectorVehicleNode v => $"{v.SysId}/{v.CompId}",
            InspectorMessageNode m => m.Name,
            InspectorFieldNode f => f.Name,
            _ => "?",
        }));

    [TestMethod]
    public void UpdateBuildsVehicleMessageFieldInSortedOrder()
    {
        var tree = new InspectorTreeModel();
        tree.Update([
            Sample(ToolsPackets.Heartbeat(255, 190)),
            Sample(ToolsPackets.Heartbeat()),
            Sample(ToolsPackets.Attitude(0.1f)),
            Sample(ToolsPackets.Heartbeat(1, 0)),
        ]);

        Assert.AreEqual("1/0,1/1,255/190", Names(tree.Vehicles));
        var autopilot = tree.Vehicles[1];
        Assert.AreEqual("System 1 · Component 1 AUTOPILOT1", autopilot.Title);
        Assert.AreEqual("System 255 · Component 190 MISSIONPLANNER", tree.Vehicles[2].Title);
        Assert.AreEqual("ATTITUDE,HEARTBEAT", Names(autopilot.Children));
        Assert.AreEqual("2 messages · 800 B/s", autopilot.TrafficText);

        var attitude = (InspectorMessageNode)autopilot.Children[0];
        Assert.AreEqual("time_boot_ms,roll,pitch,yaw,rollspeed,pitchspeed,yawspeed", Names(attitude.Children));
        Assert.AreEqual("0.1", attitude.FindField("roll")!.Value, "values are filled on creation");
        Assert.AreEqual("10 Hz", attitude.RateText);
        Assert.AreEqual("#30", attitude.IdText);
        Assert.AreEqual("float · rad", attitude.FindField("roll")!.TypeText);
    }

    [TestMethod]
    public void UpdateReusesNodesAndOnlyRefreshesExpandedMessages()
    {
        var tree = new InspectorTreeModel();
        tree.Update([Sample(ToolsPackets.Attitude(0.1f)), Sample(ToolsPackets.Heartbeat())]);
        var vehicle = tree.Vehicles[0];
        var attitude = tree.Find(1, 1, 30)!;
        var heartbeat = tree.Find(1, 1, 0)!;
        var roll = attitude.FindField("roll")!;
        attitude.IsExpanded = true;

        var notifications = new List<string>();
        roll.PropertyChanged += (_, e) => notifications.Add(e.PropertyName!);
        var vehicleChanges = 0;
        vehicle.Children.CollectionChanged += (_, _) => vehicleChanges++;

        tree.Update([Sample(ToolsPackets.Attitude(0.2f), rate: 12), Sample(ToolsPackets.Heartbeat(), rate: 1)]);

        Assert.AreSame(vehicle, tree.Vehicles[0]);
        Assert.AreSame(attitude, tree.Find(1, 1, 30));
        Assert.AreSame(roll, attitude.FindField("roll"));
        Assert.AreEqual(0, vehicleChanges, "no rebuild: the message list did not change");
        Assert.AreEqual("0.2", roll.Value);
        CollectionAssert.Contains(notifications, nameof(InspectorFieldNode.Value));
        Assert.AreEqual(12, attitude.RateHz);
        Assert.IsTrue(attitude.IsExpanded);

        // a collapsed message keeps its last values until it is opened
        var time = ToolsPackets.Make(MAVLink.MAVLINK_MSG_ID.HEARTBEAT, new MAVLink.mavlink_heartbeat_t(99, 2, 3, 81, 4, 3));
        tree.Update([Sample(time)]);
        Assert.AreEqual("7", heartbeat.FindField("custom_mode")!.Value);
        heartbeat.IsExpanded = true;
        Assert.AreEqual("99", heartbeat.FindField("custom_mode")!.Value);
    }

    [TestMethod]
    public void NewMessagesAreInsertedInPlace()
    {
        var tree = new InspectorTreeModel();
        tree.Update([Sample(ToolsPackets.Attitude(0.1f))]);
        var attitude = tree.Find(1, 1, 30)!;
        tree.Update([Sample(ToolsPackets.ParamValue("A", 1)), Sample(ToolsPackets.Heartbeat())]);
        Assert.AreEqual("ATTITUDE,HEARTBEAT,PARAM_VALUE", Names(tree.Vehicles[0].Children));
        Assert.AreSame(attitude, tree.Vehicles[0].Children[0]);
    }

    [TestMethod]
    public void FilterHidesMessagesAndEmptyVehiclesAndKeepsState()
    {
        var tree = new InspectorTreeModel();
        tree.Update([Sample(ToolsPackets.Attitude(0.1f)), Sample(ToolsPackets.Heartbeat()), Sample(ToolsPackets.Heartbeat(255, 190))]);
        var attitude = tree.Find(1, 1, 30)!;
        attitude.IsExpanded = true;

        tree.Filter = "att";
        Assert.AreEqual("1/1", Names(tree.Vehicles));
        Assert.AreEqual("ATTITUDE", Names(tree.Vehicles[0].Children));

        tree.Filter = "#0";
        Assert.AreEqual("1/1,255/190", Names(tree.Vehicles));
        Assert.AreEqual("HEARTBEAT", Names(tree.Vehicles[0].Children));

        tree.Filter = "30";
        Assert.AreEqual("ATTITUDE", Names(tree.Vehicles[0].Children));

        tree.Filter = "nothing-matches";
        Assert.AreEqual(0, tree.Vehicles.Count);

        tree.Filter = "";
        Assert.AreEqual("1/1,255/190", Names(tree.Vehicles));
        Assert.AreSame(attitude, tree.Vehicles[0].Children[0]);
        Assert.IsTrue(attitude.IsExpanded);

        // a message arriving while filtered out is still kept
        tree.Filter = "HEART";
        tree.Update([Sample(ToolsPackets.ParamValue("B", 2))]);
        Assert.AreEqual("HEARTBEAT", Names(tree.Vehicles[0].Children));
        tree.Filter = "";
        Assert.AreEqual("ATTITUDE,HEARTBEAT,PARAM_VALUE", Names(tree.Vehicles[0].Children));
    }

    [TestMethod]
    public void SyncOrderedReachesAnyOrderWithMinimalChurn()
    {
        var rnd = new Random(42);
        var items = Enumerable.Range(0, 12).Select(i => new object()).ToList();
        for (var round = 0; round < 200; round++)
        {
            var target = new ObservableCollection<object>(items.OrderBy(_ => rnd.Next()).Take(rnd.Next(items.Count)));
            var desired = items.OrderBy(_ => rnd.Next()).Take(rnd.Next(items.Count)).ToList();
            InspectorTreeModel.SyncOrdered(target, desired);
            CollectionAssert.AreEqual(desired, target);
        }

        var same = new ObservableCollection<object>(items);
        var changes = 0;
        same.CollectionChanged += (_, _) => changes++;
        InspectorTreeModel.SyncOrdered(same, items);
        Assert.AreEqual(0, changes);
    }

    [TestMethod]
    public void SamplerReadsThePacketInspector()
    {
        var inspector = new PacketInspector<MAVLink.MAVLinkMessage>();
        for (var i = 0; i < 5; i++)
        {
            var m = ToolsPackets.Attitude(i * 0.1f);
            inspector.Add(m.sysid, m.compid, m.msgid, m, m.Length);
        }

        var hb = ToolsPackets.Heartbeat(2, 1);
        inspector.Add(hb.sysid, hb.compid, hb.msgid, hb, hb.Length);

        var samples = InspectorSampler.Sample(inspector);
        Assert.AreEqual(2, samples.Count);
        var att = samples.Single(s => s.MsgId == 30);
        Assert.AreEqual("ATTITUDE", att.Name);
        Assert.AreEqual(0.4f, ((MAVLink.mavlink_attitude_t)att.Data!).roll, 1e-6, "the latest copy is kept");
        Assert.IsTrue(att.RateHz > 0);
        Assert.IsTrue(att.BytesPerSecond > 0);
        Assert.IsTrue(InspectorSampler.SafeBps(inspector, 1, 1) > 0);
        Assert.AreEqual(0, InspectorSampler.SafeBps(inspector, 9, 9), "unknown components read as no traffic");
    }
}

[TestClass]
public class ToolsInspectorViewModelTests
{
    private static int HandlerCount(MAVLinkInterface mav, string eventName) =>
        (typeof(MAVLinkInterface).GetField("_" + eventName, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(mav) as Delegate)?
        .GetInvocationList().Length ?? 0;

    private static (InspectorViewModel Vm, ToolsWindows Windows) Create()
    {
        var windows = new ToolsWindows();
        var services = new AppServices(new ToolsInlineDispatcher(), new ToolsDialogs(), windows);
        return (new InspectorViewModel(services), windows);
    }

    [TestMethod]
    public void ListensOnlyWhileShown()
    {
        var (vm, _) = Create();
        var mav = vm.Services.Session.Mav;
        // the library and the services listen too
        var received = HandlerCount(mav, nameof(MAVLinkInterface.OnPacketReceived));
        var sent = HandlerCount(mav, nameof(MAVLinkInterface.OnPacketSent));

        vm.OnShown();
        Assert.AreEqual(received + 1, HandlerCount(mav, nameof(MAVLinkInterface.OnPacketReceived)));
        Assert.AreEqual(sent, HandlerCount(mav, nameof(MAVLinkInterface.OnPacketSent)));

        vm.ShowGcsTraffic = true;
        Assert.AreEqual(sent + 1, HandlerCount(mav, nameof(MAVLinkInterface.OnPacketSent)));
        vm.OnShown();
        Assert.AreEqual(received + 1, HandlerCount(mav, nameof(MAVLinkInterface.OnPacketReceived)), "showing twice does not subscribe twice");

        vm.OnHidden();
        Assert.AreEqual(received, HandlerCount(mav, nameof(MAVLinkInterface.OnPacketReceived)));
        Assert.AreEqual(sent, HandlerCount(mav, nameof(MAVLinkInterface.OnPacketSent)));

        vm.OnShown();
        Assert.AreEqual(sent + 1, HandlerCount(mav, nameof(MAVLinkInterface.OnPacketSent)), "the GCS toggle is remembered");
        vm.OnHidden();
    }

    [TestMethod]
    public void RefreshesTwiceASecondAndHonoursPause()
    {
        var (vm, _) = Create();
        vm.Add(ToolsPackets.Attitude(0.1f));
        for (var i = 0; i < 9; i++)
            vm.Tick();
        Assert.AreEqual(0, vm.Vehicles.Count, "nothing before the 10th tick (2 Hz at a 20 Hz tick)");
        vm.Tick();
        Assert.AreEqual(1, vm.Vehicles.Count);
        Assert.AreEqual("1 component · 1 message type", vm.Summary);

        var roll = vm.Tree.Find(1, 1, 30)!.FindField("roll")!;
        vm.Tree.Find(1, 1, 30)!.IsExpanded = true;
        vm.IsPaused = true;
        vm.Add(ToolsPackets.Attitude(0.9f));
        vm.Refresh();
        Assert.AreEqual("0.1", roll.Value);
        vm.IsPaused = false;
        Assert.AreEqual("0.9", roll.Value);
    }

    [TestMethod]
    public void GraphNeedsANumericFieldAndOpensAWindow()
    {
        var (vm, windows) = Create();
        vm.Add(ToolsPackets.ParamValue("ARMING_CHECK", 1));
        vm.Add(ToolsPackets.Attitude(0.1f));
        vm.Refresh();

        Assert.IsFalse(vm.GraphCommand.CanExecute(null));
        vm.SelectedNode = vm.Tree.Find(1, 1, 22)!.FindField("param_id");
        Assert.IsFalse(vm.GraphCommand.CanExecute(null));
        Assert.Contains("cannot be graphed", vm.SelectionHint);

        vm.SelectedNode = vm.Tree.Find(1, 1, 30)!.FindField("roll");
        Assert.IsTrue(vm.GraphCommand.CanExecute(null));
        vm.GraphCommand.Execute(null);

        var graph = (InspectorGraphViewModel)windows.Shown.Single();
        Assert.AreEqual(30u, graph.MsgId);
        Assert.AreEqual("roll (rad)", graph.YLabel);
    }

    [TestMethod]
    public void ClearForgetsEverything()
    {
        var (vm, _) = Create();
        vm.Add(ToolsPackets.Attitude(0.1f));
        vm.Refresh();
        vm.SelectedNode = vm.Vehicles[0];
        vm.ClearCommand.Execute(null);
        Assert.AreEqual(0, vm.Vehicles.Count);
        Assert.IsNull(vm.SelectedNode);
    }
}

[TestClass]
public class ToolsInspectorGraphTests
{
    [TestMethod]
    public void SeriesKeepsTheNewestPoints()
    {
        var s = new InspectorSeries("x", 3);
        for (var i = 0; i < 5; i++)
            s.Add(i, i * 10);
        var (x, y) = s.ToArrays();
        CollectionAssert.AreEqual(new double[] { 2, 3, 4 }, x);
        CollectionAssert.AreEqual(new double[] { 20, 30, 40 }, y);

        s.Resize(2);
        CollectionAssert.AreEqual(new double[] { 3, 4 }, s.ToArrays().X);
        s.Resize(5);
        s.Add(5, 50);
        CollectionAssert.AreEqual(new double[] { 3, 4, 5 }, s.ToArrays().X);

        s.Clear();
        Assert.AreEqual(0, s.ToArrays().X.Length);
    }

    [TestMethod]
    public void GraphTakesOnlyItsMessageAndOneLinePerArrayElement()
    {
        var services = new AppServices(new ToolsInlineDispatcher(), new ToolsDialogs(), new ToolsWindows());
        var roll = InspectorFormat.FieldsOf(30).Single(f => f.Name == "roll");
        var graph = new InspectorGraphViewModel(services, 1, 1, 30, "ATTITUDE", roll);

        graph.Add(ToolsPackets.Attitude(0.5f), 1.0);
        graph.Add(ToolsPackets.Attitude(0.6f, 2), 1.5); // another system
        graph.Add(ToolsPackets.Heartbeat(), 2.0);
        graph.Add(ToolsPackets.Attitude(0.7f), 2.5);

        var series = graph.Snapshot().Single();
        Assert.AreEqual("roll", series.Name);
        CollectionAssert.AreEqual(new[] { 1.0, 2.5 }, series.X);
        Assert.AreEqual(0.7, series.Y[1], 1e-6);

        var controls = InspectorFormat.FieldsOf((uint)MAVLink.MAVLINK_MSG_ID.HIL_ACTUATOR_CONTROLS).Single(f => f.Name == "controls");
        var arrays = new InspectorGraphViewModel(services, 1, 1, (uint)MAVLink.MAVLINK_MSG_ID.HIL_ACTUATOR_CONTROLS, "HIL_ACTUATOR_CONTROLS", controls);
        arrays.Add(ToolsPackets.Make(MAVLink.MAVLINK_MSG_ID.HIL_ACTUATOR_CONTROLS, new MAVLink.mavlink_hil_actuator_controls_t { controls = new float[16] }), 0);
        var lines = arrays.Snapshot();
        Assert.AreEqual(16, lines.Count);
        Assert.AreEqual("controls[15]", lines[15].Name);

        var changed = 0;
        graph.DataChanged += (_, _) => changed++;
        graph.Tick();
        graph.Tick();
        Assert.AreEqual(1, changed);
        Assert.AreEqual("roll = 0.7", graph.Latest);
    }
}

[TestClass]
public class InspectorReplayTests
{
    /// <summary>The classic "MAVLink Inspector" button during a tlog replay used to land on the Full Parameter List.</summary>
    [TestMethod]
    public void TheInspectorIsThereWhileATlogIsLoaded()
    {
        var services = MissionPlanner.App.Tests.FlightData.FakeUi.Services(out _);
        var config = new ConfigViewModel(services);
        Assert.IsFalse(config.Show("MAVLink Inspector"), "no link and no log");

        services.Session.Mav.logplaybackfile = new BinaryReader(new MemoryStream(new byte[16]));
        try
        {
            Assert.IsTrue(config.Show("MAVLink Inspector"), "a loaded log (playing or paused) feeds the inspector");
            Assert.AreEqual("MAVLink Inspector", config.SelectedItem?.Title);
            Assert.IsFalse(config.Show("MAVFtp"), "MAVFTP still needs a live vehicle");
        }
        finally
        {
            services.Session.Mav.logplaybackfile = null;
        }

        Assert.IsFalse(config.Show("MAVLink Inspector"), "gone again once the log is closed");
    }
}
