using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>The latest copy of one message type from one vehicle component, with its traffic.</summary>
public readonly record struct InspectorSample(uint SysId, byte CompId, uint MsgId, string? Name, object? Data, double RateHz, double BytesPerSecond);

/// <summary>A row of the inspector tree. Nodes are kept and updated in place, so expansion survives refreshes.</summary>
public abstract partial class InspectorNode : ObservableObject
{
    public ObservableCollection<InspectorNode> Children { get; } = new();

    [ObservableProperty]
    private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value) => OnExpandedChanged(value);

    protected virtual void OnExpandedChanged(bool expanded)
    {
    }
}

/// <summary>A vehicle component (sysid/compid) on the link.</summary>
public sealed partial class InspectorVehicleNode : InspectorNode
{
    internal readonly Dictionary<uint, InspectorMessageNode> Messages = new();

    public InspectorVehicleNode(uint sysId, byte compId)
    {
        SysId = sysId;
        CompId = compId;
        var component = Enum.IsDefined(typeof(MAVLink.MAV_COMPONENT), (int)compId)
            ? ((MAVLink.MAV_COMPONENT)compId).ToString().Replace("MAV_COMP_ID_", "")
            : "";
        Title = $"System {sysId} · Component {compId}" + (component.Length > 0 ? " " + component : "");
        IsExpanded = true;
    }

    public uint SysId { get; }

    public byte CompId { get; }

    public string Title { get; }

    /// <summary>"23 messages · 4.2 kB/s".</summary>
    [ObservableProperty]
    private string _trafficText = "";
}

/// <summary>A message type: name, rate, bandwidth and id; its fields are the children.</summary>
public sealed partial class InspectorMessageNode : InspectorNode
{
    private bool _fieldsBuilt;

    public InspectorMessageNode(InspectorVehicleNode vehicle, uint msgId, string? name)
    {
        Vehicle = vehicle;
        MsgId = msgId;
        Name = string.IsNullOrEmpty(name) ? "UNKNOWN_" + msgId : name;
        IdText = "#" + msgId;
    }

    public InspectorVehicleNode Vehicle { get; }

    public uint MsgId { get; }

    public string Name { get; }

    public string IdText { get; }

    /// <summary>The decoded struct last received.</summary>
    public object? Data { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RateText))]
    private double _rateHz;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BandwidthText))]
    private double _bytesPerSecond;

    public string RateText => InspectorFormat.FormatRate(RateHz);

    public string BandwidthText => InspectorFormat.FormatBytesPerSecond(BytesPerSecond);

    /// <summary>Takes a new sample. Field values are only formatted while the message is expanded.</summary>
    public void Update(double rateHz, double bytesPerSecond, object? data)
    {
        // rates are shown to 0.1 Hz / 1 B/s; skip change notifications below that
        if (Math.Abs(RateHz - rateHz) >= 0.05)
            RateHz = rateHz;
        if (Math.Abs(BytesPerSecond - bytesPerSecond) >= 0.5)
            BytesPerSecond = bytesPerSecond;
        Data = data;
        if (!_fieldsBuilt || IsExpanded)
            RefreshFields();
    }

    protected override void OnExpandedChanged(bool expanded)
    {
        if (expanded)
            RefreshFields();
    }

    /// <summary>Creates the field rows the first time, then updates their values.</summary>
    public void RefreshFields()
    {
        if (!_fieldsBuilt)
        {
            if (Data == null)
                return;
            foreach (var field in InspectorFormat.FieldsOf(Data.GetType()))
                Children.Add(new InspectorFieldNode(this, field));
            _fieldsBuilt = true;
        }

        foreach (var child in Children)
        {
            if (child is InspectorFieldNode f)
                f.Value = InspectorFormat.FormatField(f.Field, Data);
        }
    }

    public InspectorFieldNode? FindField(string name) =>
        Children.OfType<InspectorFieldNode>().FirstOrDefault(f => f.Name == name);
}

/// <summary>One field of a message: name = value, with its MAVLink type and units.</summary>
public sealed partial class InspectorFieldNode : InspectorNode
{
    public InspectorFieldNode(InspectorMessageNode message, InspectorField field)
    {
        Message = message;
        Field = field;
    }

    public InspectorMessageNode Message { get; }

    public InspectorField Field { get; }

    public string Name => Field.Name;

    public string TypeText => Field.Units.Length > 0 ? Field.TypeName + " · " + Field.Units : Field.TypeName;

    public string Description => Field.Description;

    public bool CanGraph => Field.IsNumeric;

