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
        services.Session.LinkLost += (_, reason) =>
        {
            // read now: a new connection may begin before the posted handler runs
            var rebooted = services.Session.LostByReboot ? services.Session.Link : null;
            services.Dispatcher.Post(() => OnLinkLost(reason, rebooted));
        };

        _autoConnect = Settings.Instance.GetBoolean(AutoConnectKey, false);
        if (_autoConnect)
            _auto.Start();
    }

    public const string AutoConnectKey = "next_autoconnect";

    private readonly AutoConnectService _auto;

    private readonly RebootReconnect _rebootReconnect = new();

    /// <summary>Waiting for a rebooting board to come back (auto-connect off).</summary>
    private CancellationTokenSource? _rebootWait;

    private void OnLinkLost(string reason, LinkSpec? rebooted)
    {
        if (rebooted is { Kind: LinkKind.Serial })
        {
            // the board left USB to restart: connect again once, when it is back (one connect, not a dialog)
            AutoConnectStatus = "Autopilot restarting: connecting again when it is back…";
            if (AutoConnect)
                _auto.Resume();
            else
                _ = ReconnectAfterRebootAsync(rebooted);
            return;
        }

        if (AutoConnect)
        {
            // a pulled cable is expected with auto-connect: keep looking instead of a dialog
            AutoConnectStatus = "Link lost: " + reason;
            _auto.Resume();
        }
        else
        {
            _ = _services.Dialogs.ShowMessageAsync("Connection lost", reason, MessageKind.Warning);
        }
    }

    private async Task ReconnectAfterRebootAsync(LinkSpec link)
    {
        StopRebootWait();
        var wait = _rebootWait = new CancellationTokenSource();
        try
        {
            var ok = await _rebootReconnect.RunAsync(link, () => State == LinkState.Disconnected,
                () => ConnectSpecAsync(link, quiet: true), wait.Token);
            if (ok)
                AutoConnectStatus = "";
            else if (State == LinkState.Disconnected && !wait.IsCancellationRequested)
                AutoConnectStatus = "The autopilot did not come back after the reboot: connect when it is ready.";
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_rebootWait == wait)
                _rebootWait = null;
            wait.Dispose();
        }
    }

    /// <summary>The user took over (Connect, Cancel, auto-connect switched on): stop waiting for the rebooted board.</summary>
    private void StopRebootWait()
    {
        _rebootWait?.Cancel();
        _rebootWait = null;
    }

    /// <summary>The link being connected, held off auto-connect if the user cancels.</summary>
    private LinkSpec? _connecting;

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
        {
            // switched on: connect to whatever is there, also links disconnected earlier
            StopRebootWait();
            _auto.ReleaseAll();
            _auto.Start();
        }
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
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(IsBusy), nameof(ConnectText), nameof(CanEditLink), nameof(ShowVehicles), nameof(ShowAutoConnect))]
    private LinkState _state;

    /// <summary>Progress text while connecting (heartbeat wait, parameter download).</summary>
    [ObservableProperty]
    private string _progressText = "";

    /// <summary>Percent done of the current connect step, or -1 when unknown (waiting for heartbeats).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgress), nameof(ProgressValue))]
    private int _progressPercent = -1;

    /// <summary>The step reports how far it is (MAVFTP, parameter download): the top line fills instead of sweeping.</summary>
    public bool HasProgress => ProgressPercent >= 0;

    public double ProgressValue => Math.Clamp(ProgressPercent, 0, 100);

    public bool IsSerial => SelectedPort is not (Tcp or Udp or UdpClient);

    public bool IsConnected => State == LinkState.Connected;

    /// <summary>Connected but nothing heard from the vehicle for a while.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVehicles))]
    private bool _isStale;

    [ObservableProperty]
    private string _staleText = "";

    /// <summary>A telemetry log is loaded for playback (no link): its vehicles are listed as a live link's are.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVehicles), nameof(ShowAutoConnect))]
    private bool _isReplaying;

    public bool ShowVehicles => (IsConnected && !IsStale) || IsReplaying;

    /// <summary>The auto-connect line shares the row with the vehicle list.</summary>
    public bool ShowAutoConnect => CanEditLink && !IsReplaying;

    /// <summary>Called by the shell's UI tick.</summary>
    public void Tick()
    {
        var s = _services.Session;
        var replaying = State == LinkState.Disconnected && s.Mav.logplaybackfile != null;
        if (replaying != IsReplaying)
        {
            IsReplaying = replaying;
            RefreshVehicles();
        }

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

    // runs again while connecting: the same button is Cancel then
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleConnectAsync()
    {
        switch (State)
        {
            // the user's Disconnect / Cancel: auto-connect leaves this vehicle alone until it is
            // unplugged (USB) or stops sending (UDP/TCP), instead of connecting straight back
            case LinkState.Connecting:
                StopRebootWait();
                if (_connecting != null)
                    _auto.HoldOff(_connecting);
                _connectCancel?.Cancel();
                return;
            case LinkState.Connected:
                if (_services.Session.Link is { } link)
                    _auto.HoldOff(link);
                await _services.Session.DisconnectAsync();
                return;
        }

        StopRebootWait();
        var spec = await BuildSpecAsync();
        if (spec == null)
            return;
        _auto.Release(spec);

        Settings.Instance["comport"] = SelectedPort;
        Settings.Instance["baudrate"] = SelectedBaud.ToString();
        Settings.Instance.Save();

        await ConnectSpecAsync(spec, quiet: false);
    }

    /// <summary>Connects to a link; <paramref name="quiet"/> (auto-connect) reports failures in the status line only.</summary>
    private async Task<bool> ConnectSpecAsync(LinkSpec spec, bool quiet)
    {
        _connecting = spec;
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
            // SETTINGS > Load waypoints on connect; an auto-connect keeps planned points instead of asking
            _ = Mission.MissionOnConnect.RunAsync(_services, ask: !quiet);
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
            _connecting = null;
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
        var list = State == LinkState.Connected || IsReplaying ? _services.Session.Vehicles : [];
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
