// IDroneCanBus over ExtLibs/DroneCAN (the library Mission Planner's ConfigDroneCAN uses).
//
// Compiled only when MissionPlanner.App.Core references the library and defines DRONECAN:
//   <ProjectReference Include="..\..\ExtLibs\DroneCAN\DroneCAN.csproj" />
//   <PropertyGroup><DefineConstants>$(DefineConstants);DRONECAN</DefineConstants></PropertyGroup>
// Without them the DroneCAN page shows that the library is missing (DroneCanBusFactory.Available).
#if DRONECAN
using System.Text;
using Dc = DroneCAN.DroneCAN;
using ParamType = DroneCAN.DroneCAN.uavcan_protocol_param_Value.uavcan_protocol_param_Value_type_t;
using NumericType = DroneCAN.DroneCAN.uavcan_protocol_param_NumericValue.uavcan_protocol_param_NumericValue_type_t;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Comms;

public sealed class DroneCanLibraryBus : IDroneCanBus
{
    private readonly Dc _can = new() { SourceNode = 127 };
    private readonly HashSet<byte> _infoRequested = new();
    private bool _started;

    public byte LocalNodeId => _can.SourceNode;

    public event Action<CanNodeStatus>? NodeStatusReceived;

    public event Action<CanNodeInfo>? NodeInfoReceived;

    public event Action<CanLogMessage>? LogReceived;

    public void Start(Stream slcan)
    {
        _can.StartSLCAN(slcan);
        // StartSLCAN adds its node-list handler; ours goes after so NodeInfo is filled first
        _can.MessageReceived += OnMessage;
        // the file server answers the node's reads during a firmware update
        _can.SetupFileServer();
        _started = true;
    }

    public void Stop()
    {
        if (!_started)
            return;
        _started = false;
        _can.MessageReceived -= OnMessage;
        _can.Stop(closestream: true);
    }

    public void Dispose() => Stop();

    public void RequestNodeInfo(byte node)
    {
        var slcan = _can.PackageMessageSLCAN(node, 30, _can.TransferID++, new Dc.uavcan_protocol_GetNodeInfo_req());
        _can.WriteToStreamSLCAN(slcan);
    }

    public IReadOnlyList<CanParameter> GetParameters(byte node) =>
        _can.GetParameters(node).Select(ToParameter).ToList();

    public bool SetParameter(byte node, CanParameter parameter, double number, string text)
    {
        var type = parameter.Kind switch
        {
            CanParamKind.Integer => ParamType.UAVCAN_PROTOCOL_PARAM_VALUE_TYPE_INTEGER_VALUE,
            CanParamKind.Real => ParamType.UAVCAN_PROTOCOL_PARAM_VALUE_TYPE_REAL_VALUE,
            CanParamKind.Boolean => ParamType.UAVCAN_PROTOCOL_PARAM_VALUE_TYPE_BOOLEAN_VALUE,
            CanParamKind.String => ParamType.UAVCAN_PROTOCOL_PARAM_VALUE_TYPE_STRING_VALUE,
            _ => throw new NotSupportedException("empty parameters cannot be written"),
        };
        object value = parameter.Kind == CanParamKind.String ? text : number;
        return _can.SetParameter(node, parameter.Name, value, type);
    }

    public bool SaveParameters(byte node) => _can.SaveConfig(node);

    public bool EraseParameters(byte node) =>
        _can.ExecuteOpCode(node, (byte)Dc.uavcan_protocol_param_ExecuteOpcode_req.UAVCAN_PROTOCOL_PARAM_EXECUTEOPCODE_REQ_OPCODE_ERASE);

    public bool RestartNode(byte node) => _can.RestartNode(node);

    public void UpdateFirmware(byte node, string firmwareFile, IProgress<double> progress, CancellationToken cancel)
    {
        void OnProgress(byte id, string file, double percent)
        {
            if (id == node)
                progress.Report(percent);
        }

        void OnComplete(byte id, string file)
        {
            if (id == node)
                progress.Report(100);
        }

        _can.FileSendProgress += OnProgress;
        _can.FileSendComplete += OnComplete;
        try
        {
            _can.Update(node, _can.GetNodeName(node), 0, firmwareFile, cancel);
        }
        finally
        {
            _can.FileSendProgress -= OnProgress;
            _can.FileSendComplete -= OnComplete;
        }
    }

