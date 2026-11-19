using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;
using MissionPlanner.App.Core.ViewModels.SetupScreens.OptionalHardware;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>
/// Airspeed (ConfigHWAirspeed): enable, each sensor's type, use, pin and calibration settings, the
/// live airspeed and ratio, and the pre-flight offset calibration. Edits write at once.
/// </summary>
public sealed partial class AirspeedViewModel : HardwarePageViewModel
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(AirspeedViewModel));

    /// <summary>Per-sensor settings, in display order (ARSPD_TUBE_ORDER is the old name).</summary>
    internal static readonly IReadOnlyList<string[]> InstanceKeys =
    [
        ["USE"], ["PIN"], ["BUS"], ["RATIO"], ["OFFSET"], ["AUTOCAL"], ["TUBE_ORDR", "TUBE_ORDER"], ["SKIP_CAL"], ["PSI_RANGE"], ["DEVID"],
    ];

    /// <summary>Mission Planner's ARSPD_PIN choices, used when the documentation lists none.</summary>
    internal static readonly IReadOnlyList<ParamOption> LegacyPins =
    [
        .. Enumerable.Range(0, 10).Select(i => new ParamOption(i, "APM 2 analog pin " + i.ToString(CultureInfo.InvariantCulture))),
        new(64, "APM 1 AS Port"),
        new(11, "PX4 Analog AS Port"),
        new(15, "Pixhawk Analog AS Port"),
        new(65, "PX4/Pixhawk EagleTree or MEAS I2C AS Sensor"),
    ];

    public AirspeedViewModel(AppServices services)
        : this(services, null)
    {
    }

    internal AirspeedViewModel(AppServices services, IParamSource? source)
        : base("Airspeed", services, source)
    {
    }

    /// <summary>ARSPD_ENABLE (older firmware only).</summary>
    [ObservableProperty]
    private ParamField? _enable;

    [ObservableProperty]
    private IReadOnlyList<ParamField> _general = [];

    [ObservableProperty]
    private IReadOnlyList<InstancePanel> _sensors = [];

    [ObservableProperty]
    private InstancePanel? _selected;

    [ObservableProperty]
    private bool _hasSeveralSensors;

    [ObservableProperty]
    private bool _hasGeneral;

    [ObservableProperty]
    private string _airspeedText = "";

    [ObservableProperty]
    private string _ratioText = "";

    [ObservableProperty]
    private string _temperatureText = "";

    [ObservableProperty]
    private bool _isArmed;

    [ObservableProperty]
    private string _calibrationNote = "";

    protected override void Build()
    {
        var names = Source.Names();
        var found = SensorInstances.Find(Source.Has, "ARSPD", "TYPE", 6);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var panels = new List<InstancePanel>();
        foreach (var inst in found)
        {
            var type = Field(inst.Prefix + "TYPE", new ParamFieldSpec { Kind = ParamEditorKind.Options, Label = "Type" });
            var main = new List<ParamField>();
            foreach (var aliases in InstanceKeys)
            {
                var name = aliases.Select(k => inst.Prefix + k).FirstOrDefault(Source.Has);
                if (name == null)
                    continue;
                used.Add(name);
                var spec = aliases[0] switch
                {
                    "PIN" when Source.Meta(name) is not { Values.Count: > 0 } => new ParamFieldSpec { Kind = ParamEditorKind.Options, Options = LegacyPins },
                    "USE" when Source.Meta(name) is not { Values.Count: > 0 } => new ParamFieldSpec { Kind = ParamEditorKind.Toggle, Label = "Use Airspeed" },
                    _ => null,
                };
                if (Field(name, spec) is { } f)
                    main.Add(f);
            }

            used.Add(inst.Prefix + "TYPE");
            var panel = new InstancePanel(inst, inst.Title("Airspeed sensor"), type, main, []);
            if (type != null)
                type.Written += (_, e) =>
                {
                    panel.Update();
                    if (e.OldValue != e.NewValue)
                        RebootRequired = true;
                };
            panels.Add(panel);
        }

        Enable = Field("ARSPD_ENABLE", new ParamFieldSpec { Kind = ParamEditorKind.Toggle, Label = "Enable" });
        used.Add("ARSPD_ENABLE");
        General = names
            .Where(n => n.StartsWith("ARSPD_", StringComparison.Ordinal) && !used.Contains(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => Field(n))
            .OfType<ParamField>()
            .ToList();

        var keep = Selected?.Number;
        Sensors = panels;
        HasSeveralSensors = panels.Count > 1;
        HasGeneral = General.Count > 0;
        Selected = panels.FirstOrDefault(p => p.Number == keep) ?? panels.FirstOrDefault();
        Status = panels.Count == 0 ? "This vehicle has no ARSPD_TYPE parameter." : null;
    }

    protected override void OnValuesRefreshed()
    {
        foreach (var p in Sensors)
            p.Update();
    }

    protected override void UpdateLive()
    {
        var cs = Services.Session.Cs;
        var unit = CurrentState.SpeedUnit is { Length: > 0 } u ? u : "m/s";
        AirspeedText = cs.airspeed.ToString("0.0", CultureInfo.InvariantCulture) + " " + unit;
        RatioText = cs.asratio.ToString("0.00", CultureInfo.InvariantCulture);
        TemperatureText = cs.airspeed1_temp.ToString("0.0", CultureInfo.InvariantCulture) + " °C";
        IsArmed = cs.armed;
        foreach (var p in Sensors)
            p.SetReading(p.Number == 1 && p.IsEnabled ? AirspeedText : null);
    }

    /// <summary>
    /// Zeroes the airspeed offset (and the barometer) with the vehicle still: MAV_CMD_PREFLIGHT_CALIBRATION
    /// with the ground-pressure flag, which ArduPlane answers by calibrating both.
    /// </summary>
    [RelayCommand]
    private async Task CalibrateAsync()
    {
        if (!Services.Session.IsConnected)
            return;
        if (Services.Session.Cs.armed)
        {
            CalibrationNote = "Disarm before calibrating.";
            return;
        }

        if (!await Services.Dialogs.ConfirmAsync("Calibrate airspeed",
                "Cover the pitot tube from wind and keep the vehicle still. The autopilot re-zeroes the airspeed sensor offset and the barometer.",
                "Calibrate"))
            return;

        IsBusy = true;
        CalibrationNote = "Calibrating…";
        var v = Services.Session.Vehicle;
        var mav = Services.Session.Mav;
        try
        {
            var ok = await Task.Run(() => mav.doCommand(v.sysid, v.compid, MAVLink.MAV_CMD.PREFLIGHT_CALIBRATION, 0, 0, 1, 0, 0, 0, 0));
            CalibrationNote = ok ? "Calibration done: the offset is updated." : "The autopilot refused the calibration.";
        }
        catch (Exception ex)
        {
            Log.Warn("airspeed calibration", ex);
            CalibrationNote = "No answer from the autopilot: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            RefreshFields();
        }
    }
}
