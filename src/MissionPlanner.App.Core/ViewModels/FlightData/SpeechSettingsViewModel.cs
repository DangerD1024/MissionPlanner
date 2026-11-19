using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.FlightData;

/// <summary>
/// Speech options (Mission Planner's Planner page speech check boxes and their prompts, in one
/// dialog): master switch, armed-only, and each announcement with its text and threshold.
/// Saved to the same settings keys Mission Planner uses.
/// </summary>
public sealed partial class SpeechSettingsViewModel : PageViewModel, IDialogPage
{
    public SpeechSettingsViewModel(AppServices services)
        : base("Speech")
    {
        Services = services;
        _enabled = SpeechOptions.Enabled;
        _armedOnly = SpeechOptions.ArmedOnly;

        _waypoint = SpeechOptions.On(SpeechOptions.Waypoint.Enabled);
        _waypointText = SpeechOptions.Text(SpeechOptions.Waypoint.Text, SpeechOptions.Waypoint.Default);
        _mode = SpeechOptions.On(SpeechOptions.Mode.Enabled);
        _modeText = SpeechOptions.Text(SpeechOptions.Mode.Text, SpeechOptions.Mode.Default);
        _armDisarm = SpeechOptions.On(SpeechOptions.Arm.Enabled);
        _armText = SpeechOptions.Text(SpeechOptions.Arm.Text, SpeechOptions.Arm.Default);
        _disarmText = SpeechOptions.Text(SpeechOptions.DisarmKey, SpeechOptions.DisarmDefault);
        _custom = SpeechOptions.On(SpeechOptions.Custom.Enabled);
        _customText = SpeechOptions.Text(SpeechOptions.Custom.Text, SpeechOptions.Custom.Default);
        _battery = SpeechOptions.On(SpeechOptions.Battery.Enabled);
        _batteryText = SpeechOptions.Text(SpeechOptions.Battery.Text, SpeechOptions.Battery.Default);
        _batteryVolt = (decimal)SpeechOptions.Number(SpeechOptions.BatteryVoltKey, 9.6);
        _batteryPercent = (decimal)SpeechOptions.Number(SpeechOptions.BatteryPercentKey, 20);
        _altitude = SpeechOptions.On(SpeechOptions.Altitude.Enabled);
        _altitudeText = SpeechOptions.Text(SpeechOptions.Altitude.Text, SpeechOptions.Altitude.Default);
        // stored in metres, shown in display units
        _altitudeHeight = (decimal)Math.Round(SpeechOptions.Number(SpeechOptions.AltitudeHeightKey, 2) * CurrentState.multiplieralt, 1);
        _lowSpeed = SpeechOptions.On(SpeechOptions.LowGroundSpeed.Enabled);
        _lowGroundText = SpeechOptions.Text(SpeechOptions.LowGroundSpeed.Text, SpeechOptions.LowGroundSpeed.Default);
        _lowGroundTrigger = (decimal)SpeechOptions.Number(SpeechOptions.LowGroundSpeedTriggerKey, 0);
        _lowAirText = SpeechOptions.Text(SpeechOptions.LowAirSpeedKey, SpeechOptions.LowAirSpeedDefault);
        _lowAirTrigger = (decimal)SpeechOptions.Number(SpeechOptions.LowAirSpeedTriggerKey, 0);
        _linkLost = SpeechOptions.On(SpeechOptions.LinkLostKey, true);
        _statusSeverity = Settings.Instance.GetInt32("severity", 4);
    }

    public AppServices Services { get; }

    public event EventHandler<bool>? CloseRequested;

    public string AltitudeUnit => Units.Altitude;

    /// <summary>Which program speaks, for the header.</summary>
    public string BackendText => AppSpeech.Engine.BackendName is { } name
        ? $"Speech uses {name}."
        : SpeechBackend.Current switch
        {
            SpeechBackend.Os.Linux => "No speech program found: install speech-dispatcher (spd-say) or espeak-ng.",
            SpeechBackend.Os.MacOs => "No speech program found (the system's 'say').",
            _ => "No speech program found (Windows PowerShell with System.Speech).",
        };

    public string PlaceholderHelp =>
        "Placeholders: {mode} {wpn} {alt} {wpa} {gsp} {asp} {vsp} {batv} {batp} {curr} {hdop} {satcount} {rssi} {disthome} {timeinair} {altunit} {speedunit}";

