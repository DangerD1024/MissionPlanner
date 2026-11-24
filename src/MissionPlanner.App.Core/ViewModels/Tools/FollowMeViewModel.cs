using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using MissionPlanner.ArduPilot;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.Tools;

/// <summary>The guided target Follow Me sends for a GPS fix.</summary>
public static class FollowMeLogic
{
    /// <summary>
    /// A WAYPOINT at the GPS position and <paramref name="altAboveHome"/> metres above home
    /// (Mission Planner asks "Enter Alt (relative to home alt)").
    /// </summary>
    public static Locationwp Target(GgaFix fix, double altAboveHome) => new()
    {
        id = (ushort)MAVLink.MAV_CMD.WAYPOINT,
        lat = fix.Lat,
        lng = fix.Lng,
        alt = (float)altAboveHome,
        frame = (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT,
    };

    /// <summary>Mission Planner's starting altitude: 10 m for copters, 100 m otherwise.</summary>
    public static double DefaultAltitude(Firmwares firmware) => firmware == Firmwares.ArduCopter2 ? 10 : 100;
}

/// <summary>
/// TOOLS > Follow Me (Mission Planner's Ctrl+F > Follow Me): reads a NMEA GPS connected to this
/// computer and keeps sending the vehicle a guided "go to" at its position.
/// </summary>
public sealed partial class FollowMeViewModel : ToolPageViewModel
{
    private const string PortKey = "next_followme_port";
    private const string BaudKey = "next_followme_baud";
    private const string RateKey = "next_followme_rate";
    private const string AltKey = "next_followme_alt";

    private static readonly ILog Log = LogManager.GetLogger(typeof(FollowMeViewModel));

    private NmeaGpsReader? _reader;

    public FollowMeViewModel(AppServices services)
        : base("Follow Me",
            "Use a NMEA GPS connected to this computer to make the vehicle follow you: its position is sent as a guided target at the update rate. Put the vehicle in Guided mode first.",
            services)
    {
        _port = Settings.Instance[PortKey, ""];
        _baud = Settings.Instance.GetInt32(BaudKey, 4800);
        _rateHz = Settings.Instance.GetDouble(RateKey, 0.5);
        if (!Rates.Contains(_rateHz))
            _rateHz = 0.5;
        if (Settings.Instance.GetDouble(AltKey, double.NaN) is var alt && !double.IsNaN(alt))
            _altitude = (decimal)alt;
    }

    public static IReadOnlyList<int> Bauds { get; } = [4800, 9600, 14400, 19200, 28800, 38400, 57600, 115200];

    public static IReadOnlyList<double> Rates { get; } = [2, 1, 0.5, 0.25];

    public IReadOnlyList<string> SerialPorts { get; private set; } = [];

    [ObservableProperty]
    private string? _port;

    [ObservableProperty]
    private int _baud;

    [ObservableProperty]
    private double _rateHz;

    /// <summary>Altitude above home in display units; null until the vehicle type is known.</summary>
    [ObservableProperty]
    private decimal? _altitude;

    public string AltitudeUnit => CurrentState.AltUnit;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartStopText), nameof(CanEditSettings))]
    private bool _isRunning;

    public string StartStopText => IsRunning ? "Stop" : "Connect";

    public bool CanEditSettings => !IsRunning;

    /// <summary>The GPS position and the target, as Mission Planner's location label.</summary>
    [ObservableProperty]
    private string _location = "0,0,0";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SentText))]
    private int _targetsSent;

    public string SentText => L.Format("{0} targets sent", TargetsSent);

    public override void OnShown()
    {
        base.OnShown();
        RefreshPorts();
        if (Altitude == null)
            Altitude = (decimal)(FollowMeLogic.DefaultAltitude(Services.Session.Cs.firmware) * CurrentState.multiplieralt);
    }

    partial void OnPortChanged(string? value) => Settings.Instance[PortKey] = value ?? "";

    partial void OnBaudChanged(int value) => Settings.Instance[BaudKey] = value.ToString(CultureInfo.InvariantCulture);

    partial void OnRateHzChanged(double value)
    {
        Settings.Instance[RateKey] = value.ToString(CultureInfo.InvariantCulture);
        if (_reader != null)
            _reader.Gate.Hz = value;
    }

    partial void OnAltitudeChanged(decimal? value)
    {
        if (value != null)
            Settings.Instance[AltKey] = value.Value.ToString(CultureInfo.InvariantCulture);
    }

    [RelayCommand]
    private void RefreshPorts()
    {
        SerialPorts = LinkFactory.GetSerialPortNames();
        OnPropertyChanged(nameof(SerialPorts));
        if (string.IsNullOrEmpty(Port) || !SerialPorts.Contains(Port))
            Port = SerialPorts.FirstOrDefault() ?? Port;
    }

    [RelayCommand]
    private void StartStop()
    {
        if (IsRunning)
        {
            Stop();
            Report("Follow Me stopped.");
            return;
        }

        if (string.IsNullOrWhiteSpace(Port))
        {
            Report("Pick the serial port of the NMEA GPS.", true);
            return;
        }

        if (Altitude is not { } alt || alt <= 0)
        {
            Report("Enter the altitude above home to follow at.", true);
            return;
        }

        var altMetres = (double)alt / CurrentState.multiplieralt;
        var source = NmeaSources.Create(NmeaSourceKind.Serial, Port.Trim(), Baud);
        var reader = new NmeaGpsReader(source, RateHz, Path.Combine(Settings.GetUserDataDirectory(), "followmeraw.txt"))
        {
            OnUpdate = fix => SendTarget(fix, altMetres),
        };
        try
        {
            reader.Start();
        }
        catch (Exception ex)
        {
            source.Dispose();
            Report($"Could not open {Port}: {ex.Message}", true);
            return;
        }

        _reader = reader;
        TargetsSent = 0;
        IsRunning = true;
        Report($"Reading the GPS on {Port} at {Baud} baud; following at {alt} {AltitudeUnit} above home.");
    }

    internal void SendTarget(GgaFix fix, double altMetres)
    {
        var wp = FollowMeLogic.Target(fix, altMetres);
        var text = fix.Describe(altMetres * CurrentState.multiplieralt);
        var session = Services.Session;
        var sent = false;
        if (session.IsConnected && !session.Mav.giveComport)
        {
            try
            {
                // as Mission Planner: the mode is left alone, the vehicle follows while in Guided
                session.Mav.setGuidedModeWP(session.Vehicle.sysid, session.Vehicle.compid, wp, false);
                sent = true;
            }
            catch (Exception ex)
            {
                Log.Warn("follow me", ex);
            }
        }

        Services.Dispatcher.Post(() =>
        {
            Location = text;
            if (sent)
                TargetsSent++;
        });
    }

    private void Stop()
    {
        var reader = Interlocked.Exchange(ref _reader, null);
        reader?.Stop();
        IsRunning = false;
    }

    public override void Tick()
    {
        base.Tick();
        // the GPS went away (unplugged): show it stopped
        if (_reader is { IsRunning: false })
        {
            Stop();
            Report("The GPS port closed; Follow Me stopped.", true);
        }
    }
}
