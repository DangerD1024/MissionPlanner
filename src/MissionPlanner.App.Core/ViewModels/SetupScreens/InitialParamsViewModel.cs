using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using MissionPlanner.App.Core.Params;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;
using MissionPlanner.App.Core.ViewModels.SetupScreens.OptionalHardware;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>One calculated parameter against the vehicle's value; the user picks which to write.</summary>
public sealed partial class InitialParamRow : ObservableObject
{
    public InitialParamRow(string name, double value, double? current)
    {
        Name = name;
        Value = value;
        Current = current;
        _include = IsChange;
    }

    public string Name { get; }

    public double Value { get; }

    /// <summary>The vehicle's value; null when it does not have the parameter.</summary>
    public double? Current { get; }

    public bool OnVehicle => Current != null;

    /// <summary>On the vehicle and different (float-precision compare, like the vehicle stores it).</summary>
    public bool IsChange => Current is { } c && (float)c != (float)Value;

    public string ValueText => Format(Value);

    public string CurrentText => Current is { } c ? Format(c) : "—";

    public string Note => !OnVehicle ? "not on this vehicle" : IsChange ? "" : "already set";

    [ObservableProperty]
    private bool _include;

    public static string Format(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
}

/// <summary>
/// Initial Parameters (ConfigInitialParams): calculates a first tune for a new copter or QuadPlane
/// from the propeller size and battery (filters, acceleration limits, thrust expo, battery
/// voltages), previews it against the vehicle's values and writes the chosen ones.
/// </summary>
public sealed partial class InitialParamsViewModel : HardwarePageViewModel
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(InitialParamsViewModel));

    private bool _applyingChemistry;

    public InitialParamsViewModel(AppServices services)
        : this(services, null)
    {
    }

    internal InitialParamsViewModel(AppServices services, IParamSource? source)
        : base("Initial Parameters", services, source)
    {
        ApplyChemistry(BatteryChemistry.LiPo);
    }

    public string TuningGuideUrl => InitialParamsCalculator.TuningGuideUrl;

    public IReadOnlyList<BatteryChemistry> Chemistries { get; } = Enum.GetValues<BatteryChemistry>();

    // Mission Planner starts with a 9" prop and 4 cells
    [ObservableProperty]
    private decimal? _propInches = 9;

    [ObservableProperty]
    private decimal? _cells = 4;

    [ObservableProperty]
    private BatteryChemistry _chemistry = BatteryChemistry.LiPo;

    [ObservableProperty]
    private decimal? _cellMax;

    [ObservableProperty]
    private decimal? _cellMin;

    [ObservableProperty]
    private bool _tMotorFlame;

    [ObservableProperty]
    private bool _addSuggested;

    [ObservableProperty]
    private bool _suggestedAvailable;

    [ObservableProperty]
    private IReadOnlyList<InitialParamRow> _rows = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    private bool _calculated;

    public bool HasPreview => Calculated && Rows.Count > 0;

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private string _inputError = "";

    [ObservableProperty]
    private string _vehicleText = "";

    partial void OnChemistryChanged(BatteryChemistry value) => ApplyChemistry(value);

    private void ApplyChemistry(BatteryChemistry chemistry)
    {
        var (max, min) = InitialParamsCalculator.CellVoltages(chemistry);
        _applyingChemistry = true;
        CellMax = (decimal)max;
        CellMin = (decimal)min;
        _applyingChemistry = false;
        Invalidate();
    }

    partial void OnPropInchesChanged(decimal? value) => Invalidate();

    partial void OnCellsChanged(decimal? value) => Invalidate();

    partial void OnCellMaxChanged(decimal? value)
    {
        if (!_applyingChemistry)
            Invalidate();
    }

    partial void OnCellMinChanged(decimal? value)
    {
        if (!_applyingChemistry)
            Invalidate();
    }

    partial void OnTMotorFlameChanged(bool value) => Invalidate();

    partial void OnAddSuggestedChanged(bool value) => Invalidate();

    // a preview for other inputs would write the wrong values
    private void Invalidate()
    {
        if (!Calculated)
            return;
        Calculated = false;
        Rows = [];
        Summary = "Inputs changed: calculate again.";
    }

    protected override void Build()
    {
        var vehicle = Vehicle();
        SuggestedAvailable = InitialParamsCalculator.SuggestedApplies(vehicle);
        VehicleText = vehicle.IsPlane
            ? "QuadPlane: the Q_A_ / Q_M_ parameters are calculated."
            : "Copter: the ATC_ / MOT_ parameters are calculated.";
        Invalidate();
    }

    internal InitialParamsVehicle Vehicle()
    {
        var cs = Services.Session.Cs;
        // QuadPlane has Q_ENABLE; a copter's ATC_ parameters win over a firmware guess made before the heartbeat
        var isPlane = Source.Has("Q_ENABLE")
                      || (cs.firmware == ArduPilot.Firmwares.ArduPlane && !Source.Names().Any(n => n.StartsWith("ATC_", StringComparison.Ordinal)));
        var major = cs.version?.Major ?? 0;
        if (major <= 0)
            major = 4; // unknown (no AUTOPILOT_VERSION yet): every supported copter is 4.x or later
        return new InitialParamsVehicle(isPlane, major, Source.Has);
    }

    internal InitialParamsInput? Input()
    {
        if (PropInches is not { } prop || Cells is not { } cells || CellMax is not { } max || CellMin is not { } min)
            return null;
        return new InitialParamsInput((double)prop, (int)cells, (double)max, (double)min, TMotorFlame, AddSuggested && SuggestedAvailable);
    }

    [RelayCommand]
    private void Calculate()
    {
        var input = Input();
        var error = input == null ? "Fill in all values." : InitialParamsCalculator.Validate(input);
        InputError = error ?? "";
        if (error != null || input == null)
        {
            Calculated = false;
            Rows = [];
            return;
        }

        var values = InitialParamsCalculator.Calculate(input, Vehicle());
        Rows = values.Select(v => new InitialParamRow(v.Name, v.Value, Source.Get(v.Name))).ToList();
        Calculated = true;
        var changes = Rows.Count(r => r.IsChange);
        var missing = Rows.Count(r => !r.OnVehicle);
        Summary = $"{changes} change{(changes == 1 ? "" : "s")}" +
                  (missing > 0 ? $", {missing} not on this vehicle" : "") +
                  ". Untick any you want to keep, then write.";
        OnPropertyChanged(nameof(HasPreview));
    }

    [RelayCommand]
    private async Task WriteAsync()
    {
        var chosen = Rows.Where(r => r.Include && r.OnVehicle).ToList();
        if (chosen.Count == 0)
        {
            await Services.Dialogs.ShowMessageAsync("Initial parameters", "Nothing selected to write.");
            return;
        }

        if (!await Services.Dialogs.ConfirmAsync("Write to FC",
                $"Write {chosen.Count} parameter{(chosen.Count == 1 ? "" : "s")} to the autopilot?", "Write to FC"))
            return;

        IsBusy = true;
        ParamWriteResult? result = null;
        try
        {
            result = await Services.Params.SetManyAsync(chosen.ToDictionary(r => r.Name, r => r.Value));
        }
        catch (Exception ex)
        {
            Log.Warn("initial params", ex);
            Status = "Write failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            RefreshFields();
        }

        if (result == null)
            return;
        if (result.RebootRequired)
            RebootRequired = true;

        if (result.Failed.Count > 0)
        {
            await Services.Dialogs.ShowMessageAsync("Initial parameters",
                $"{result.Written} written. The vehicle did not accept: {string.Join(", ", result.Failed)}", MessageKind.Warning);
        }
        else
        {
            await Services.Dialogs.ShowMessageAsync("Initial parameter calculator",
                "Initial Parameters successfully updated.\nCheck parameters before flight!\n\n" +
                InitialParamsCalculator.AfterFlightAdvice(Vehicle()) + "\n\nHappy flying!");
        }

        // show the vehicle's values now
        Calculate();
    }
}