    /// <summary>MAV_SEVERITY names: status texts at or above this are spoken (and shown on the HUD).</summary>
    public IReadOnlyList<string> Severities { get; } =
        ["Emergency", "Alert", "Critical", "Error", "Warning", "Notice", "Info", "Debug"];

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private bool _armedOnly;
    [ObservableProperty] private int _statusSeverity;
    [ObservableProperty] private bool _waypoint;
    [ObservableProperty] private string _waypointText;
    [ObservableProperty] private bool _mode;
    [ObservableProperty] private string _modeText;
    [ObservableProperty] private bool _armDisarm;
    [ObservableProperty] private string _armText;
    [ObservableProperty] private string _disarmText;
    [ObservableProperty] private bool _custom;
    [ObservableProperty] private string _customText;
    [ObservableProperty] private bool _battery;
    [ObservableProperty] private string _batteryText;
    [ObservableProperty] private decimal? _batteryVolt;
    [ObservableProperty] private decimal? _batteryPercent;
    [ObservableProperty] private bool _altitude;
    [ObservableProperty] private string _altitudeText;
    [ObservableProperty] private decimal? _altitudeHeight;
    [ObservableProperty] private bool _lowSpeed;
    [ObservableProperty] private string _lowGroundText;
    [ObservableProperty] private decimal? _lowGroundTrigger;
    [ObservableProperty] private string _lowAirText;
    [ObservableProperty] private decimal? _lowAirTrigger;
    [ObservableProperty] private bool _linkLost;

    [RelayCommand]
    private void Test()
    {
        var text = Expand(Services, ModeText);
        AppSpeech.Engine.SpeakNow(text.Length > 0 ? text : "Speech test");
    }

    [RelayCommand]
    private void Save()
    {
        var s = Settings.Instance;
        string B(bool b) => b.ToString(); // "True" / "False", as the library compares
        string N(decimal? d, decimal fallback) => (d ?? fallback).ToString(CultureInfo.InvariantCulture);

        s[SpeechOptions.EnableKey] = B(Enabled);
        s[SpeechOptions.ArmedOnlyKey] = B(ArmedOnly);
        s["severity"] = Math.Clamp(StatusSeverity, 0, 7).ToString(CultureInfo.InvariantCulture);
        s[SpeechOptions.Waypoint.Enabled] = B(Waypoint);
        s[SpeechOptions.Waypoint.Text] = WaypointText;
        s[SpeechOptions.Mode.Enabled] = B(Mode);
        s[SpeechOptions.Mode.Text] = ModeText;
        s[SpeechOptions.Arm.Enabled] = B(ArmDisarm);
        s[SpeechOptions.Arm.Text] = ArmText;
        s[SpeechOptions.DisarmKey] = DisarmText;
        s[SpeechOptions.Custom.Enabled] = B(Custom);
        s[SpeechOptions.Custom.Text] = CustomText;
        s[SpeechOptions.Battery.Enabled] = B(Battery);
        s[SpeechOptions.Battery.Text] = BatteryText;
        s[SpeechOptions.BatteryVoltKey] = N(BatteryVolt, 9.6m);
        s[SpeechOptions.BatteryPercentKey] = N(BatteryPercent, 20);
        s[SpeechOptions.Altitude.Enabled] = B(Altitude);
        s[SpeechOptions.Altitude.Text] = AltitudeText;
        s[SpeechOptions.AltitudeHeightKey] =
            ((double)(AltitudeHeight ?? 2) / CurrentState.multiplieralt).ToString(CultureInfo.InvariantCulture);
        s[SpeechOptions.LowGroundSpeed.Enabled] = B(LowSpeed);
        s[SpeechOptions.LowGroundSpeed.Text] = LowGroundText;
        s[SpeechOptions.LowGroundSpeedTriggerKey] = N(LowGroundTrigger, 0);
        s[SpeechOptions.LowAirSpeedKey] = LowAirText;
        s[SpeechOptions.LowAirSpeedTriggerKey] = N(LowAirTrigger, 0);
        s[SpeechOptions.LinkLostKey] = B(LinkLost);
        try
        {
            s.Save();
        }
        catch (Exception)
        {
            // kept in memory; saved with the next settings write
        }

        AppSpeech.ApplySettings();
        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    private static string Expand(AppServices services, string text)
    {
        try
        {
            return MissionPlanner.ArduPilot.Common.speechConversion(services.Session.Vehicle, text ?? "");
        }
        catch (Exception)
        {
            return text ?? "";
        }
    }
}
