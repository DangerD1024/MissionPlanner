using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using log4net;
using MissionPlanner.Comms;
using Newtonsoft.Json;

namespace MissionPlanner.App.Core.Outputs;

/// <summary>What an output sends.</summary>
public enum OutputKind
{
    /// <summary>Every MAVLink packet from the vehicle, and optionally what the other side sends back to it (Mission Planner's MAVLink mirror).</summary>
    MavlinkMirror,

    /// <summary>The vehicle position as NMEA sentences.</summary>
    Nmea,

    /// <summary>Cursor on Target XML for TAK.</summary>
    CursorOnTarget,

    /// <summary>MicroDrone telemetry lines.</summary>
    MicroDrone,
}

/// <summary>The saved settings of one output.</summary>
public sealed class OutputConfig
{
    public OutputKind Kind { get; set; }
    public OutputTransportKind Transport { get; set; } = OutputTransportKind.UdpSend;
    public string Address { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 14551;
    public double RateHz { get; set; } = 5;
    public bool AllowWrite { get; set; } = true;
    public string CotType { get; set; } = "a-f-A-M-F-Q";
    public string CotCallsign { get; set; } = "";

    /// <summary>Start again when the app starts if it was running when it closed.</summary>
    public bool WasRunning { get; set; }

    public OutputEndpoint Endpoint => new(Transport, Address, Port);

    /// <summary>Mission Planner's defaults for a new output of this kind.</summary>
    public static OutputConfig Default(OutputKind kind) => kind switch
    {
        OutputKind.MavlinkMirror => new() { Kind = kind, Transport = OutputTransportKind.UdpSend, Address = "127.0.0.1", Port = 14550 },
        OutputKind.Nmea => new() { Kind = kind, Transport = OutputTransportKind.TcpServer, Port = 14551, RateHz = 5 },
        OutputKind.CursorOnTarget => new() { Kind = kind, Transport = OutputTransportKind.UdpSend, Address = "239.2.3.1", Port = 6969, RateHz = 1 },
        OutputKind.MicroDrone => new() { Kind = kind, Transport = OutputTransportKind.Serial, Address = "", Port = 57600, RateHz = 10 },
        _ => new() { Kind = kind },
    };
}

/// <summary>One output: its settings, and while running its transport and sender.</summary>
public sealed partial class OutputItem : ObservableObject
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(OutputItem));

    private readonly AppServices _services;
    private ICommsSerial? _comms;
    private MAVLinkInterface.Mirror? _mirror;
    private CancellationTokenSource? _stop;
    private Task? _loop;
    private long _sent;

    internal OutputItem(AppServices services, OutputConfig config)
    {
        _services = services;
        Config = config;
    }

    public OutputConfig Config { get; }

    public string KindName => Names[Config.Kind];

    public static IReadOnlyDictionary<OutputKind, string> Names { get; } = new Dictionary<OutputKind, string>
    {
        [OutputKind.MavlinkMirror] = "MAVLink mirror",
        [OutputKind.Nmea] = "NMEA position",
        [OutputKind.CursorOnTarget] = "Cursor on Target (TAK)",
        [OutputKind.MicroDrone] = "MicroDrone",
    };

    public string Summary => Config.Endpoint.ToString();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartStopText))]
    private bool _isRunning;

    [ObservableProperty]
    private string _status = "Stopped";

    [ObservableProperty]
    private bool _isError;

    public string StartStopText => IsRunning ? "Stop" : "Start";

    public long BytesSent => Interlocked.Read(ref _sent);

    /// <summary>Re-reads the summary after the settings were edited.</summary>
    public void SettingsChanged() => OnPropertyChanged(nameof(Summary));

    public void Start()
    {
        if (IsRunning)
            return;
        if (Config.Endpoint.Validate() is { } why)
        {
            Fail(why);
            return;
        }

        try
        {
            _comms = Config.Endpoint.Create();
            _comms.Open();
        }
        catch (Exception ex)
        {
            Log.Warn("output open", ex);
            Close();
            Fail($"Could not open {Config.Endpoint}: {ex.Message}");
            return;
        }

        Interlocked.Exchange(ref _sent, 0);
        IsError = false;
        IsRunning = true;
        Config.WasRunning = true;
        if (Config.Kind == OutputKind.MavlinkMirror)
        {
            _mirror = new MAVLinkInterface.Mirror { MirrorStream = _comms, MirrorStreamWrite = Config.AllowWrite };
            _services.Session.Mav.AddMirror(_mirror);
        }
        else
        {
            _stop = new CancellationTokenSource();
            var token = _stop.Token;
            _loop = Task.Run(() => SendLoop(token));
        }

        UpdateStatus();
    }

    public async Task StopAsync(bool remember = false)
    {
        if (!IsRunning)
            return;
        if (_mirror != null)
            _services.Session.Mav.RemoveMirror(_mirror);
        _mirror = null;
        _stop?.Cancel();
        if (_loop != null)
        {
            try
            {
                await _loop.ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Warn("output stop", ex);
            }
        }

        _loop = null;
        _cot = null;
        Close();
        IsRunning = false;
        Config.WasRunning = remember;
        Status = "Stopped";
    }

    /// <summary>App exit: stops without waiting on the UI thread, remembering that it ran.</summary>
    internal void StopForExit()
    {
        if (!IsRunning)
            return;
        if (_mirror != null)
            _services.Session.Mav.RemoveMirror(_mirror);
        _mirror = null;
        _stop?.Cancel();
        _loop?.Wait(TimeSpan.FromMilliseconds(500)); // the loop runs on the pool: no UI-thread deadlock
        Close();
        Config.WasRunning = true;
    }

    private void Close()
    {
        try
        {
            _comms?.Close();
            _comms?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug("output close", ex);
        }

        _comms = null;
    }

    private void Fail(string message)
    {
        IsError = true;
        Status = message;
        _services.Notify(message, MessageKind.Error);
    }

    /// <summary>Refreshes the status line (called by the page about once a second).</summary>
    public void UpdateStatus()
    {
        if (!IsRunning)
            return;
        var where = _comms is TcpServerComms server
            ? $"{server.ClientCount} client{(server.ClientCount == 1 ? "" : "s")} connected"
            : Config.Endpoint.ToString();
        Status = Config.Kind == OutputKind.MavlinkMirror
            ? $"Mirroring — {where}" + (Config.AllowWrite ? " (the other side can control the vehicle)" : " (read only)")
            : $"Sending — {where}, {FormatBytes(BytesSent)} sent" + (_services.Session.IsConnected ? "" : " (waiting for a vehicle)");
    }

    private static string FormatBytes(long n) =>
        n < 10_000 ? n.ToString(CultureInfo.InvariantCulture) + " B" : (n / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " kB";

    private CotOutput? _cot;
    private int _round;

    private async Task SendLoop(CancellationToken stop)
    {
        var period = TimeSpan.FromSeconds(1 / Math.Clamp(Config.RateHz, 0.01, 50));
        using var timer = new PeriodicTimer(period);
        if (Config.Kind == OutputKind.CursorOnTarget)
            _cot = new CotOutput { Type = Config.CotType, Callsign = Config.CotCallsign, IntervalSeconds = period.TotalSeconds };
        try
        {
            do
            {
                try
                {
                    foreach (var chunk in Build(DateTime.UtcNow))
                        Send(chunk);
                    _round++;
                }
                catch (Exception ex)
                {
                    Log.Debug("output send", ex);
                }
            }
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
        }

        // TAK greys the tracks out at once instead of at their stale time
        if (_cot != null)
        {
            foreach (var chunk in _cot.Expire(DateTime.UtcNow))
            {
                try
                {
                    Send(chunk);
                }
                catch (Exception)
                {
                    break;
                }
            }
        }
    }

    private IEnumerable<string> Build(DateTime utc)
    {
        var session = _services.Session;
        if (!session.IsConnected)
            return [];
        var cs = session.Cs;
        switch (Config.Kind)
        {
            case OutputKind.Nmea:
                if (cs.lat == 0 && cs.lng == 0)
                    return [];
                var home = cs.HomeLocation;
                return NmeaOutput.Sentences(VehicleFix.From(cs), home == null ? null : (home.Lat, home.Lng, home.Alt), utc, _round);
            case OutputKind.MicroDrone:
                return MicroDroneOutput.Lines(VehicleFix.From(cs), utc, _round);
            case OutputKind.CursorOnTarget:
                var vehicles = session.Vehicles.Select(m => MissionPlanner.Controls.CotVehicles.Read(m, session.Mav)).ToList();
                return _cot!.Round(vehicles, utc);
            default:
                return [];
        }
    }

    private void Send(string text)
    {
        var comms = _comms;
        if (comms == null || !comms.IsOpen)
            return;
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        comms.Write(bytes, 0, bytes.Length);
        Interlocked.Add(ref _sent, bytes.Length);
    }
}

