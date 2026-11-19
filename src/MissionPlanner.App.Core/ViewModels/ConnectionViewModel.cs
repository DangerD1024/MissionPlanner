using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.Session;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels;

public sealed record VehicleOption(byte SysId, byte CompId, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Top-right connect controls: port, baud, vehicle and the connect button.</summary>
public sealed partial class ConnectionViewModel : ObservableObject
{
    public const string Tcp = "TCP";
    public const string Udp = "UDP";
    public const string UdpClient = "UDPCl";

    private readonly AppServices _services;
    private CancellationTokenSource? _connectCancel;

    public ConnectionViewModel(AppServices services)
    {
        _services = services;
        Bauds = [4800, 9600, 14400, 19200, 28800, 38400, 57600, 115200, 230400, 460800, 921600, 1500000, 2000000];

        _selectedBaud = int.TryParse(Settings.Instance["baudrate"], out var b) ? b : 115200;
        RefreshPorts();
        var last = Settings.Instance["comport"];
        _selectedPort = !string.IsNullOrEmpty(last) && Ports.Contains(last) ? last : Ports.FirstOrDefault(p => p.StartsWith('/') || p.StartsWith("COM")) ?? Tcp;

        _auto = new AutoConnectService(() => services.Session.State == LinkState.Disconnected);
        _auto.StatusChanged += s => services.Dispatcher.Post(() => AutoConnectStatus = s);
        _auto.Found += (spec, label) => services.Dispatcher.Post(() => _ = AutoConnectToAsync(spec, label));

        services.Session.StateChanged += (_, _) => services.Dispatcher.Post(OnStateChanged);
        services.Session.LinkLost += (_, reason) => services.Dispatcher.Post(() =>
        {
            if (AutoConnect)
            {
                // a pulled cable is expected with auto-connect: keep looking instead of a dialog
                AutoConnectStatus = "Link lost: " + reason;
                _auto.Resume();
            }
            else
            {
                _ = services.Dialogs.ShowMessageAsync("Connection lost", reason, MessageKind.Warning);
            }
        });

        _autoConnect = Settings.Instance.GetBoolean(AutoConnectKey, false);
        if (_autoConnect)
            _auto.Start();
    }

    public const string AutoConnectKey = "next_autoconnect";

    private readonly AutoConnectService _auto;

    /// <summary>Connect by itself to any vehicle found on USB serial or UDP 14550.</summary>
    [ObservableProperty]
    private bool _autoConnect;

    [ObservableProperty]
    private string _autoConnectStatus = "";

    partial void OnAutoConnectChanged(bool value)
    {
        Settings.Instance[AutoConnectKey] = value.ToString();
        Settings.Instance.Save();
        if (value)
            _auto.Start();
        else
            _auto.Stop();
    }

    private async Task AutoConnectToAsync(LinkSpec spec, string label)
    {
        if (!AutoConnect || State != LinkState.Disconnected)
        {
            _auto.Resume();
            return;
        }

        // show what was found in the port/baud boxes, as if the user had picked it
        if (spec.Kind == LinkKind.Serial)
        {
            if (!Ports.Contains(spec.Address))
                Ports.Insert(0, spec.Address);
            SelectedPort = spec.Address;
            SelectedBaud = spec.Value;
        }
        else
        {
            SelectedPort = Udp;
        }

        var ok = await ConnectSpecAsync(spec, quiet: true);
        _auto.Resume(ok ? null : label);
    }

    public ObservableCollection<string> Ports { get; } = new();

    public IReadOnlyList<int> Bauds { get; }

    public ObservableCollection<VehicleOption> Vehicles { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSerial))]
    private string _selectedPort;

    [ObservableProperty]
    private int _selectedBaud;

    [ObservableProperty]
    private VehicleOption? _selectedVehicle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(IsBusy), nameof(ConnectText), nameof(CanEditLink), nameof(ShowVehicles))]
    private LinkState _state;

    /// <summary>Progress text while connecting (heartbeat wait, parameter download).</summary>
    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    private int _progressPercent = -1;

    public bool IsSerial => SelectedPort is not (Tcp or Udp or UdpClient);

    public bool IsConnected => State == LinkState.Connected;

    /// <summary>Connected but nothing heard from the vehicle for a while.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVehicles))]
    private bool _isStale;

    [ObservableProperty]
    private string _staleText = "";

    public bool ShowVehicles => IsConnected && !IsStale;

    /// <summary>Called by the shell's UI tick.</summary>
    public void Tick()
    {
        var s = _services.Session;
        IsStale = s.IsReconnecting || s.IsLinkStale;
        var secs = (int)s.SinceLastPacket.TotalSeconds;
        StaleText = s.IsReconnecting ? $"Reconnecting… no data for {secs} s"
            : IsStale ? $"No data from the vehicle for {secs} s" : "";
    }

    public bool IsBusy => State == LinkState.Connecting;

    public bool CanEditLink => State == LinkState.Disconnected;

    public string ConnectText => State switch
    {
        LinkState.Connected => "DISCONNECT",
        LinkState.Connecting => "CANCEL",
        _ => "CONNECT",
    };

    /// <summary>Raised on the UI thread after each successful connect.</summary>
    public event EventHandler? Connected;

    [RelayCommand]
    private void RefreshPorts()
    {
        var current = SelectedPort;
        Ports.Clear();
        foreach (var p in LinkFactory.GetSerialPortNames().OrderBy(p => p, StringComparer.Ordinal))
            Ports.Add(p);
        Ports.Add(Tcp);
        Ports.Add(Udp);
        Ports.Add(UdpClient);
        if (current != null && Ports.Contains(current))
            SelectedPort = current;
    }

    partial void OnSelectedVehicleChanged(VehicleOption? value)
    {
        if (value != null && (value.SysId != _services.Session.Mav.sysidcurrent || value.CompId != _services.Session.Mav.compidcurrent))
            _services.Session.SelectVehicle(value.SysId, value.CompId);
    }

    [RelayCommand]
    private async Task ToggleConnectAsync()
    {
        switch (State)
        {
            case LinkState.Connecting:
                _connectCancel?.Cancel();
                return;
            case LinkState.Connected:
                await _services.Session.DisconnectAsync();
                return;
        }

        var spec = await BuildSpecAsync();
        if (spec == null)
            return;

        Settings.Instance["comport"] = SelectedPort;
        Settings.Instance["baudrate"] = SelectedBaud.ToString();
        Settings.Instance.Save();

        await ConnectSpecAsync(spec, quiet: false);
    }

    /// <summary>Connects to a link; <paramref name="quiet"/> (auto-connect) reports failures in the status line only.</summary>
    private async Task<bool> ConnectSpecAsync(LinkSpec spec, bool quiet)
    {
        _connectCancel = new CancellationTokenSource();
        var progress = new Progress<OperationProgress>(p =>
        {
            ProgressText = p.Status;
            ProgressPercent = p.Percent;
        });

        try
        {
            ProgressText = "Connecting…";
            await _services.Session.ConnectAsync(spec, getParams: true, progress, _connectCancel.Token);
            Connected?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (ConnectException ex)
        {
            if (quiet)
                AutoConnectStatus = "Connect failed: " + ex.Message.Split('\n')[0];
            else
                await _services.Dialogs.ShowMessageAsync("Connect failed", ex.Message, MessageKind.Error);
            return false;
        }
        finally
        {
            _connectCancel.Dispose();
            _connectCancel = null;
            ProgressText = "";
            ProgressPercent = -1;
        }
    }

    private async Task<LinkSpec?> BuildSpecAsync()
    {
        switch (SelectedPort)
        {
            case Tcp:
            {
                var last = Settings.Instance["next_tcp", "127.0.0.1:5760"];
                var text = await _services.Dialogs.PromptAsync("TCP connection", "Remote host and port (start the remote end first):", last);
                if (string.IsNullOrWhiteSpace(text))
                    return null;
                Settings.Instance["next_tcp"] = text.Trim();
                return Parse("tcp:" + text.Trim());
            }
            case Udp:
            {
                var last = Settings.Instance["next_udp", "14550"];
                var text = await _services.Dialogs.PromptAsync("UDP connection", "Local port to listen on:", last);
                if (string.IsNullOrWhiteSpace(text))
                    return null;
                Settings.Instance["next_udp"] = text.Trim();
                return Parse("udp:" + text.Trim());
            }
            case UdpClient:
            {
                var last = Settings.Instance["next_udpcl", "192.168.4.1:14550"];
                var text = await _services.Dialogs.PromptAsync("UDP client connection", "Remote host and port:", last);
                if (string.IsNullOrWhiteSpace(text))
                    return null;
                Settings.Instance["next_udpcl"] = text.Trim();
                return Parse("udpout:" + text.Trim());
            }
            default:
                return LinkSpec.Serial(SelectedPort, SelectedBaud);
        }

        LinkSpec? Parse(string text)
        {
            try
            {
                return LinkSpec.Parse(text);
            }
            catch (FormatException ex)
            {
                _ = _services.Dialogs.ShowMessageAsync("Invalid address", ex.Message, MessageKind.Error);
                return null;
            }
        }
    }

    private void OnStateChanged()
    {
        State = _services.Session.State;
        RefreshVehicles();
    }

    /// <summary>Called by the shell's telemetry tick: vehicles appear on the link over time.</summary>
    public void RefreshVehicles()
    {
        var list = State == LinkState.Connected ? _services.Session.Vehicles : [];
        var options = list
            .Where(m => m.sysid != 0)
            .Select(m => new VehicleOption((byte)m.sysid, (byte)m.compid, $"{m.sysid}-{m.compid} {m.aptype.ToString().Replace('_', ' ')}"))
            .ToList();

        if (options.SequenceEqual(Vehicles))
            return;

        Vehicles.Clear();
        foreach (var o in options)
            Vehicles.Add(o);
        SelectedVehicle = Vehicles.FirstOrDefault(v => v.SysId == _services.Session.Mav.sysidcurrent &&
                                                       v.CompId == _services.Session.Mav.compidcurrent)
                          ?? Vehicles.FirstOrDefault();
    }
}
