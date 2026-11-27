using System.Globalization;
using log4net;
using MissionPlanner.ArduPilot;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.FlightData;

/// <summary>
/// Mission Planner's speech settings: the same keys in the settings file, so the library's own
/// announcements (mode change and waypoint in CurrentState, status texts in MAVLinkInterface)
/// follow them too. Texts take Mission Planner's {mode}, {wpn}, {alt}, {batv}... placeholders.
/// </summary>
public static class SpeechOptions
{
    public const string EnableKey = "speechenable";
    public const string ArmedOnlyKey = "speech_armed_only";

    public static readonly (string Enabled, string Text, string Default) Waypoint = ("speechwaypointenabled", "speechwaypoint", "Heading to Waypoint {wpn}");
    public static readonly (string Enabled, string Text, string Default) Mode = ("speechmodeenabled", "speechmode", "Mode changed to {mode}");
    public static readonly (string Enabled, string Text, string Default) Custom = ("speechcustomenabled", "speechcustom", "Heading to Waypoint {wpn}, altitude is {alt}, Ground speed is {gsp} ");
    public static readonly (string Enabled, string Text, string Default) Battery = ("speechbatteryenabled", "speechbattery", "WARNING, Battery at {batv} Volt, {batp} percent");
    public static readonly (string Enabled, string Text, string Default) Altitude = ("speechaltenabled", "speechalt", "WARNING, low altitude {alt}");
    public static readonly (string Enabled, string Text, string Default) Arm = ("speecharmenabled", "speecharm", "Armed");
    public const string DisarmKey = "speechdisarm";
    public const string DisarmDefault = "Disarmed";
    public static readonly (string Enabled, string Text, string Default) LowGroundSpeed = ("speechlowspeedenabled", "speechlowgroundspeed", "Low Ground Speed {gsp}");
    public const string LowAirSpeedKey = "speechlowairspeed";
    public const string LowAirSpeedDefault = "Low Air Speed {asp}";

    public const string BatteryVoltKey = "speechbatteryvolt";
    public const string BatteryPercentKey = "speechbatterypercent";
    public const string AltitudeHeightKey = "speechaltheight";
    public const string LowGroundSpeedTriggerKey = "speechlowgroundspeedtrigger";
    public const string LowAirSpeedTriggerKey = "speechlowairspeedtrigger";

    /// <summary>New in this app (Mission Planner always warns): the "No Data" warning when the link goes quiet while armed.</summary>
    public const string LinkLostKey = "next_speechlinklost";

    public static bool Enabled => Settings.Instance.GetBoolean(EnableKey);

    /// <summary>Status texts at or above this MAV_SEVERITY are spoken; the library reads the key itself.</summary>
    public const string SeverityKey = "severity";

    public static int Severity => Math.Clamp(Settings.Instance.GetInt32(SeverityKey, 4), 0, 7);

    /// <summary>MAV_SEVERITY names in order (0 = Emergency), the choices for <see cref="Severity"/>.</summary>
    public static IReadOnlyList<string> Severities { get; } =
        ["Emergency", "Alert", "Critical", "Error", "Warning", "Notice", "Info", "Debug"];

    public static bool ArmedOnly => Settings.Instance.GetBoolean(ArmedOnlyKey);

    public static bool On(string key, bool fallback = false) => Settings.Instance.GetBoolean(key, fallback);

    public static string Text(string key, string fallback) => Settings.Instance[key] is { Length: > 0 } s ? s : fallback;

