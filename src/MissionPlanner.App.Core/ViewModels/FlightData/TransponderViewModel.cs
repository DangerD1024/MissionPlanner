using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MissionPlanner.App.Core.ViewModels.FlightData;

/// <summary>Mode A squawk codes: four octal digits written as a decimal number (1200, 7000, 7700).</summary>
public static class SquawkCode
{
    public static bool IsValid(int code) =>
        code is >= 0 and <= 7777 && code.ToString(CultureInfo.InvariantCulture).All(c => c <= '7');

    /// <summary>
    /// Mission Planner's up/down fix-up: a digit stepped to 8 carries into the next one, a digit
    /// stepped to 9 (a borrow) becomes 7. So 1207 + 1 → 1210 and 1200 - 1 → 1177.
    /// </summary>
    public static int Normalize(int value)
    {
        value = Math.Clamp(value, 0, 9999);
        int ones = value % 10, tens = value / 10 % 10, hundreds = value / 100 % 10, thousands = value / 1000 % 10;
        if (ones == 9)
            ones = 7;
        if (tens == 9)
            tens = 7;
        if (hundreds == 9)
            hundreds = 7;
        if (thousands == 9)
            thousands = 7;
        if (ones > 7)
        {
            tens++;
            ones = 0;
        }

        if (tens > 7)
        {
            hundreds++;
            tens = 0;
        }

        if (hundreds > 7)
        {
            hundreds = 0;
            thousands++;
        }

        if (thousands > 7)
            thousands = 7;
        return thousands * 1000 + hundreds * 100 + tens * 10 + ones;
    }
}

/// <summary>uAvionix transponder control, as Mission Planner's Transponder tab sends it.</summary>
public static class TransponderControl
{
    // UAVIONIX_ADSB_OUT_CONTROL_STATE bits
    public const byte Ident = 8;
    public const byte ModeA = 16;
    public const byte ModeC = 32;
    public const byte ModeS = 64;
    public const byte Es1090 = 128;

    public static byte State(bool modeA, bool modeC, bool modeS, bool es1090, bool ident) =>
        (byte)((ident ? Ident : 0) | (modeA ? ModeA : 0) | (modeC ? ModeC : 0) | (modeS ? ModeS : 0) | (es1090 ? Es1090 : 0));

    /// <summary>UAVIONIX_ADSB_OUT_CONTROL: baro altitude "not set" (int.MaxValue), squawk, state, flight id (8 chars, ASCII).</summary>
    public static MAVLink.mavlink_uavionix_adsb_out_control_t Build(int squawk, byte state, string flightId)
    {
        var id = new byte[8];
        var ascii = Encoding.ASCII.GetBytes((flightId ?? "").ToUpperInvariant());
        Array.Copy(ascii, id, Math.Min(8, ascii.Length));
        return new MAVLink.mavlink_uavionix_adsb_out_control_t
        {
            baroAltMSL = int.MaxValue,
            squawk = (ushort)Math.Clamp(squawk, 0, 7777),
            state = state,
            emergencyStatus = 0,
            flight_id = id,
            x_bit = 0,
        };
    }

    // Navigation Integrity Category / Navigation Accuracy Category for position (Mission Planner's tables)
    private static readonly string[] Nic =
        ["UNKNOWN", "<20.0NM", "<8.0NM", "<4.0NM", "<2.0NM", "<1.0NM", "<0.3NM", "<0.2NM", "<0.1NM", "<75m", "<25m", "<7.5m"];

    private static readonly string[] Nacp =
        ["UNKNOWN", "<10.0NM", "<4.0NM", "<2.0NM", "<1.0NM", "<0.5NM", "<0.3NM", "<0.1NM", "<0.05NM", "<30m", "<10m", "<3m"];

    public static string NicText(int v) => v >= 0 && v < Nic.Length ? Nic[v] : v.ToString(CultureInfo.InvariantCulture);

    public static string NacpText(int v) => v >= 0 && v < Nacp.Length ? Nacp[v] : v.ToString(CultureInfo.InvariantCulture);
}

public enum TransponderLink
{
    /// <summary>No status yet: offer to connect (request UAVIONIX_ADSB_OUT_STATUS).</summary>
    NotConnected,
    Connected,
    /// <summary>Status stopped arriving.</summary>
    Lost,
    /// <summary>The transponder says its status is unavailable.</summary>
    Offline,
}