    private void OnMessage(global::DroneCAN.CANFrame frame, object msg, byte transferId)
    {
        var source = frame.SourceNode;
        // the library also reports what we send ourselves
        if (source == LocalNodeId)
            return;

        switch (msg)
        {
            case Dc.uavcan_protocol_NodeStatus ns:
            {
                NodeStatusReceived?.Invoke(ToStatus(source, ns));
                bool ask;
                lock (_infoRequested)
                    ask = _infoRequested.Add(source);
                if (ask && source != 0)
                    RequestNodeInfo(source);
                break;
            }
            case Dc.uavcan_protocol_GetNodeInfo_res info when frame.IsServiceMsg:
            {
                var name = Encoding.ASCII.GetString(info.name, 0, Math.Min(info.name_len, info.name.Length)).TrimEnd('\0');
                var sw = info.software_version;
                var hw = info.hardware_version;
                var swText = $"{sw.major}.{sw.minor}" + ((sw.optional_field_flags & 1) != 0 ? $" ({sw.vcs_commit:x8})" : "");
                NodeInfoReceived?.Invoke(new CanNodeInfo(source, name, swText, $"{hw.major}.{hw.minor}",
                    Convert.ToHexString(hw.unique_id ?? []), sw.image_crc, ToStatus(source, info.status)));
                break;
            }
            case Dc.uavcan_protocol_debug_LogMessage log:
                LogReceived?.Invoke(new CanLogMessage(source, log.level.value,
                    Encoding.ASCII.GetString(log.source, 0, Math.Min(log.source_len, log.source.Length)),
                    Encoding.ASCII.GetString(log.text, 0, Math.Min(log.text_len, log.text.Length))));
                break;
        }
    }

    private static CanNodeStatus ToStatus(byte node, Dc.uavcan_protocol_NodeStatus ns) =>
        new(node, (CanNodeHealth)ns.health, (CanNodeMode)ns.mode, ns.uptime_sec, ns.vendor_specific_status_code);

    private static CanParameter ToParameter(Dc.uavcan_protocol_param_GetSet_res p)
    {
        var name = Encoding.ASCII.GetString(p.name, 0, Math.Min(p.name_len, p.name.Length));
        var (kind, number, text) = Value(p.value);
        var (_, def, _) = Value(p.default_value);
        return new CanParameter(name, kind, number, text,
            p.default_value.uavcan_protocol_param_Value_type == ParamType.UAVCAN_PROTOCOL_PARAM_VALUE_TYPE_EMPTY ? null : def,
            Numeric(p.min_value), Numeric(p.max_value));
    }

    private static (CanParamKind, double, string) Value(Dc.uavcan_protocol_param_Value v) => v.uavcan_protocol_param_Value_type switch
    {
        ParamType.UAVCAN_PROTOCOL_PARAM_VALUE_TYPE_INTEGER_VALUE => (CanParamKind.Integer, v.union.integer_value, ""),
        ParamType.UAVCAN_PROTOCOL_PARAM_VALUE_TYPE_REAL_VALUE => (CanParamKind.Real, v.union.real_value, ""),
        ParamType.UAVCAN_PROTOCOL_PARAM_VALUE_TYPE_BOOLEAN_VALUE => (CanParamKind.Boolean, v.union.boolean_value, ""),
        ParamType.UAVCAN_PROTOCOL_PARAM_VALUE_TYPE_STRING_VALUE => (CanParamKind.String, 0,
            Encoding.ASCII.GetString(v.union.string_value, 0, Math.Min(v.union.string_value_len, v.union.string_value.Length))),
        _ => (CanParamKind.Empty, 0, ""),
    };

    private static double? Numeric(Dc.uavcan_protocol_param_NumericValue v) => v.uavcan_protocol_param_NumericValue_type switch
    {
        NumericType.UAVCAN_PROTOCOL_PARAM_NUMERICVALUE_TYPE_INTEGER_VALUE => v.union.integer_value,
        NumericType.UAVCAN_PROTOCOL_PARAM_NUMERICVALUE_TYPE_REAL_VALUE => v.union.real_value,
        _ => null,
    };
}
#endif
