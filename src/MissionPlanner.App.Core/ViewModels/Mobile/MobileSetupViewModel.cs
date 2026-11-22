using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.ViewModels.FlightData;
using MissionPlanner.App.Core.ViewModels.SetupScreens;

namespace MissionPlanner.App.Core.ViewModels.Mobile;

/// <summary>One row of the phone's Setup list.</summary>
public sealed partial class MobileSetupEntry : ObservableObject
{
    private readonly Func<PageViewModel?> _create;
    private readonly Func<bool> _available;

    public MobileSetupEntry(string title, string subtitle, string icon, Func<PageViewModel?> create, Func<bool> available)
    {
        Title = title;
        Subtitle = subtitle;
        Icon = icon;
        _create = create;
        _available = available;
    }

    public string Title { get; }

    public string Subtitle { get; }

    /// <summary>The theme's icon key (IcCompass...).</summary>
    public string Icon { get; }

    [ObservableProperty]
    private bool _isAvailable;

    internal void Refresh() => IsAvailable = _available();

    internal PageViewModel? Create() => _create();
}

/// <summary>
/// The phone's Setup tab: calibrations (accelerometer and level, compass, radio), sensors,
/// flight modes, failsafe, battery, frame, motor test and the parameter list. The pages are the
/// desktop SETUP view-models; the phone draws its own views for them.
/// </summary>
public sealed partial class MobileSetupViewModel : ObservableObject
{
    private readonly AppServices _services;

    public MobileSetupViewModel(AppServices services, MobileParamsViewModel parameters)
    {
        _services = services;
        Parameters = parameters;
        var s = services;
        bool Connected() => s.Session.IsConnected;
        bool Ready() => s.Session.IsConnected && s.Params.HasParams;
        bool Has(params string[] names) => Ready() && names.Any(s.Params.Has);

        Entries =
        [
            new("Accelerometer", "6-position calibration and level", "IcLevel", () => new AccelCalibrationViewModel(s), Connected),
            new("Compass", "Onboard calibration, priority", "IcCompass", () => new CompassViewModel(s), Ready),
            new("Radio", "Calibrate the transmitter sticks and switches", "IcRadio", () => new RadioCalibrationViewModel(s), Connected),
            new("Sensors", "IMUs, compasses, RC in and servo out, live", "IcSensors", () => new RawSensorsViewModel(s), Connected),
            new("Flight modes", "The six modes of the mode switch", "IcFly", () => new FlightModesViewModel(s), () => Has("FLTMODE1", "MODE1")),
            new("Failsafe", "Radio, battery and GCS failsafe actions", "IcShield", () => new FailsafeViewModel(s), Ready),
            new("Battery", "Monitor, capacity, voltage and current", "IcBattery", () => new BatteryMonitorViewModel(s), () => Has("BATT_MONITOR")),
            new("Frame", "Frame class and type", "IcFrame", () => new FrameTypeViewModel(s), () => Has("FRAME_CLASS", "Q_FRAME_CLASS", "FRAME")),
            new("Motor test", "Spin each motor (propellers off!)", "IcMotor", () => new MotorTestViewModel(s),
                () => Has("FRAME_CLASS", "Q_FRAME_CLASS", "FRAME", "FRAME_TYPE")),
            new("Parameters", "Every parameter, searchable", "IcParams", () => null, Ready),
        ];
        Refresh();
    }

    public IReadOnlyList<MobileSetupEntry> Entries { get; }

    public MobileParamsViewModel Parameters { get; }

    /// <summary>The open page (null: the list, or the parameter list when <see cref="IsParameters"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsList), nameof(Title))]
    private PageViewModel? _current;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsList), nameof(Title))]
    private bool _isParameters;

    public bool IsList => Current == null && !IsParameters;

    public string Title => IsParameters ? "Parameters" : Current?.Title ?? "Setup";

    [ObservableProperty]
    private string _hint = "";

    public void Refresh()
    {
        foreach (var e in Entries)
            e.Refresh();
        Hint = !_services.Session.IsConnected
            ? "Connect to a vehicle to set it up."
            : !_services.Params.HasParams ? "Getting the parameters…" : "";
    }

    [RelayCommand]
    private void Open(MobileSetupEntry? entry)
    {
        if (entry == null)
            return;
        entry.Refresh();
        if (!entry.IsAvailable)
        {
            _services.Notify(_services.Session.IsConnected
                ? $"{entry.Title} is not available for this vehicle (or the parameters are still loading)."
                : "Connect to a vehicle first.", MessageKind.Warning);
            return;
        }

        Close();
        if (entry.Title == "Parameters")
        {
            Parameters.Reload();
            IsParameters = true;
            return;
        }

        var page = entry.Create();
        if (page == null)
            return;
        Current = page;
        page.OnShown();
    }

    /// <summary>Back to the list.</summary>
    [RelayCommand]
    public void Close()
    {
        IsParameters = false;
        if (Current is { } page)
        {
            Current = null;
            page.OnHidden();
        }
    }

    /// <summary>Back: true when there was a page to leave.</summary>
    public bool Back()
    {
        if (IsList)
            return false;
        Close();
        return true;
    }

    /// <summary>While the Setup tab is shown (20 Hz).</summary>
    public void Tick() => Current?.Tick();
}