    public static double Number(string key, double fallback) =>
        double.TryParse(Settings.Instance[key], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ||
        double.TryParse(Settings.Instance[key], NumberStyles.Float, CultureInfo.CurrentCulture, out v)
            ? v
            : fallback;
}

/// <summary>What the announcer looks at, once per step (a plain snapshot, so the logic can be tested).</summary>
public readonly record struct AnnouncerInput(
    bool LinkOpen,
    bool Armed,
    double Altitude,
    double Airspeed,
    double Groundspeed,
    double BatteryVoltage,
    double BatteryRemaining,
    TimeSpan SinceLastPacket,
    TimeSpan SinceConnect);

/// <summary>
/// The periodic announcements of Mission Planner's serial loop: custom text and battery every 30 s,
/// low speed every 10 s, the low-altitude warning, arm / disarm, and "No Data" when the link goes
/// quiet while armed. Mode and waypoint announcements come from the library itself.
/// </summary>
public sealed class SpeechAnnouncer
{
    private readonly ISpeech _speech;
    private readonly Func<string, string> _expand;
    private DateTime _customAt;
    private DateTime _lowSpeedAt;
    private DateTime _altitudeAt = DateTime.MinValue;
    private DateTime _noDataAt = DateTime.MinValue;
    private bool? _armed;
    private double _altitudeMax;

    /// <param name="speech">Where to speak.</param>
    /// <param name="expand">Fills in the {placeholders} (Mission Planner's speechConversion for the vehicle).</param>
    /// <param name="start">The clock's start, so the 30 s and 10 s intervals count from then.</param>
    public SpeechAnnouncer(ISpeech speech, Func<string, string> expand, DateTime start)
    {
        _speech = speech;
        _expand = expand;
        _customAt = start;
        _lowSpeedAt = start;
    }

    /// <summary>The "No Data" text, for the HUD.</summary>
    public string? LastWarning { get; private set; }

    public void Step(AnnouncerInput s, DateTime now)
    {
        LastWarning = null;
        if (!s.LinkOpen)
        {
            _altitudeMax = 0;
            _armed = null;
            return;
        }

        // the link went quiet: wait 3 s, ignore the first 30 s after connecting, repeat every 5 s.
        // Shown on the HUD even with speech off, as Mission Planner does.
        if (s.Armed && s.SinceLastPacket.TotalSeconds > 3 && s.SinceConnect.TotalSeconds > 30 && (now - _noDataAt).TotalSeconds > 5)
        {
            _noDataAt = now;
            LastWarning = $"WARNING No Data for {(int)s.SinceLastPacket.TotalSeconds} Seconds";
            if (_speech.speechEnable && SpeechOptions.On(SpeechOptions.LinkLostKey, true))
                _speech.SpeakAsync(LastWarning);
        }

        if (!_speech.speechEnable)
        {
            _armed = s.Armed;
            return;
        }

        // Mission Planner's speechEnabled(): "armed only" mutes everything but arm / disarm while disarmed
        var active = !SpeechOptions.ArmedOnly || s.Armed;

        if (_armed is { } was && was != s.Armed && SpeechOptions.On(SpeechOptions.Arm.Enabled))
        {
            var text = s.Armed
                ? SpeechOptions.Text(SpeechOptions.Arm.Text, SpeechOptions.Arm.Default)
                : SpeechOptions.Text(SpeechOptions.DisarmKey, SpeechOptions.DisarmDefault);
            _speech.SpeakAsync(_expand(text));
        }

        _armed = s.Armed;

        if (!active)
            return;

        if ((now - _customAt).TotalSeconds > 30 && _speech.IsReady)
        {
            _customAt = now;
            if (SpeechOptions.On(SpeechOptions.Custom.Enabled))
                _speech.SpeakAsync(_expand(SpeechOptions.Text(SpeechOptions.Custom.Text, SpeechOptions.Custom.Default)));

            if (SpeechOptions.On(SpeechOptions.Battery.Enabled) && BatteryLow(s))
                _speech.SpeakAsync(_expand(SpeechOptions.Text(SpeechOptions.Battery.Text, SpeechOptions.Battery.Default)));
        }

        if ((now - _lowSpeedAt).TotalSeconds > 10 && SpeechOptions.On(SpeechOptions.LowGroundSpeed.Enabled) && s.Armed)
        {
            var air = SpeechOptions.Number(SpeechOptions.LowAirSpeedTriggerKey, 0);
            var ground = SpeechOptions.Number(SpeechOptions.LowGroundSpeedTriggerKey, 0);
            if (s.Airspeed < air && _speech.IsReady)
            {
                _speech.SpeakAsync(_expand(SpeechOptions.Text(SpeechOptions.LowAirSpeedKey, SpeechOptions.LowAirSpeedDefault)));
                _lowSpeedAt = now;
            }
            else if (s.Groundspeed < ground && _speech.IsReady)
            {
                _speech.SpeakAsync(_expand(SpeechOptions.Text(SpeechOptions.LowGroundSpeed.Text, SpeechOptions.LowGroundSpeed.Default)));
                _lowSpeedAt = now;
            }
            else if (s.Airspeed >= air && s.Groundspeed >= ground)
            {
                _lowSpeedAt = now;
            }
        }

        // low altitude: only after the vehicle has been above the warning height
        var warnAlt = SpeechOptions.Number(SpeechOptions.AltitudeHeightKey, double.MaxValue) * CurrentState.multiplieralt;
        _altitudeMax = Math.Max(_altitudeMax, s.Altitude);
        if (SpeechOptions.On(SpeechOptions.Altitude.Enabled) && s.Armed && s.Altitude != 0 && s.Altitude <= warnAlt &&
            _altitudeMax > warnAlt && (now - _altitudeAt).TotalSeconds >= 5 && _speech.IsReady)
        {
            _altitudeAt = now;
            _speech.SpeakAsync(_expand(SpeechOptions.Text(SpeechOptions.Altitude.Text, SpeechOptions.Altitude.Default)));
        }
    }

