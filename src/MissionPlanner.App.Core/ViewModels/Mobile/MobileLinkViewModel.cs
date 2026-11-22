using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.Session;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.Mobile;

/// <summary>A way to reach the vehicle from a phone: Wi-Fi telemetry (UDP), a TCP bridge, or SITL.</summary>
public sealed record MobileLinkChoice(LinkKind Kind, string Title, string Hint);

/// <summary>
/// The phone's connect page: pick UDP (listen; ESP8266 / ELRS / Herelink Wi-Fi bridges send to
/// 14550), UDP to a host, or TCP; recent links one tap away; connect at start if wanted.
/// </summary>
public sealed partial class MobileLinkViewModel : ObservableObject
{
    public const string RecentKey = "next_mobile_links";
    public const string AutoKey = "next_mobile_autoconnect";
    private const int MaxRecent = 6;

    private readonly AppServices _services;
    private CancellationTokenSource? _cancel;

    public MobileLinkViewModel(AppServices services)
    {
        _services = services;
        _selectedKind = Kinds[0];
        _autoConnect = Settings.Instance.GetBoolean(AutoKey, false);
        foreach (var r in (Settings.Instance[RecentKey] ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                Recent.Add(LinkSpec.Parse(r));
            }
            catch (FormatException)
            {
            }
        }

        if (Recent.FirstOrDefault() is { } last)
            Fill(last);
        services.Session.StateChanged += (_, _) => services.Dispatcher.Post(OnStateChanged);
    }

    public IReadOnlyList<MobileLinkChoice> Kinds { get; } =
    [
        new(LinkKind.UdpListen, "UDP (Wi-Fi telemetry)", "Listens for the vehicle on this port: Wi-Fi bridges and SITL send to 14550."),
        new(LinkKind.Tcp, "TCP", "Connects to a host and port, e.g. a TCP telemetry bridge or SITL on 5760."),
        new(LinkKind.UdpConnect, "UDP to host", "Sends to and receives from a host and port."),
    ];

    public ObservableCollection<LinkSpec> Recent { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsHost), nameof(KindHint))]
    private MobileLinkChoice _selectedKind;

    [ObservableProperty]
    private string _host = "192.168.4.1";

    [ObservableProperty]
    private string _port = "14550";

    [ObservableProperty]
    private bool _autoConnect;

    partial void OnAutoConnectChanged(bool value) => Settings.Instance[AutoKey] = value.ToString();

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    private string _error = "";

    public bool NeedsHost => SelectedKind.Kind != LinkKind.UdpListen;

    public string KindHint => SelectedKind.Hint;

    public LinkState State => _services.Session.State;

    public bool IsConnected => State == LinkState.Connected;

    public bool IsConnecting => State == LinkState.Connecting;

    public string ButtonText => State switch
    {
        LinkState.Connected => "Disconnect",
        LinkState.Connecting => "Cancel",
        _ => "Connect",
    };

    /// <summary>What the link is, once connected ("udp:14550 · ArduCopter V4.6.2").</summary>
    public string ConnectedText => _services.Session is { IsConnected: true } s
        ? $"{s.Link} · {FirmwareName(s.Cs.firmware)} {s.Cs.version?.ToString(3)}".Trim()
        : "";

    /// <summary>"ArduCopter" rather than the library's ArduCopter2.</summary>
    public static string FirmwareName(MissionPlanner.ArduPilot.Firmwares fw) =>
        fw == MissionPlanner.ArduPilot.Firmwares.ArduCopter2 ? "ArduCopter" : fw.ToString();

    partial void OnSelectedKindChanged(MobileLinkChoice value)
    {
        if (value.Kind == LinkKind.Tcp && Port == "14550")
            Port = "5760";
        else if (value.Kind != LinkKind.Tcp && Port == "5760")
            Port = "14550";
    }

    private void OnStateChanged()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsConnecting));
        OnPropertyChanged(nameof(ButtonText));
        OnPropertyChanged(nameof(ConnectedText));
    }

    /// <summary>The link the fields describe, or null with <see cref="Error"/> set.</summary>
    public LinkSpec? BuildSpec()
    {
        Error = "";
        if (!int.TryParse(Port.Trim(), out var port) || port is < 1 or > 65535)
        {
            Error = $"\"{Port}\" is not a port number (1–65535).";
            return null;
        }

        var host = Host.Trim();
        if (NeedsHost && host.Length == 0)
        {
            Error = "Enter the host (IP address or name).";
            return null;
        }

        return SelectedKind.Kind switch
        {
            LinkKind.Tcp => LinkSpec.Tcp(host, port),
            LinkKind.UdpConnect => LinkSpec.UdpConnect(host, port),
            _ => LinkSpec.UdpListen(port),
        };
    }

    // runs again while connecting: the same button is Cancel then
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Toggle()
    {
        switch (State)
        {
            case LinkState.Connecting:
                _cancel?.Cancel();
                return;
            case LinkState.Connected:
                await _services.Session.DisconnectAsync();
                return;
        }

        if (BuildSpec() is { } spec)
            await ConnectAsync(spec);
    }

    [RelayCommand]
    private Task ConnectRecent(LinkSpec? spec)
    {
        if (spec == null || State != LinkState.Disconnected)
            return Task.CompletedTask;
        Fill(spec);
        return ConnectAsync(spec);
    }

    /// <summary>At start: the last link again, if the user asked for that.</summary>
    public Task ConnectAtStartAsync() =>
        AutoConnect && State == LinkState.Disconnected && Recent.FirstOrDefault() is { } last ? ConnectAsync(last) : Task.CompletedTask;

    public async Task<bool> ConnectAsync(LinkSpec spec)
    {
        Error = "";
        _cancel = new CancellationTokenSource();
        var progress = new Progress<OperationProgress>(p => ProgressText = p.Status);
        try
        {
            ProgressText = "Connecting…";
            await _services.Session.ConnectAsync(spec, getParams: true, progress, _cancel.Token);
            Remember(spec);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (ConnectException ex)
        {
            Error = ex.Message.Split('\n')[0];
            return false;
        }
        finally
        {
            _cancel.Dispose();
            _cancel = null;
            ProgressText = "";
            OnStateChanged();
        }
    }

    private void Remember(LinkSpec spec)
    {
        var existing = Recent.FirstOrDefault(r => r == spec);
        if (existing != null)
            Recent.Remove(existing);
        Recent.Insert(0, spec);
        while (Recent.Count > MaxRecent)
            Recent.RemoveAt(Recent.Count - 1);
        Settings.Instance[RecentKey] = string.Join("|", Recent.Select(r => r.ToString()));
        Settings.Instance.Save();
    }

    private void Fill(LinkSpec spec)
    {
        SelectedKind = Kinds.FirstOrDefault(k => k.Kind == spec.Kind) ?? Kinds[0];
        if (spec.Kind != LinkKind.UdpListen)
            Host = spec.Address;
        Port = spec.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
