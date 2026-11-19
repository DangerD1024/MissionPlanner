using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Comms;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Firmware;
using MissionPlanner.Comms;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

public enum CanInterface
{
    /// <summary>CAN bus 1 of the connected autopilot, forwarded over MAVLink (MAV_CMD_CAN_FORWARD).</summary>
    MavlinkCan1,

    /// <summary>CAN bus 2 of the connected autopilot, forwarded over MAVLink.</summary>
    MavlinkCan2,

    /// <summary>An SLCAN adapter (or an autopilot in SLCAN mode) on a serial port.</summary>
    Slcan,
}

public sealed record CanInterfaceOption(CanInterface Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// DroneCAN/UAVCAN (Mission Planner's ConfigDroneCAN): lists the nodes on a CAN bus — reached through
/// the autopilot's MAVLink CAN forwarding or an SLCAN serial port — with their health, mode, uptime
/// and versions; reads and writes a node's parameters, stores them, restarts the node and updates
/// its firmware. The protocol work is ExtLibs/DroneCAN behind <see cref="IDroneCanBus"/>.
/// </summary>
public sealed partial class DroneCanViewModel : PageViewModel
{
    private readonly Func<IDroneCanBus> _busFactory;
    private readonly Func<int, MavlinkCanForwarder> _forwarderFactory;
    private readonly Dictionary<byte, CanNodeRow> _byId = new();
    private IDroneCanBus? _bus;
    private MavlinkCanForwarder? _forwarder;
    private ICommsSerial? _serial;
    private CancellationTokenSource? _updateCancel;
    private int _tick;

    public DroneCanViewModel(AppServices services)
        : this(services, DroneCanBusFactory.Available, DroneCanBusFactory.Create,
            bus => new MavlinkCanForwarder(new MavlinkCanFrameTransport(services.Session), bus,
                (byte)services.Session.Vehicle.sysid, services.Session.Vehicle.compid))
    {
    }

    /// <summary>Tests pass a fake bus and a forwarder over a fake vehicle.</summary>
    internal DroneCanViewModel(AppServices services, bool libraryAvailable, Func<IDroneCanBus> busFactory,
        Func<int, MavlinkCanForwarder> forwarderFactory)
        : base("DroneCAN/UAVCAN")
    {
        Services = services;
        LibraryAvailable = libraryAvailable;
        _busFactory = busFactory;
        _forwarderFactory = forwarderFactory;
        Interfaces =
        [
            new(CanInterface.MavlinkCan1, "MAVLink CAN1 (through the autopilot)"),
            new(CanInterface.MavlinkCan2, "MAVLink CAN2 (through the autopilot)"),
            new(CanInterface.Slcan, "SLCAN serial port"),
        ];
        _selectedInterface = Interfaces[0];
        Bauds = [115200, 230400, 460800, 921600, 1000000, 1500000, 2000000];
        _selectedBaud = 115200;
        RefreshPorts();
        var last = Settings.Instance["dronecan_port"];
        // a port typed in earlier (a symlink, a pty) is kept even though the OS list lacks it
        if (last != null && !Ports.Contains(last) && File.Exists(last))
            Ports.Insert(0, last);
        _selectedPort = last != null && Ports.Contains(last) ? last : Ports.FirstOrDefault();
        if (!libraryAvailable)
            _status = "This build does not include the DroneCAN library (ExtLibs/DroneCAN), so the bus cannot be opened.";
    }

    public AppServices Services { get; }

    /// <summary>False when the app was built without ExtLibs/DroneCAN.</summary>
    public bool LibraryAvailable { get; }

    public bool LibraryMissing => !LibraryAvailable;

    public IReadOnlyList<CanInterfaceOption> Interfaces { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSlcan))]
    private CanInterfaceOption _selectedInterface;

    public bool IsSlcan => SelectedInterface.Value == CanInterface.Slcan;

    public ObservableCollection<string> Ports { get; } = new();

    public IReadOnlyList<int> Bauds { get; }

    [ObservableProperty]
    private string? _selectedPort;

    [ObservableProperty]
    private int _selectedBaud;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisconnected), nameof(ConnectText))]
    [NotifyCanExecuteChangedFor(nameof(ReadParametersCommand), nameof(RestartNodeCommand), nameof(UpdateFirmwareCommand),
        nameof(RequestInfoCommand))]
    private bool _isConnected;

    public bool IsDisconnected => !IsConnected;

    public string ConnectText => IsConnected ? "Disconnect" : "Connect";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleConnectCommand), nameof(ReadParametersCommand), nameof(WriteParametersCommand),
        nameof(SaveParametersCommand), nameof(RestartNodeCommand), nameof(UpdateFirmwareCommand), nameof(EraseParametersCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    public bool IsIdle => !IsBusy;

    [ObservableProperty]
    private string _status = "Pick how to reach the CAN bus and press Connect.";

    [ObservableProperty]
    private string _traffic = "";

    public ObservableCollection<CanNodeRow> Nodes { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedNode), nameof(SelectedNodeTitle))]
    [NotifyCanExecuteChangedFor(nameof(ReadParametersCommand), nameof(RestartNodeCommand), nameof(UpdateFirmwareCommand),
        nameof(RequestInfoCommand))]
    private CanNodeRow? _selectedNode;

    public bool HasSelectedNode => SelectedNode != null;

    public string SelectedNodeTitle => SelectedNode == null ? "No node selected" : $"Node {SelectedNode.Id} · {SelectedNode.Name}";

    /// <summary>Parameters of <see cref="ParamsNodeId"/>.</summary>
    public ObservableCollection<CanParamRow> Parameters { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasParameters), nameof(ParametersTitle))]
    [NotifyCanExecuteChangedFor(nameof(WriteParametersCommand), nameof(SaveParametersCommand), nameof(EraseParametersCommand))]
    private byte? _paramsNodeId;

    public bool HasParameters => ParamsNodeId != null;

    public string ParametersTitle => ParamsNodeId is { } id ? $"Parameters of node {id} ({Parameters.Count})" : "Parameters";

    public ObservableCollection<CanLogRow> LogMessages { get; } = new();

    [ObservableProperty]
    private double _firmwareProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotUpdating))]
    private bool _isUpdating;

    public bool IsNotUpdating => !IsUpdating;

    partial void OnSelectedPortChanged(string? value)
    {
        if (!string.IsNullOrEmpty(value))
            Settings.Instance["dronecan_port"] = value;
    }

    [RelayCommand]
    private void RefreshPorts()
    {
        var current = SelectedPort;
        Ports.Clear();
        foreach (var p in LinkFactory.GetSerialPortNames().OrderBy(p => p, StringComparer.Ordinal))
            Ports.Add(p);
        if (current != null && !Ports.Contains(current))
            Ports.Insert(0, current);
        if (current != null)
            SelectedPort = current;
    }

    private bool CanToggle() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanToggle))]
    private async Task ToggleConnectAsync()
    {
        if (IsConnected)
        {
            await DisconnectAsync("Disconnected.");
            return;
        }

        if (!LibraryAvailable)
        {
            await Services.Dialogs.ShowMessageAsync("DroneCAN",
                "This build of Mission Planner does not include the DroneCAN library (ExtLibs/DroneCAN).", MessageKind.Warning);
            return;
        }

        IsBusy = true;
        try
        {
            Stream stream;
            var choice = SelectedInterface.Value;
            if (choice == CanInterface.Slcan)
            {
                if (string.IsNullOrEmpty(SelectedPort))
                {
                    await Services.Dialogs.ShowMessageAsync("DroneCAN", "Pick the SLCAN serial port first.", MessageKind.Warning);
                    return;
                }

                var session = Services.Session;
                if (session.IsConnected && session.Link is { Kind: LinkKind.Serial } link && SikRadioViewModel.SamePort(link.Address, SelectedPort))
                {
                    await Services.Dialogs.ShowMessageAsync("DroneCAN",
                        $"{SelectedPort} carries the MAVLink connection. Use MAVLink CAN1/CAN2 instead, or disconnect the vehicle " +
                        "after switching the port to SLCAN (CAN_SLCAN_CPORT, CAN_SLCAN_SERNUM).", MessageKind.Warning);
                    return;
                }

                var serial = LinkFactory.Create(LinkSpec.Serial(SelectedPort, SelectedBaud));
                await Task.Run(serial.Open);
                _serial = serial;
                stream = serial.BaseStream;
                Status = $"Opening SLCAN on {SelectedPort}…";
            }
            else
            {
                var bus = choice == CanInterface.MavlinkCan1 ? 1 : 2;
                if (!Services.Session.IsConnected)
                {
                    await Services.Dialogs.ShowMessageAsync("DroneCAN", "Connect to the vehicle first: its CAN bus is forwarded over MAVLink.",
                        MessageKind.Warning);
                    return;
                }

                var driver = $"CAN_P{bus}_DRIVER";
                if (Services.Params.Has(driver) && Services.Params.Get(driver) == 0 &&
                    !await Services.Dialogs.ConfirmAsync("DroneCAN",
                        $"{driver} is 0, so CAN bus {bus} is off on the autopilot. Set it to 1 (and reboot) to see nodes. Connect anyway?",
                        "Connect"))
                    return;

                var forwarder = _forwarderFactory(bus);
                forwarder.Start();
                _forwarder = forwarder;
                stream = forwarder.Stream;
                Status = $"Forwarding CAN{bus} over MAVLink…";
            }

            Nodes.Clear();
            _byId.Clear();
            Parameters.Clear();
            ParamsNodeId = null;
            LogMessages.Clear();

            var can = _busFactory();
            _bus = can;
            can.NodeStatusReceived += OnNodeStatus;
            can.NodeInfoReceived += OnNodeInfo;
            can.LogReceived += OnLog;
            await Task.Run(() => can.Start(stream));
            IsConnected = true;
            Status = choice == CanInterface.Slcan
                ? $"Connected to SLCAN on {SelectedPort}. Nodes appear as they report."
                : "Connected through the autopilot. Nodes appear as they report.";
        }
        catch (Exception ex)
        {
            await CleanUpAsync();
            Status = "Connect failed: " + ex.Message;
            await Services.Dialogs.ShowMessageAsync("DroneCAN", "Could not open the CAN bus.\n\n" + ex.Message, MessageKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool HasNode() => IsConnected && SelectedNode != null && !IsBusy;

    [RelayCommand(CanExecute = nameof(HasNode))]
    private async Task ReadParametersAsync()
    {
        var node = SelectedNode!.Id;
        var bus = _bus!;
        IsBusy = true;
        Status = $"Reading parameters of node {node}…";
        try
        {
            var list = await Task.Run(() => bus.GetParameters(node));
            Parameters.Clear();
            foreach (var p in list.OrderBy(p => p.Name, StringComparer.Ordinal))
                Parameters.Add(new CanParamRow(p));
            ParamsNodeId = node;
            OnPropertyChanged(nameof(ParametersTitle));
            Status = list.Count == 0 ? $"Node {node} returned no parameters." : $"Read {list.Count} parameters from node {node}.";
        }
        catch (Exception ex)
        {
            Status = "Reading parameters failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool HasParams() => IsConnected && ParamsNodeId != null && !IsBusy;

    [RelayCommand(CanExecute = nameof(HasParams))]
    private async Task WriteParametersAsync()
    {
        var node = ParamsNodeId!.Value;
        var changed = Parameters.Where(p => p.IsChanged).ToList();
        if (changed.Count == 0)
        {
            Status = "No parameter changed.";
            return;
        }

        var invalid = changed.Where(p => p.IsInvalid).Select(p => p.Name).ToList();
        if (invalid.Count > 0)
        {
            await Services.Dialogs.ShowMessageAsync("DroneCAN", "Not written, these values do not fit their type or range:\n\n" + string.Join("\n", invalid),
                MessageKind.Warning);
            return;
        }

        if (!await Services.Dialogs.ConfirmAsync("DroneCAN", L.Format("Write {0} parameter(s) to node {1}?", changed.Count, node), "Write"))
            return;

        var bus = _bus!;
        IsBusy = true;
        var failed = new List<string>();
        try
        {
            foreach (var row in changed)
            {
                row.TryParse(out var number, out var text);
                Status = $"Writing {row.Name}…";
                var ok = await Task.Run(() => bus.SetParameter(node, row.Parameter, number, text));
                if (ok)
                    row.Commit(number, text);
                else
                    failed.Add(row.Name);
            }

            Status = failed.Count == 0
                ? $"Wrote {changed.Count} parameter(s). Use Save to flash to keep them after a restart."
                : "Not acknowledged: " + string.Join(", ", failed);
        }
        catch (Exception ex)
        {
            Status = "Writing failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(HasParams))]
    private async Task SaveParametersAsync()
    {
        var node = ParamsNodeId!.Value;
        await RunNodeOperation($"Storing the parameters of node {node}…", bus => bus.SaveParameters(node),
            $"Node {node} stored its parameters.", $"Node {node} did not confirm the save.");
    }

    [RelayCommand(CanExecute = nameof(HasParams))]
    private async Task EraseParametersAsync()
    {
        var node = ParamsNodeId!.Value;
        if (!await Services.Dialogs.ConfirmAsync("DroneCAN",
                $"Reset all parameters of node {node} to their defaults? They apply after the node restarts.", "Reset"))
            return;
        await RunNodeOperation($"Erasing the parameters of node {node}…", bus => bus.EraseParameters(node),
            $"Node {node} erased its parameters; restart it to load the defaults.", $"Node {node} did not confirm the erase.");
    }

    [RelayCommand(CanExecute = nameof(HasNode))]
    private async Task RestartNodeAsync()
    {
        var node = SelectedNode!.Id;
        if (!await Services.Dialogs.ConfirmAsync("DroneCAN", L.Format("Restart node {0} ({1})?", node, SelectedNode.Name), "Restart"))
            return;
        await RunNodeOperation($"Restarting node {node}…", bus => bus.RestartNode(node),
            $"Node {node} is restarting.", $"Node {node} did not confirm the restart.");
    }

    [RelayCommand(CanExecute = nameof(HasNode))]
    private void RequestInfo()
    {
        try
        {
            _bus?.RequestNodeInfo(SelectedNode!.Id);
        }
        catch (Exception ex)
        {
            Status = "Request failed: " + ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(HasNode))]
    private async Task UpdateFirmwareAsync()
    {
        var row = SelectedNode!;
        var file = await Services.Dialogs.OpenFileAsync($"Firmware for node {row.Id} ({row.Name})",
            new FileFilter("Firmware", "bin", "apj"));
        if (file == null)
            return;
        if (!await Services.Dialogs.ConfirmAsync("DroneCAN",
                $"Update node {row.Id} ({row.Name}) with {Path.GetFileName(file)}? Do not power off the node during the update.", "Update"))
            return;

        var bus = _bus!;
        IsBusy = true;
        IsUpdating = true;
        FirmwareProgress = 0;
        _updateCancel = new CancellationTokenSource();
        var token = _updateCancel.Token;
        var progress = new Progress<double>(p => FirmwareProgress = p);
        string? temp = null;
        try
        {
            var image = file;
            if (file.EndsWith(".apj", StringComparison.OrdinalIgnoreCase))
            {
                // the node reads a plain binary: unpack the APJ as Mission Planner does
                temp = Path.Combine(Path.GetTempPath(), $"dronecan-{row.Id}-{Guid.NewGuid():N}.bin");
                var apj = await Task.Run(() => ApjFirmware.Load(file));
                await File.WriteAllBytesAsync(temp, apj.Image, token);
                image = temp;
            }

            Status = $"Updating node {row.Id}: the node fetches the file from us…";
            await Task.Run(() => bus.UpdateFirmware(row.Id, image, progress, token), CancellationToken.None);
            Status = token.IsCancellationRequested ? "Firmware update cancelled." : $"Node {row.Id} finished the update.";
        }
        catch (Exception ex)
        {
            Status = "Firmware update failed: " + ex.Message;
            await Services.Dialogs.ShowMessageAsync("DroneCAN", "Firmware update failed.\n\n" + ex.Message, MessageKind.Error);
        }
        finally
        {
            IsUpdating = false;
            IsBusy = false;
            _updateCancel.Dispose();
            _updateCancel = null;
            if (temp != null)
                TryDelete(temp);
        }
    }

    [RelayCommand]
    private void CancelUpdate() => _updateCancel?.Cancel();

    [RelayCommand]
    private void ClearLog() => LogMessages.Clear();

    public override void Tick()
    {
        if (++_tick % 20 != 0)
            return;

        // NodeStatus comes at least once a second; 3 s without one means the node is gone (OFFLINE_TIMEOUT)
        var now = Environment.TickCount64;
        foreach (var row in Nodes)
        {
            if (!row.IsStale && now - row.LastSeen > 3000)
                row.IsStale = true;
        }

        if (_forwarder is { } f)
        {
            var result = f.ForwardResult is { } r && r != MAVLink.MAV_RESULT.ACCEPTED ? $" · autopilot answered CAN_FORWARD with {r}" : "";
            Traffic = $"CAN{f.Bus} frames in {f.FramesFromVehicle} · out {f.FramesToVehicle}{result}";
        }

        if (IsConnected && _forwarder != null && !Services.Session.IsConnected)
            _ = DisconnectAsync("The vehicle link closed.");
    }

    public override void OnHidden()
    {
        // as Mission Planner: leaving the page closes the bus (but never in the middle of a firmware update)
        if (IsConnected && !IsUpdating)
            _ = DisconnectAsync("Disconnected when the page was left.");
    }

    private async Task RunNodeOperation(string doing, Func<IDroneCanBus, bool> operation, string ok, string failed)
    {
        var bus = _bus!;
        IsBusy = true;
        Status = doing;
        try
        {
            Status = await Task.Run(() => operation(bus)) ? ok : failed;
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnNodeStatus(CanNodeStatus status) => Services.Dispatcher.Post(() =>
    {
        if (!_byId.TryGetValue(status.NodeId, out var row))
        {
            row = new CanNodeRow(status.NodeId);
            _byId[status.NodeId] = row;
            var index = 0;
            while (index < Nodes.Count && Nodes[index].Id < status.NodeId)
                index++;
            Nodes.Insert(index, row);
            SelectedNode ??= row;
        }

        row.Apply(status, Environment.TickCount64);
    });

    private void OnNodeInfo(CanNodeInfo info) => Services.Dispatcher.Post(() =>
    {
        if (!_byId.TryGetValue(info.NodeId, out var row))
        {
            OnNodeStatus(info.Status ?? new CanNodeStatus(info.NodeId, CanNodeHealth.Ok, CanNodeMode.Operational, 0, 0));
            if (!_byId.TryGetValue(info.NodeId, out row))
                return;
        }

        row.Apply(info);
        if (row == SelectedNode)
            OnPropertyChanged(nameof(SelectedNodeTitle));
    });

    private void OnLog(CanLogMessage log) => Services.Dispatcher.Post(() =>
    {
        var level = log.Level switch
        {
            0 => "DEBUG",
            1 => "INFO",
            2 => "WARNING",
            3 => "ERROR",
            _ => log.Level.ToString(),
        };
        LogMessages.Insert(0, new CanLogRow(DateTime.Now, log.NodeId, level, log.Source, log.Text));
        while (LogMessages.Count > 100)
            LogMessages.RemoveAt(LogMessages.Count - 1);
    });

    private async Task DisconnectAsync(string status)
    {
        _updateCancel?.Cancel();
        await CleanUpAsync();
        IsConnected = false;
        Status = status;
        foreach (var row in Nodes)
            row.IsStale = true;
    }

    private async Task CleanUpAsync()
    {
        var bus = _bus;
        var forwarder = _forwarder;
        var serial = _serial;
        _bus = null;
        _forwarder = null;
        _serial = null;
        await Task.Run(() =>
        {
            if (bus != null)
            {
                bus.NodeStatusReceived -= OnNodeStatus;
                bus.NodeInfoReceived -= OnNodeInfo;
                bus.LogReceived -= OnLog;
                try
                {
                    bus.Dispose();
                }
                catch
                {
                    // closing a dead stream
                }
            }

            forwarder?.Dispose();
            try
            {
                serial?.Close();
            }
            catch
            {
                // unplugged
            }
        });
    }

    /// <summary>Tests: use <paramref name="bus"/> as if a connect had opened it.</summary>
    internal void AttachForTest(IDroneCanBus bus)
    {
        _bus = bus;
        bus.NodeStatusReceived += OnNodeStatus;
        bus.NodeInfoReceived += OnNodeInfo;
        bus.LogReceived += OnLog;
        IsConnected = true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // a temp file left behind is harmless
        }
    }
}
