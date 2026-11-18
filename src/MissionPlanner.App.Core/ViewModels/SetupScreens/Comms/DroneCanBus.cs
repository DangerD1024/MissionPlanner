namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Comms;

public enum CanNodeHealth : byte
{
    Ok = 0,
    Warning = 1,
    Error = 2,
    Critical = 3,
}

public enum CanNodeMode : byte
{
    Operational = 0,
    Initialization = 1,
    Maintenance = 2,
    SoftwareUpdate = 3,
    Offline = 7,
}

/// <summary>A uavcan.protocol.NodeStatus broadcast.</summary>
public sealed record CanNodeStatus(byte NodeId, CanNodeHealth Health, CanNodeMode Mode, uint UptimeSec, ushort VendorStatus);

/// <summary>A uavcan.protocol.GetNodeInfo reply.</summary>
public sealed record CanNodeInfo(byte NodeId, string Name, string SoftwareVersion, string HardwareVersion, string UniqueId,
    ulong ImageCrc, CanNodeStatus? Status = null);

/// <summary>A uavcan.protocol.debug.LogMessage.</summary>
public sealed record CanLogMessage(byte NodeId, int Level, string Source, string Text);

public enum CanParamKind
{
    Empty,
    Integer,
    Real,
    Boolean,
    String,
}

/// <summary>A node parameter (uavcan.protocol.param.GetSet reply): numbers as double, strings as text.</summary>
public sealed record CanParameter(string Name, CanParamKind Kind, double Number, string Text = "",
    double? Default = null, double? Min = null, double? Max = null)
{
    public string ValueText => Kind switch
    {
        CanParamKind.String => Text,
        CanParamKind.Boolean => Number != 0 ? "1" : "0",
        CanParamKind.Integer => ((long)Number).ToString(System.Globalization.CultureInfo.InvariantCulture),
        CanParamKind.Real => ((float)Number).ToString("G7", System.Globalization.CultureInfo.InvariantCulture),
        _ => "",
    };
}

/// <summary>
/// A DroneCAN bus as the page uses it: implemented over ExtLibs/DroneCAN (<c>DroneCanLibraryBus</c>),
/// faked in tests. Events come on worker threads; the request methods block, so call them off the UI thread.
/// </summary>
public interface IDroneCanBus : IDisposable
{
    /// <summary>Our node id on the bus (the library's default, 127).</summary>
    byte LocalNodeId { get; }

    event Action<CanNodeStatus>? NodeStatusReceived;

    event Action<CanNodeInfo>? NodeInfoReceived;

    event Action<CanLogMessage>? LogReceived;

    /// <summary>Starts SLCAN on the stream (an adapter's serial stream or the MAVLink bridge); blocks for the handshake.</summary>
    void Start(Stream slcan);

    void Stop();

    /// <summary>Asks a node for its GetNodeInfo (name, versions); the reply comes as <see cref="NodeInfoReceived"/>.</summary>
    void RequestNodeInfo(byte node);

    /// <summary>Reads every parameter of a node (GetSet by index until the empty reply).</summary>
    IReadOnlyList<CanParameter> GetParameters(byte node);

    /// <summary>Writes one parameter (as its own type); true when the node echoed it back.</summary>
    bool SetParameter(byte node, CanParameter parameter, double number, string text);

    /// <summary>ExecuteOpcode SAVE: store the node's parameters in its flash.</summary>
    bool SaveParameters(byte node);

    /// <summary>ExecuteOpcode ERASE: back to the node's defaults (after a restart).</summary>
    bool EraseParameters(byte node);

    bool RestartNode(byte node);

    /// <summary>Serves <paramref name="firmwareFile"/> to the node through BeginFirmwareUpdate and file reads.</summary>
    void UpdateFirmware(byte node, string firmwareFile, IProgress<double> progress, CancellationToken cancel);
}

/// <summary>Where the page gets its bus from.</summary>
public static class DroneCanBusFactory
{
    /// <summary>False while MissionPlanner.App.Core does not reference ExtLibs/DroneCAN (see DroneCanLibraryBus.cs).</summary>
    public static bool Available =>
#if DRONECAN
        true;
#else
        false;
#endif

    public static IDroneCanBus Create() =>
#if DRONECAN
        new DroneCanLibraryBus();
#else
        throw new NotSupportedException(
            "This build does not include the DroneCAN library (ExtLibs/DroneCAN is not referenced by MissionPlanner.App.Core).");
#endif
}
