using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.Tools;

/// <summary>The arithmetic of Mission Planner's Moving Base window.</summary>
public static class MovingBaseLogic
{
    /// <summary>The base altitude: the GPS altitude, less the home altitude with "Relative alt" ticked.</summary>
    public static double BaseAltitude(GgaFix fix, bool relative, double homeAlt) => relative ? fix.Alt - homeAlt : fix.Alt;

    /// <summary>Rally point 0 sits <paramref name="defaultAlt"/> (the planner's default altitude) above the base.</summary>
    public static PointLatLngAlt RallyPoint(GgaFix fix, double defaultAlt) => new(fix.Lat, fix.Lng, fix.Alt + defaultAlt);
}

/// <summary>A GPS source choice (serial ports first, then the network kinds).</summary>
public sealed record NmeaSourceChoice(NmeaSourceKind Kind, string Port, string Text)
{
    public override string ToString() => Text;
}

/// <summary>
/// TOOLS > Moving Base (Mission Planner's Ctrl+F > Moving Base): a GPS at the ground station puts
/// an extra marker on the maps where you are, and can keep rally point 0 there (planes).
/// </summary>
public sealed partial class MovingBaseViewModel : ToolPageViewModel
{
    private const string SourceKey = "next_movingbase_source";
    private const string BaudKey = "next_movingbase_baud";
    private const string RateKey = "next_movingbase_rate";
    private const string AddressKey = "next_movingbase_address";
    private const string RallyKey = "next_movingbase_rally";
    private const string RelAltKey = "next_movingbase_relalt";

    private static readonly ILog Log = LogManager.GetLogger(typeof(MovingBaseViewModel));

    private NmeaGpsReader? _reader;
    private DateTime _nextRally = DateTime.MinValue;

    public MovingBaseViewModel(AppServices services)
        : base("Moving Base",
            "Show your own position on the maps from a NMEA GPS at the ground station (serial, TCP or UDP). Optionally keep the vehicle's rally point 0 at your position.",
            services)
    {
        _baud = Settings.Instance.GetInt32(BaudKey, 4800);
        _rateHz = Settings.Instance.GetDouble(RateKey, 0.5);
        if (!Rates.Contains(_rateHz))
            _rateHz = 0.5;
        _address = Settings.Instance[AddressKey, "127.0.0.1:14551"];
        _updateRallyPoint = Settings.Instance.GetBoolean(RallyKey, false);
        _relativeAlt = Settings.Instance.GetBoolean(RelAltKey, false);
    }

    public override bool NeedsVehicle => false;

    public static IReadOnlyList<int> Bauds => FollowMeViewModel.Bauds;

    public static IReadOnlyList<double> Rates => FollowMeViewModel.Rates;