    [ObservableProperty]
    private string _value = "";
}

/// <summary>
/// The inspector tree: vehicle → message → field. <see cref="Update"/> merges new samples into
/// the existing nodes (adding what is new, in sorted position) instead of rebuilding, and
/// <see cref="Filter"/> hides messages whose name does not match.
/// </summary>
public sealed class InspectorTreeModel
{
    private readonly Dictionary<(uint, byte), InspectorVehicleNode> _vehicles = new();
    private string _filter = "";

    /// <summary>The vehicles shown (those with a message matching the filter).</summary>
    public ObservableCollection<InspectorVehicleNode> Vehicles { get; } = new();

    public IReadOnlyCollection<InspectorVehicleNode> AllVehicles => _vehicles.Values;

    public int MessageCount => _vehicles.Values.Sum(v => v.Messages.Count);

    /// <summary>Part of a message name (case-insensitive) or a message id; empty shows everything.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            value = value?.Trim() ?? "";
            if (value == _filter)
                return;
            _filter = value;
            foreach (var v in _vehicles.Values)
                SyncMessages(v);
            SyncVehicles();
        }
    }

    public bool Matches(InspectorMessageNode message)
    {
        if (_filter.Length == 0)
            return true;
        if (message.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase))
            return true;
        var id = _filter.TrimStart('#');
        return id.Length > 0 && uint.TryParse(id, out var msgid) && msgid == message.MsgId;
    }

    /// <summary>Merges samples into the tree. <paramref name="vehicleBytesPerSecond"/> gives a component's total traffic.</summary>
    public void Update(IEnumerable<InspectorSample> samples, Func<uint, byte, double>? vehicleBytesPerSecond = null)
    {
        var changed = new HashSet<InspectorVehicleNode>();
        foreach (var s in samples)
        {
            if (!_vehicles.TryGetValue((s.SysId, s.CompId), out var vehicle))
            {
                vehicle = new InspectorVehicleNode(s.SysId, s.CompId);
                _vehicles[(s.SysId, s.CompId)] = vehicle;
            }

            if (!vehicle.Messages.TryGetValue(s.MsgId, out var message))
            {
                message = new InspectorMessageNode(vehicle, s.MsgId, s.Name);
                vehicle.Messages[s.MsgId] = message;
                changed.Add(vehicle);
            }

            message.Update(s.RateHz, s.BytesPerSecond, s.Data);
        }

        foreach (var v in changed)
            SyncMessages(v);
        if (changed.Count > 0)
            SyncVehicles();

        foreach (var v in _vehicles.Values)
        {
            var bps = vehicleBytesPerSecond?.Invoke(v.SysId, v.CompId) ?? v.Messages.Values.Sum(m => m.BytesPerSecond);
            v.TrafficText = $"{v.Messages.Count} message{(v.Messages.Count == 1 ? "" : "s")} · {InspectorFormat.FormatBytesPerSecond(bps)}";
        }
    }

    public void Clear()
    {
        _vehicles.Clear();
        Vehicles.Clear();
    }

    public InspectorMessageNode? Find(uint sysid, byte compid, uint msgid) =>
        _vehicles.TryGetValue((sysid, compid), out var v) && v.Messages.TryGetValue(msgid, out var m) ? m : null;

    private void SyncMessages(InspectorVehicleNode vehicle)
    {
        var desired = vehicle.Messages.Values.Where(Matches)
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.MsgId)
            .Cast<InspectorNode>()
            .ToList();
        SyncOrdered(vehicle.Children, desired);
    }

    private void SyncVehicles()
    {
        var desired = _vehicles.Values
            .Where(v => _filter.Length == 0 || v.Children.Count > 0)
            .OrderBy(v => v.SysId)
            .ThenBy(v => v.CompId)
            .ToList();
        SyncOrdered(Vehicles, desired);
    }

    /// <summary>
    /// Makes <paramref name="target"/> equal to <paramref name="desired"/> with moves, inserts and
    /// removes, so items that stay keep their place in the view.
    /// </summary>
    internal static void SyncOrdered<T>(ObservableCollection<T> target, IReadOnlyList<T> desired) where T : class
    {
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], desired[i]))
                continue;
            var at = -1;
            for (var j = i + 1; j < target.Count; j++)
            {
                if (ReferenceEquals(target[j], desired[i]))
                {
                    at = j;
                    break;
                }
            }

            if (at >= 0)
                target.Move(at, i);
            else
                target.Insert(i, desired[i]);
        }

        while (target.Count > desired.Count)
            target.RemoveAt(target.Count - 1);
    }
}