    /// <summary>Below the warning voltage, or below the warning percentage (with a real battery reading).</summary>
    internal static bool BatteryLow(AnnouncerInput s)
    {
        var volt = SpeechOptions.Number(SpeechOptions.BatteryVoltKey, 9.6);
        var percent = SpeechOptions.Number(SpeechOptions.BatteryPercentKey, 20);
        if (s.BatteryVoltage >= 5.0 && s.BatteryVoltage <= volt)
            return true;
        return s.BatteryVoltage >= 5.0 && s.BatteryRemaining != 0 && s.BatteryRemaining < percent;
    }
}

/// <summary>
/// Installs the app's speech: <see cref="MAVLinkInterface.Speech"/> and <see cref="CurrentState.Speech"/>
/// (the library's mode, waypoint and status text announcements) and a background announcer at
/// 2 Hz that runs whichever screen is showing. Nothing here ever blocks the link's reader.
/// </summary>
public static class AppSpeech
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(AppSpeech));
    private static readonly object Gate = new();
    private static Session.VehicleSession? _session;
    private static SpeechAnnouncer? _announcer;
    private static Timer? _timer;
    private static int _busy;

    public static SpeechEngine Engine { get; } = new();

    /// <summary>Hooks speech up for a session (the latest call wins); safe to call more than once.</summary>
    public static void Install(Session.VehicleSession session)
    {
        lock (Gate)
        {
            Engine.speechEnable = SpeechOptions.Enabled;
            MAVLinkInterface.Speech = Engine;
            CurrentState.Speech = Engine;
            if (ReferenceEquals(_session, session))
                return;
            _session = session;
            _announcer = new SpeechAnnouncer(Engine, Expand, DateTime.UtcNow);
            session.StateChanged += (_, state) =>
            {
                if (state == Session.LinkState.Disconnected)
                    Engine.SpeakAsyncCancelAll();
            };
            _timer ??= new Timer(_ => Step(), null, 500, 500);
        }
    }

    /// <summary>Re-reads the master switch after the settings changed.</summary>
    public static void ApplySettings() => Engine.speechEnable = SpeechOptions.Enabled;

    /// <summary>Re-reads the settings (the Speech dialog saved them) and tells every switch that shows them.</summary>
    public static void ApplySettings(AppServices services)
    {
        ApplySettings();
        services.OnSpeechSettingsChanged();
    }

    /// <summary>
    /// Mission Planner's "Enable Speech", wherever the switch is (SETTINGS, DATA > Actions): saved under its
    /// key, applied, and announced so the other switches follow. Turning it off stops what is being said.
    /// </summary>
    public static void SetEnabled(AppServices services, bool on)
    {
        Settings.Instance[SpeechOptions.EnableKey] = on.ToString(); // "True" / "False", as the library compares
        Save();
        ApplySettings();
        if (!on)
            Engine.SpeakAsyncCancelAll();
        services.OnSpeechSettingsChanged();
    }

    /// <summary>The lowest severity of vehicle messages that is spoken (0 Emergency ... 7 Debug).</summary>
    public static void SetSeverity(AppServices services, int severity)
    {
        Settings.Instance[SpeechOptions.SeverityKey] = Math.Clamp(severity, 0, 7).ToString(CultureInfo.InvariantCulture);
        Save();
        services.OnSpeechSettingsChanged();
    }

    /// <summary>Which program speaks, for the speech settings.</summary>
    public static string BackendText => Engine.BackendName is { } name
        ? $"Speech uses {name}."
        : SpeechBackend.Current switch
        {
            SpeechBackend.Os.Linux => "No speech program found: install speech-dispatcher (spd-say) or espeak-ng.",
            SpeechBackend.Os.MacOs => "No speech program found (the system's 'say').",
            _ => "No speech program found (Windows PowerShell with System.Speech).",
        };

    private static void Save()
    {
        try
        {
            Settings.Instance.Save();
        }
        catch (Exception ex)
        {
            // kept in memory; saved with the next settings write
            Log.Warn("settings save", ex);
        }
    }

    /// <summary>The engine or voice settings changed: retry a voice that failed, load the chosen one.</summary>
    public static void ApplyVoiceSettings()
    {
        Engine.Neural?.Reset();
        if (Engine.speechEnable)
            Engine.Prepare();
    }

    /// <summary>
    /// App exit: stop talking (and any player program) at once. The voice model is left to the process exit,
    /// so closing never waits for a sentence being prepared.
    /// </summary>
    public static void Shutdown()
    {
        Engine.speechEnable = false;
        Engine.SpeakAsyncCancelAll();
        Speech.ProcessAudioPlayer.CleanUp();
    }

    private static string Expand(string text)
    {
        var s = _session;
        if (s == null)
            return text;
        try
        {
            return Common.speechConversion(s.Vehicle, text);
        }
        catch (Exception ex)
        {
            Log.Info("speech text", ex);
            return text;
        }
    }

    private static void Step()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
            return;
        try
        {
            Session.VehicleSession? s;
            SpeechAnnouncer? a;
            lock (Gate)
            {
                s = _session;
                a = _announcer;
            }

            if (s == null || a == null)
                return;
            var mav = s.Vehicle;
            var cs = mav.cs;
            var now = DateTime.UtcNow;
            var open = s.IsConnected || s.Mav.logreadmode;
            var last = mav.lastvalidpacket == DateTime.MinValue ? TimeSpan.Zero : now - mav.lastvalidpacket;
            a.Step(new AnnouncerInput(open, cs.armed, cs.alt, cs.airspeed, cs.groundspeed, cs.battery_voltage,
                cs.battery_remaining, s.Mav.logreadmode ? TimeSpan.Zero : last,
                s.IsConnected ? DateTime.Now - s.ConnectedAt : TimeSpan.Zero), now);
            if (a.LastWarning != null)
                cs.messageHigh = a.LastWarning;

            // TOOLS > Warning Manager: the user's own warnings (Mission Planner's WarningEngine)
            if (open)
            {
                if (Tools.AppWarnings.Step(cs, Engine, DateTime.Now) is { } warning)
                    cs.messageHigh = warning;
            }
            else
            {
                Tools.AppWarnings.Clear();
            }
        }
        catch (Exception ex)
        {
            Log.Info("announcer", ex);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }
}
