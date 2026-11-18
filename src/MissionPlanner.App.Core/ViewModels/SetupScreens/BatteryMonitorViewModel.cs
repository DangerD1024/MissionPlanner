using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>
/// Battery Monitor (ConfigBatteryMonitoring): monitor type, capacity, sensor and board presets,
/// pins and multipliers, and calibration of the voltage multiplier / amps-per-volt from a meter reading.
/// </summary>
public sealed partial class BatteryMonitorViewModel : HardwarePageViewModel
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(BatteryMonitorViewModel));

    private bool _syncing;

    public BatteryMonitorViewModel(AppServices services)
        : this(services, null)
    {
    }

    internal BatteryMonitorViewModel(AppServices services, IParamSource? source)
        : base("Battery Monitor", services, source)
    {
    }

    public IReadOnlyList<BatterySensorPreset> Sensors => BatteryLogic.Sensors;

    public IReadOnlyList<BatteryBoardPreset> Boards => BatteryLogic.Boards;

    [ObservableProperty]
    private ParamField? _monitor;

    [ObservableProperty]
    private ParamField? _capacity;

    [ObservableProperty]
    private ParamField? _voltPin;

    [ObservableProperty]
    private ParamField? _currPin;

    [ObservableProperty]
    private ParamField? _voltMult;

    [ObservableProperty]
    private ParamField? _ampPerVolt;

    [ObservableProperty]
    private ParamField? _ampOffset;

    [ObservableProperty]
    private BatterySensorPreset? _selectedSensor;

    [ObservableProperty]
    private BatteryBoardPreset? _selectedBoard;

    /// <summary>A monitor is configured (BATT_MONITOR is not 0).</summary>
    [ObservableProperty]
    private bool _isEnabled;

    /// <summary>Analog sensor (3 or 4): presets, pins and calibration apply.</summary>
    [ObservableProperty]
    private bool _isAnalog;

    [ObservableProperty]
    private bool _hasCurrent;

    [ObservableProperty]
    private string _voltageText = "";

    [ObservableProperty]
    private string _currentText = "";

    [ObservableProperty]
    private decimal? _measuredVoltage;

    [ObservableProperty]
    private decimal? _measuredCurrent;

    [ObservableProperty]
    private string _calibrationNote = "";

    protected override void Build()
    {
        Monitor = Field("BATT_MONITOR", new ParamFieldSpec { Kind = ParamEditorKind.Options, Label = "Monitor" });
        Capacity = Field("BATT_CAPACITY", new ParamFieldSpec { Kind = ParamEditorKind.Number, Label = "Battery Capacity", Min = 0, Max = 1000000, Increment = 50 });
        VoltPin = Field("BATT_VOLT_PIN", new ParamFieldSpec { Label = "Voltage pin" });
        CurrPin = Field("BATT_CURR_PIN", new ParamFieldSpec { Label = "Current pin" });
        VoltMult = Field(BatteryLogic.VoltMultNames, new ParamFieldSpec { Kind = ParamEditorKind.Number, Label = "3. Voltage multiplier", Min = 0, Max = 1000, Increment = 0.01 });
        AmpPerVolt = Field(BatteryLogic.AmpPerVoltNames, new ParamFieldSpec { Kind = ParamEditorKind.Number, Label = "6. Amperes per volt", Min = -1000, Max = 1000, Increment = 0.01 });
        AmpOffset = Field("BATT_AMP_OFFSET", new ParamFieldSpec { Kind = ParamEditorKind.Number, Label = "7. Amps offset" });

        if (Monitor != null)
            Monitor.Written += OnMonitorWritten;

        MeasuredVoltage = null;
        MeasuredCurrent = null;
        CalibrationNote = "";
        Status = Monitor == null ? "This vehicle has no BATT_MONITOR parameter." : null;
        OnValuesRefreshed();
    }

    protected override void OnValuesRefreshed()
    {
        var monitor = Monitor?.Value ?? 0;
        IsEnabled = monitor != BatteryLogic.MonitorDisabled;
        IsAnalog = BatteryLogic.IsAnalog(monitor);
        HasCurrent = BatteryLogic.MeasuresCurrent(monitor);

        _syncing = true;
        try
        {
            SelectedSensor = BatteryLogic.DetectSensor(AmpPerVolt?.Value, VoltMult?.Value);
            SelectedBoard = BatteryLogic.DetectBoard(VoltPin?.Value, CurrPin?.Value);
        }
        finally
        {
            _syncing = false;
        }
    }

    protected override void UpdateLive()
    {
        var cs = Services.Session.Cs;
        VoltageText = cs.battery_voltage.ToString("0.00", CultureInfo.InvariantCulture) + " V";
        CurrentText = cs.current.ToString("0.00", CultureInfo.InvariantCulture) + " A";
    }

    private async void OnMonitorWritten(object? sender, ParamWrittenEventArgs e)
    {
        try
        {
            if (e.NewValue == BatteryLogic.MonitorDisabled)
            {
                // as Mission Planner: an unused analog sensor has no pins
                if (Source.Has("BATT_VOLT_PIN"))
                    await WriteAsync("BATT_VOLT_PIN", -1);
                if (Source.Has("BATT_CURR_PIN"))
                    await WriteAsync("BATT_CURR_PIN", -1);
            }
            else if (e.OldValue == 0)
            {
                // enabling a monitor adds the BATT_ parameters that go with it
                IsBusy = true;
                Status = "Getting parameters…";
                await Services.Params.RefreshAsync();
                Status = "Battery monitor enabled. Reboot the autopilot if its settings do not appear.";
            }
        }
        catch (Exception ex)
        {
            Log.Warn("battery monitor", ex);
            Status = "Could not refresh parameters: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            OnValuesRefreshed();
        }
    }

    partial void OnSelectedSensorChanged(BatterySensorPreset? value)
    {
        if (_syncing || value?.AmpPerVolt is not { } apv || value.VoltMult is not { } vm)
            return;
        _ = ApplySensorAsync(apv, vm);
    }

    private async Task ApplySensorAsync(double ampPerVolt, double voltMult)
    {
        if (VoltMult != null)
            await VoltMult.WriteAsync(voltMult);
        if (AmpPerVolt != null)
            await AmpPerVolt.WriteAsync(ampPerVolt);
        OnValuesRefreshed();
    }

    partial void OnSelectedBoardChanged(BatteryBoardPreset? value)
    {
        if (_syncing || value == null)
            return;
        _ = ApplyBoardAsync(value);
    }

    private async Task ApplyBoardAsync(BatteryBoardPreset board)
    {
        if (VoltPin != null)
            await VoltPin.WriteAsync(board.VoltPin);
        if (CurrPin != null)
            await CurrPin.WriteAsync(board.CurrPin);
        OnValuesRefreshed();
    }

    [RelayCommand]
    private async Task CalibrateVoltageAsync()
    {
        if (VoltMult?.Value is not { } mult || MeasuredVoltage is not { } measured)
            return;
        var reported = Services.Session.Cs.battery_voltage;
        if (BatteryLogic.CalibrateVoltMult((double)measured, reported, mult) is not { } updated)
        {
            CalibrationNote = "Calibration needs a measured voltage and a non-zero voltage from the autopilot.";
            return;
        }

        if (await VoltMult.WriteAsync(updated))
            CalibrationNote = $"{VoltMult.Name} {VoltMult.Format(mult)} → {VoltMult.Format(updated)}";
        OnValuesRefreshed();
    }

    [RelayCommand]
    private async Task CalibrateCurrentAsync()
    {
        if (AmpPerVolt?.Value is not { } apv || MeasuredCurrent is not { } measured)
            return;
        var reported = Services.Session.Cs.current;
        if (BatteryLogic.CalibrateAmpPerVolt((double)measured, reported, apv) is not { } updated)
        {
            CalibrationNote = "Calibration needs a measured current and a non-zero current from the autopilot.";
            return;
        }

        if (await AmpPerVolt.WriteAsync(updated))
            CalibrationNote = $"{AmpPerVolt.Name} {AmpPerVolt.Format(apv)} → {AmpPerVolt.Format(updated)}";
        OnValuesRefreshed();
    }
}