    public IReadOnlyList<NmeaSourceChoice> Sources { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSerial), nameof(ShowAddress))]
    private NmeaSourceChoice? _source;

    public bool IsSerial => Source?.Kind == NmeaSourceKind.Serial;

    public bool ShowAddress => Source?.Kind is NmeaSourceKind.TcpClient or NmeaSourceKind.UdpClient;

    [ObservableProperty]
    private int _baud;

    [ObservableProperty]
    private double _rateHz;

    /// <summary>host:port for the TCP and UDP client sources.</summary>
    [ObservableProperty]
    private string _address;

    [ObservableProperty]
    private bool _updateRallyPoint;

    [ObservableProperty]
    private bool _relativeAlt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartStopText), nameof(CanEditSettings))]
    private bool _isRunning;

    public string StartStopText => IsRunning ? "Stop" : "Connect";

    public bool CanEditSettings => !IsRunning;

    [ObservableProperty]
    private string _location = "0,0,0";

    public override void OnShown()
    {
        base.OnShown();
        RefreshSources();
    }

    partial void OnSourceChanged(NmeaSourceChoice? value)
    {
        if (value != null)
            Settings.Instance[SourceKey] = value.Kind == NmeaSourceKind.Serial ? value.Port : value.Kind.ToString();
    }

    partial void OnBaudChanged(int value) => Settings.Instance[BaudKey] = value.ToString(CultureInfo.InvariantCulture);

    partial void OnRateHzChanged(double value)
    {
        Settings.Instance[RateKey] = value.ToString(CultureInfo.InvariantCulture);
        if (_reader != null)
            _reader.Gate.Hz = value;
    }

    partial void OnAddressChanged(string value) => Settings.Instance[AddressKey] = value;

    partial void OnUpdateRallyPointChanged(bool value) => Settings.Instance[RallyKey] = value.ToString();

    partial void OnRelativeAltChanged(bool value) => Settings.Instance[RelAltKey] = value.ToString();

    [RelayCommand]
    private void RefreshSources()
    {
        var list = LinkFactory.GetSerialPortNames().Select(p => new NmeaSourceChoice(NmeaSourceKind.Serial, p, p)).ToList();
        list.Add(new(NmeaSourceKind.TcpHost, "", $"TCP host - {NmeaSources.DefaultHostPort}"));
        list.Add(new(NmeaSourceKind.TcpClient, "", "TCP client"));
        list.Add(new(NmeaSourceKind.UdpHost, "", $"UDP host - {NmeaSources.DefaultHostPort}"));
        list.Add(new(NmeaSourceKind.UdpClient, "", "UDP client"));
        Sources = list;
        OnPropertyChanged(nameof(Sources));

        var saved = Source != null ? (Source.Kind == NmeaSourceKind.Serial ? Source.Port : Source.Kind.ToString()) : Settings.Instance[SourceKey, ""];
        Source = list.FirstOrDefault(s => s.Kind == NmeaSourceKind.Serial ? s.Port == saved : s.Kind.ToString() == saved) ?? list[0];
    }

    [RelayCommand]
    private void StartStop()
    {
        if (IsRunning)
        {
            Stop();
            Report("Moving base stopped.");
            return;
        }

        if (Source is not { } src)
        {
            Report("Pick where the GPS is connected.", true);
            return;
        }

        INmeaLineSource source;
        try
        {
            source = src.Kind switch
            {
                NmeaSourceKind.Serial => NmeaSources.Create(src.Kind, src.Port, Baud),
                NmeaSourceKind.TcpHost or NmeaSourceKind.UdpHost => NmeaSources.Create(src.Kind, "", NmeaSources.DefaultHostPort),
                _ => NmeaSources.Create(src.Kind, Address.Trim(), 0),
            };
        }
        catch (Exception ex)
        {
            Report($"Invalid source: {ex.Message}", true);
            return;
        }

        var relative = RelativeAlt;
        var rally = UpdateRallyPoint;
        var reader = new NmeaGpsReader(source, RateHz, Path.Combine(Settings.GetUserDataDirectory(), "MovingBase.txt"))
        {
            OnUpdate = fix => OnBaseUpdate(fix, relative, rally),
        };
        try
        {
            reader.Start();
        }
        catch (Exception ex)
        {
            source.Dispose();
            Report($"Could not open {src.Text}: {ex.Message}", true);
            return;
        }

        _reader = reader;
        _nextRally = DateTime.MinValue;
        IsRunning = true;
        Report($"Reading the GPS on {src.Text}.");
    }

    internal void OnBaseUpdate(GgaFix fix, bool relative, bool rally)
    {
        var session = Services.Session;
        var alt = MovingBaseLogic.BaseAltitude(fix, relative, session.Cs.HomeAlt);
        var position = new PointLatLngAlt(fix.Lat, fix.Lng, fix.Alt, "Base");

        // the marker on the maps, and MAV.cs.Base as Mission Planner sets it
        Services.GcsLocation.Set(new GcsPosition(fix.Lat, fix.Lng, fix.Alt, "Moving base", DateTime.Now));
        try
        {
            session.Cs.Base = position;
        }
        catch (Exception)
        {
            // no vehicle state yet
        }

        if (rally && session.IsConnected && !session.Mav.giveComport && DateTime.Now > _nextRally)
        {
            _nextRally = DateTime.Now.AddSeconds(5);
            try
            {
                var mav = session.Mav;
                var v = session.Vehicle;
                mav.setParam(v.sysid, v.compid, "RALLY_TOTAL", 1);
                mav.setRallyPoint(0, MovingBaseLogic.RallyPoint(fix, Services.Mission.DefaultAlt), 0, 0, 0, 1);
                mav.setParam(v.sysid, v.compid, "RALLY_TOTAL", 1);
            }
            catch (Exception ex)
            {
                Log.Warn("moving base rally point", ex);
            }
        }

        var text = fix.Describe(alt * CurrentState.multiplieralt);
        Services.Dispatcher.Post(() => Location = text);
    }

    private void Stop()
    {
        var reader = Interlocked.Exchange(ref _reader, null);
        reader?.Stop();
        Services.GcsLocation.Clear();
        IsRunning = false;
    }
}
