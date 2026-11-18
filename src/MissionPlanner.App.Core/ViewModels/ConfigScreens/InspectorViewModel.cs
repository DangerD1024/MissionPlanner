using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>
/// MAVLink Inspector: every message type on the link per vehicle component, with its rate,
/// bandwidth and live field values (Mission Planner's MAVLinkInspector).
/// </summary>
public sealed partial class InspectorViewModel : PageViewModel
{
    /// <summary>The tree refreshes twice a second (the page is ticked at 20 Hz).</summary>
    private const int TicksPerRefresh = 10;

    private readonly PacketInspector<MAVLink.MAVLinkMessage> _inspector = new();
    private bool _subscribed;
    private bool _sentSubscribed;
    private int _tick;

    public InspectorViewModel(AppServices services)
        : base("MAVLink Inspector")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public InspectorTreeModel Tree { get; } = new();

    public ObservableCollection<InspectorVehicleNode> Vehicles => Tree.Vehicles;

    [ObservableProperty]
    private string _filter = "";

    /// <summary>Also inspect what this ground station sends (shows as its own system).</summary>
    [ObservableProperty]
    private bool _showGcsTraffic;

    /// <summary>Freezes the values on screen; packets are still counted.</summary>
    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GraphCommand))]
    [NotifyPropertyChangedFor(nameof(SelectedField), nameof(SelectionHint))]
    private InspectorNode? _selectedNode;

    [ObservableProperty]
    private string _summary = "Waiting for packets…";

    public InspectorFieldNode? SelectedField => SelectedNode as InspectorFieldNode;

    public string SelectionHint => SelectedNode switch
    {
        InspectorFieldNode { CanGraph: true } f => $"{f.Message.Name}.{f.Name}" + (f.Description.Length > 0 ? " — " + f.Description : ""),
        InspectorFieldNode f => $"{f.Message.Name}.{f.Name} is text and cannot be graphed",
        InspectorMessageNode m => $"{m.Name} (#{m.MsgId}) from system {m.Vehicle.SysId}, component {m.Vehicle.CompId}",
        _ => "Select a numeric field to graph it.",
    };

    /// <summary>Messages fed to the inspector (for tests and the packet hooks).</summary>
    internal void Add(MAVLink.MAVLinkMessage message)
    {
        if (message.Length == 0)
            return;
        _inspector.Add(message.sysid, message.compid, message.msgid, message, message.Length);
    }

    public override void OnShown()
    {
        Subscribe();
        Refresh();
    }

    public override void OnHidden() => Unsubscribe();

    public override void Tick()
    {
        if (++_tick % TicksPerRefresh == 0)
            Refresh();
    }

    partial void OnFilterChanged(string value) => Tree.Filter = value;

    partial void OnShowGcsTrafficChanged(bool value)
    {
        if (_subscribed)
            SubscribeSent(value);
    }

    partial void OnIsPausedChanged(bool value)
    {
        if (!value)
            Refresh();
    }

    /// <summary>Merges the inspector's latest packets into the tree.</summary>
    public void Refresh()
    {
        if (IsPaused)
            return;
        Tree.Update(InspectorSampler.Sample(_inspector), (sysid, compid) => InspectorSampler.SafeBps(_inspector, sysid, compid));
        var vehicles = Tree.AllVehicles.Count;
        Summary = vehicles == 0
            ? (Services.Session.IsConnected ? "Waiting for packets…" : "Not connected.")
            : $"{vehicles} component{(vehicles == 1 ? "" : "s")} · {Tree.MessageCount} message type{(Tree.MessageCount == 1 ? "" : "s")}";
    }

    [RelayCommand]
    private void Clear()
    {
        _inspector.Clear();
        Tree.Clear();
        SelectedNode = null;
        Refresh();
    }

    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var v in Tree.AllVehicles)
        {
            foreach (var m in v.Messages.Values)
                m.IsExpanded = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanGraph))]
    private void Graph()
    {
        if (SelectedNode is not InspectorFieldNode { CanGraph: true } field)
            return;
        var m = field.Message;
        var graph = new InspectorGraphViewModel(Services, m.Vehicle.SysId, m.Vehicle.CompId, m.MsgId, m.Name, field.Field);
        Services.Windows.Show(graph, 820, 480);
    }

    private bool CanGraph() => SelectedNode is InspectorFieldNode { CanGraph: true };

    private void OnPacket(object? sender, MAVLink.MAVLinkMessage message) => Add(message);

    private void Subscribe()
    {
        if (_subscribed)
            return;
        Services.Session.Mav.OnPacketReceived += OnPacket;
        _subscribed = true;
        SubscribeSent(ShowGcsTraffic);
    }

    private void SubscribeSent(bool on)
    {
        if (on == _sentSubscribed)
            return;
        if (on)
            Services.Session.Mav.OnPacketSent += OnPacket;
        else
            Services.Session.Mav.OnPacketSent -= OnPacket;
        _sentSubscribed = on;
    }

    private void Unsubscribe()
    {
        if (!_subscribed)
            return;
        Services.Session.Mav.OnPacketReceived -= OnPacket;
        SubscribeSent(false);
        _subscribed = false;
    }
}

/// <summary>Reads a <see cref="PacketInspector{T}"/> into tree samples.</summary>
public static class InspectorSampler
{
    public static List<InspectorSample> Sample(PacketInspector<MAVLink.MAVLinkMessage> inspector)
    {
        var samples = new List<InspectorSample>();
        foreach (var m in inspector.GetPacketMessages())
        {
            if (m == null)
                continue;
            double rate, bps;
            try
            {
                // the inspector's rate tables are written by the link thread
                rate = inspector.SeenRate(m.sysid, m.compid, m.msgid);
                bps = inspector.SeenBps(m.sysid, m.compid, m.msgid);
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or ArgumentException)
            {
                rate = 0;
                bps = 0;
            }

            samples.Add(new InspectorSample(m.sysid, m.compid, m.msgid, m.msgtypename, m.data, rate, bps));
        }

        return samples;
    }

    public static double SafeBps(PacketInspector<MAVLink.MAVLinkMessage> inspector, uint sysid, byte compid)
    {
        try
        {
            return inspector.SeenBps(sysid, compid);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            return 0;
        }
    }
}