/// <summary>
/// Transponder tab, after Mission Planner's: ADS-B out status from a uAvionix transponder
/// (UAVIONIX_ADSB_OUT_STATUS), STBY / ON / ALT, squawk, flight ID, IDENT, fault flags, NIC / NACp.
/// </summary>
public sealed partial class TransponderViewModel : PageViewModel
{
    private const int LostAfterMs = 5000;

    private readonly IFlightCommands _commands;
    private long _lastStatus = long.MinValue;
    private bool _everConnected;
    private long _tick;
    private bool _syncing;

    public TransponderViewModel(AppServices services, IFlightCommands commands)
        : base("Transponder")
    {
        Services = services;
        _commands = commands;
    }

    public AppServices Services { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive), nameof(LinkText), nameof(CanConnect))]
    private TransponderLink _link;

    /// <summary>Status is arriving: the controls are live.</summary>
    public bool IsActive => Link == TransponderLink.Connected;

    public bool CanConnect => _commands.IsConnected && Link is TransponderLink.NotConnected or TransponderLink.Lost;

    /// <summary>IDENT works without a uAvionix status too (DO_ADSB_OUT_IDENT).</summary>
    public bool CanIdent => _commands.IsConnected;

    public string LinkText => Link switch
    {
        TransponderLink.Connected => "Transponder connected",
        TransponderLink.Lost => "Transponder status lost",
        TransponderLink.Offline => "Transponder offline",
        _ => "Connect to transponder",
    };

    [ObservableProperty]
    private bool _modeA;

    [ObservableProperty]
    private bool _modeC;

    [ObservableProperty]
    private bool _modeS;

    [ObservableProperty]
    private bool _es1090;

    [ObservableProperty]
    private bool _identActive;

    [ObservableProperty]
    private bool _airborne;

    [ObservableProperty]
    private decimal? _squawk = 1200;

    [ObservableProperty]
    private string _flightId = "";

    [ObservableProperty]
    private string _nic = "";

    [ObservableProperty]
    private string _nacp = "";

    [ObservableProperty]
    private string _boardTemperature = "";

    [ObservableProperty]
    private bool _faultMaintenance;

    [ObservableProperty]
    private bool _faultGpsUnavailable;

    [ObservableProperty]
    private bool _faultGpsNoFix;

    [ObservableProperty]
    private bool _faultTxSystem;

    /// <summary>STBY, ON or ALT, from the mode bits.</summary>
    public string ModeName => ModeA && ModeC && ModeS && Es1090 ? "ALT"
        : ModeA && !ModeC && ModeS && Es1090 ? "ON"
        : !ModeA && !ModeC && !ModeS && !Es1090 ? "STBY" : "CUSTOM";

    public bool IsStandby => ModeName == "STBY";

    public bool IsOn => ModeName == "ON";

    public bool IsAlt => ModeName == "ALT";

    partial void OnModeAChanged(bool value) => OnModeChanged();

    partial void OnModeCChanged(bool value) => OnModeChanged();

    partial void OnModeSChanged(bool value) => OnModeChanged();

    partial void OnEs1090Changed(bool value) => OnModeChanged();

    private void OnModeChanged()
    {
        OnPropertyChanged(nameof(ModeName));
        OnPropertyChanged(nameof(IsStandby));
        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(IsAlt));
    }

    partial void OnSquawkChanged(decimal? value)
    {
        if (_syncing || value is not { } v)
            return;
        var fixedCode = SquawkCode.Normalize((int)v);
        if (fixedCode != (int)v)
        {
            _syncing = true;
            Squawk = fixedCode;
            _syncing = false;
        }

        if (IsActive)
            SendControl(ident: false);
    }

    public override void OnShown() => Update();

    public override void Tick()
    {
        if (_tick++ % 5 == 0)
            Update();
    }

    private void Update()
    {
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanIdent));
        var cs = Services.Session.Cs;
        var now = Environment.TickCount64;
        if (cs.xpdr_status_pending)
        {
            // the reader sets this for every UAVIONIX_ADSB_OUT_STATUS; Mission Planner clears it the same way
            cs.xpdr_status_pending = false;
            _lastStatus = now;
            _everConnected = true;
            if (cs.xpdr_status_unavail)
            {
                Link = TransponderLink.Offline;
                return;
            }

            Link = TransponderLink.Connected;
            ApplyStatus(cs);
        }
        else if (Link == TransponderLink.Connected && now - _lastStatus > LostAfterMs)
        {
            Link = TransponderLink.Lost;
        }
        else if (!_everConnected)
        {
            Link = TransponderLink.NotConnected;
        }
    }

    private void ApplyStatus(CurrentState cs)
    {
        _syncing = true;
        try
        {
            ModeA = cs.xpdr_mode_A_enabled;
            ModeC = cs.xpdr_mode_C_enabled;
            ModeS = cs.xpdr_mode_S_enabled;
            Es1090 = cs.xpdr_es1090_tx_enabled;
            IdentActive = cs.xpdr_ident_active;
            Airborne = cs.xpdr_airborne_status;
            if (SquawkCode.IsValid(cs.xpdr_mode_A_squawk_code))
                Squawk = cs.xpdr_mode_A_squawk_code;
            if (cs.xpdr_flight_id is { } id)
                FlightId = Encoding.ASCII.GetString(id).TrimEnd('\0', ' ');
            Nic = TransponderControl.NicText(cs.xpdr_nic);
            Nacp = TransponderControl.NacpText(cs.xpdr_nacp);
            BoardTemperature = cs.xpdr_board_temperature.ToString(CultureInfo.InvariantCulture) + " °C";
            FaultMaintenance = cs.xpdr_maint_req;
            FaultGpsUnavailable = cs.xpdr_gps_unavail;
            FaultGpsNoFix = cs.xpdr_gps_no_fix;
            FaultTxSystem = cs.xpdr_adsb_tx_sys_fail;
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Asks for UAVIONIX_ADSB_OUT_STATUS at 1 Hz and waits up to 3 s for one.</summary>
    [RelayCommand]
    private async Task ConnectAsync()
    {
        var cs = Services.Session.Cs;
        cs.xpdr_status_pending = false;
        var got = false;
        await _commands.RunAsync("Connect to transponder", (mav, sysid, compid, state) =>
        {
            mav.doCommand(sysid, compid, MAVLink.MAV_CMD.SET_MESSAGE_INTERVAL,
                (float)MAVLink.MAVLINK_MSG_ID.UAVIONIX_ADSB_OUT_STATUS, 1_000_000, 0, 0, 0, 0, 0, requireack: false);
            var until = Environment.TickCount64 + 3000;
            while (Environment.TickCount64 < until && !state.xpdr_status_pending)
                Thread.Sleep(100);
            got = state.xpdr_status_pending;
            return got;
        }, "Transponder connected", "Timeout: no transponder status (UAVIONIX_ADSB_OUT_STATUS) received");
        if (got)
            Update();
    }

    [RelayCommand]
    private void Standby() => SetModes(false, false, false, false);

    [RelayCommand]
    private void On() => SetModes(true, false, true, true);

    [RelayCommand]
    private void Alt() => SetModes(true, true, true, true);

    private void SetModes(bool a, bool c, bool s, bool es)
    {
        _syncing = true;
        ModeA = a;
        ModeC = c;
        ModeS = s;
        Es1090 = es;
        _syncing = false;
        SendControl(ident: false);
    }

    [RelayCommand]
    private void Ident()
    {
        if (IsActive)
        {
            SendControl(ident: true);
            return;
        }

        // other transponders (Sagetech...) take the generic command
        var cmd = new VehicleCommand(MAVLink.MAV_CMD.DO_ADSB_OUT_IDENT);
        _ = _commands.RunAsync("IDENT", (mav, sysid, compid, _) => cmd.Send(mav, sysid, compid), "IDENT sent",
            "The vehicle rejected DO_ADSB_OUT_IDENT");
    }

    /// <summary>Flight ID edited and confirmed (Enter / focus lost).</summary>
    [RelayCommand]
    private void ApplyFlightId()
    {
        if (FlightId.Length > 8)
            FlightId = FlightId[..8];
        if (IsActive)
            SendControl(ident: false);
    }

    public MAVLink.mavlink_uavionix_adsb_out_control_t ControlMessage(bool ident) =>
        TransponderControl.Build((int)(Squawk ?? 1200), TransponderControl.State(ModeA, ModeC, ModeS, Es1090, ident), FlightId);

    private void SendControl(bool ident)
    {
        if (!_commands.IsConnected)
        {
            _commands.Report("Connect to a vehicle first.", true);
            return;
        }

        var mav = Services.Session.Mav;
        var vehicle = Services.Session.Vehicle;
        var msg = ControlMessage(ident);
        var sysid = vehicle.sysid;
        var compid = vehicle.compid;
        _ = Task.Run(() => mav.sendPacket(msg, sysid, compid));
        _commands.Report(ident ? "IDENT sent" : $"Transponder: {ModeName}, squawk {(int)(Squawk ?? 0):0000}", false);
    }
}