/// <summary>
/// The outputs the user set up (TOOLS > Outputs). They belong to the app, not a screen, so they keep
/// running on every screen; their settings, and which were running, are saved.
/// </summary>
public sealed class OutputManager
{
    private const string SettingsKey = "next_outputs";
    private static readonly ILog Log = LogManager.GetLogger(typeof(OutputManager));

    private readonly AppServices _services;

    internal OutputManager(AppServices services)
    {
        _services = services;
        foreach (var config in LoadConfigs())
            Items.Add(new OutputItem(services, config));
    }

    public ObservableCollection<OutputItem> Items { get; } = new();

    public OutputItem Add(OutputKind kind)
    {
        var item = new OutputItem(_services, OutputConfig.Default(kind));
        Items.Add(item);
        Save();
        return item;
    }

    public async Task RemoveAsync(OutputItem item)
    {
        await item.StopAsync();
        Items.Remove(item);
        Save();
    }

    /// <summary>Restarts the outputs that were running when the app closed.</summary>
    public void StartRemembered()
    {
        foreach (var item in Items.Where(i => i.Config.WasRunning))
            item.Start();
    }

    /// <summary>App exit: stops everything, remembering what was running. Safe to call on the UI thread.</summary>
    public void StopAllForExit()
    {
        foreach (var item in Items)
        {
            try
            {
                item.StopForExit();
            }
            catch (Exception ex)
            {
                Log.Warn("stop output", ex);
            }
        }

        Save();
    }

    public void Save()
    {
        try
        {
            MissionPlanner.Utilities.Settings.Instance[SettingsKey] = JsonConvert.SerializeObject(Items.Select(i => i.Config).ToList());
            MissionPlanner.Utilities.Settings.Instance.Save();
        }
        catch (Exception ex)
        {
            Log.Warn("save outputs", ex);
        }
    }

    private static IEnumerable<OutputConfig> LoadConfigs()
    {
        try
        {
            var json = MissionPlanner.Utilities.Settings.Instance[SettingsKey];
            if (!string.IsNullOrEmpty(json))
                return JsonConvert.DeserializeObject<List<OutputConfig>>(json) ?? [];
        }
        catch (Exception ex)
        {
            Log.Warn("load outputs", ex);
        }

        return [];
    }
}
