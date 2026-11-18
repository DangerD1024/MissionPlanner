using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.Session;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Calibration;
using MissionPlanner.ArduPilot;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>A row of the compass priority table.</summary>
public sealed partial class CompassRowViewModel : ObservableObject
{
    public CompassRowViewModel(CompassDevice device, string orientation, Action<CompassRowViewModel> up, Action<CompassRowViewModel> down)
    {
        Device = device;
        Orientation = orientation;
        MoveUpCommand = new RelayCommand(() => up(this));
        MoveDownCommand = new RelayCommand(() => down(this));
    }

    public CompassDevice Device { get; }

    [ObservableProperty]
    private int _priority;

    public uint DevId => Device.DevId;

    public string BusType => Device.Id.BusTypeName;

    public int Bus => Device.Id.Bus;

    public int Address => Device.Id.Address;

    public string DevType => Device.Id.DevTypeName(Device.ParamName);

    public bool Missing => Device.Missing;

    public bool External => Device.External;

    public string Orientation { get; }

    public IRelayCommand MoveUpCommand { get; }

    public IRelayCommand MoveDownCommand { get; }
}

/// <summary>One compass's bar during the onboard calibration.</summary>
public sealed partial class MagCalBarViewModel(byte compassId) : ObservableObject
{
    public byte CompassId { get; } = compassId;

    public string Name => "Mag " + (CompassId + 1);

    [ObservableProperty]
    private int _percent;

    [ObservableProperty]
    private string _result = "";

    [ObservableProperty]
    private bool _isSuccess;

    [ObservableProperty]
    private bool _isFailed;
}

/// <summary>A compass on vehicles without the priority list (before ArduPilot 4.1): settings and offsets.</summary>
public sealed partial class LegacyCompassViewModel : ObservableObject
{
    public LegacyCompassViewModel(string title, ParamToggleViewModel use, ParamToggleViewModel external)
    {
        Title = title;
        Use = use;
        External = external;
    }

    public string Title { get; }

    public ParamToggleViewModel Use { get; }

    public ParamToggleViewModel External { get; }

    [ObservableProperty]
    private string _orientation = "";

    [ObservableProperty]
    private string _offsets = "";

    [ObservableProperty]
    private string _motor = "";

    /// <summary>Red: an offset above 600 or all zero (not calibrated); yellow: above 400; green otherwise.</summary>
    [ObservableProperty]
    private StatusKind _offsetsKind;

    public bool OffsetsBad => OffsetsKind == StatusKind.Error;

    public bool OffsetsWarn => OffsetsKind == StatusKind.Warning;

    public bool OffsetsGood => OffsetsKind == StatusKind.Success;

    partial void OnOffsetsKindChanged(StatusKind value)
    {
        OnPropertyChanged(nameof(OffsetsBad));
        OnPropertyChanged(nameof(OffsetsWarn));
        OnPropertyChanged(nameof(OffsetsGood));
    }
}

/// <summary>
/// Compass: Mission Planner's ConfigHWCompass2 (priority list, ArduPilot 4.1+) with
/// ConfigHWCompass's offsets view as the fallback for older firmware. Onboard calibration is the
/// same on both (<see cref="MagCalibrationProcess"/>).
/// </summary>
public sealed partial class CompassViewModel : PageViewModel
{
    private const int OffsetRed = 600;
    private const int OffsetYellow = 400;

    private static readonly ILog Log = LogManager.GetLogger(typeof(CompassViewModel));

    // ArduPilot's documented COMPASS_CAL_FIT values, until the documentation has loaded
    private static readonly ParamOption[] FitnessFallback =
    [
        new(4, "Very Strict"), new(8, "Strict"), new(16, "Default"), new(32, "Relaxed"),
    ];

    private static readonly ParamOption[] PrimaryOptions =
    [
        new(0, "First"), new(1, "Second"), new(2, "Third"),
    ];

    private readonly ICalibrationLink _link;
    private volatile bool _magDirty;
    private List<CompassDevice> _savedOrder = new();

    public CompassViewModel(AppServices services)
        : this(services, new VehicleCalibrationLink(services.Session))
    {
    }

    internal CompassViewModel(AppServices services, ICalibrationLink link)
        : base("Compass")
    {
        Services = services;
        _link = link;
        MagCal = new MagCalibrationProcess(link);
        MagCal.Changed += (_, _) => _magDirty = true;
        MagCal.Completed += (_, _) => services.Dispatcher.Post(() => _ = OnMagCompleteAsync());

        UseCompass =
        [
            new(services, Status, "Use Compass 1", 1, 0, "COMPASS_USE", "COMPASS1_USE"),
            new(services, Status, "Use Compass 2", 1, 0, "COMPASS_USE2", "COMPASS2_USE"),
            new(services, Status, "Use Compass 3", 1, 0, "COMPASS_USE3", "COMPASS3_USE"),
        ];
        Learn = new(services, Status, "Automatically learn offsets", 1, 0, "COMPASS_LEARN");
        AutoDeclination = new(services, Status, "Obtain declination automatically", 1, 0, "COMPASS_AUTODEC");
        Fitness = new(services, MagStatus, "Fitness", "COMPASS_CAL_FIT", FitnessFallback);
        Primary = new(services, Status, "Primary compass", "COMPASS_PRIMARY", PrimaryOptions);

        string[] suffix = ["", "2", "3"];
        for (var i = 0; i < 3; i++)
        {
            var n = i + 1;
            Legacy.Add(new LegacyCompassViewModel("Compass " + n,
                new(services, Status, "Use this compass", 1, 0, "COMPASS_USE" + suffix[i], $"COMPASS{n}_USE"),
                new(services, Status, "Externally mounted", 1, 0, i == 0 ? "COMPASS_EXTERNAL" : "COMPASS_EXTERN" + suffix[i], $"COMPASS{n}_EXTERN")));
        }

        services.Params.ParamsChanged += (_, _) => Rebuild();
        services.Params.MetadataChanged += (_, _) => Rebuild();
        services.Session.StateChanged += (_, state) => services.Dispatcher.Post(() => OnLinkState(state));
    }

    public AppServices Services { get; }

    public MagCalibrationProcess MagCal { get; }

    /// <summary>Results of parameter writes and reboots.</summary>
    public StatusLine Status { get; } = new();

    /// <summary>Results of the onboard calibration commands.</summary>
    public StatusLine MagStatus { get; } = new();

    public ObservableCollection<CompassRowViewModel> Rows { get; } = new();

    public IReadOnlyList<ParamToggleViewModel> UseCompass { get; }

    public ParamToggleViewModel Learn { get; }

    public ParamToggleViewModel AutoDeclination { get; }

    public ParamChoiceViewModel Fitness { get; }

    public ParamChoiceViewModel Primary { get; }

    public ObservableCollection<LegacyCompassViewModel> Legacy { get; } = new();

    public ObservableCollection<LegacyCompassViewModel> LegacyVisible { get; } = new();

    public ObservableCollection<MagCalBarViewModel> MagBars { get; } = new();

    /// <summary>ArduPilot 4.1+ (COMPASS_PRIO1_ID): the priority table. Otherwise the per-compass offsets view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLegacyLayout))]
    private bool _isPriorityLayout = true;

    public bool IsLegacyLayout => !IsPriorityLayout;

    [ObservableProperty]
    private bool _anyMissing;

    /// <summary>The table order differs from COMPASS_PRIO1..3_ID on the vehicle.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SavePriorityCommand), nameof(UndoPriorityCommand))]
    private bool _hasPendingOrder;

    [ObservableProperty]
    private string _pendingText = "";

    [ObservableProperty]
    private bool _rebootRequired;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartMagCalCommand), nameof(AcceptMagCalCommand), nameof(CancelMagCalCommand),
        nameof(SavePriorityCommand), nameof(RebootCommand), nameof(LargeVehicleMagCalCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartMagCalCommand), nameof(AcceptMagCalCommand), nameof(CancelMagCalCommand))]
    [NotifyPropertyChangedFor(nameof(ShowMagWaiting))]
    private bool _isMagCalRunning;

    [ObservableProperty]
    private bool _isMagCalComplete;

    [ObservableProperty]
    private bool _hasOnboardCalibration = true;

    [ObservableProperty]
    private string _declination = "";

    public bool IsConnected => Services.Session.IsConnected;

    public bool HasMagBars => MagBars.Count > 0;

    /// <summary>Calibrating, before the first progress message.</summary>
    public bool ShowMagWaiting => IsMagCalRunning && !HasMagBars;

    // ---------------------------------------------------------------- table

    /// <summary>Re-reads everything from the parameters (UI thread).</summary>
    public void Rebuild()
    {
        var p = ParamValues();
        IsPriorityLayout = CompassPriority.HasPriorities(p) || p.Count == 0;

        _savedOrder = CompassPriority.Build(p, out var missing);
        AnyMissing = missing;
        SetRows(_savedOrder);

        foreach (var t in UseCompass)
            t.Refresh();
        Learn.Refresh();
        AutoDeclination.Refresh();
        Fitness.Refresh();
        Primary.Refresh();
        RebuildLegacy(p);

        var cs = Services.Session.Cs;
        HasOnboardCalibration = IsPriorityLayout ||
                                (cs.capabilities & (uint)MAVLink.MAV_PROTOCOL_CAPABILITY.COMPASS_CALIBRATION) != 0 ||
                                cs.firmware == Firmwares.ArduPlane && cs.version >= new Version(3, 7, 1);
    }

    private Dictionary<string, double> ParamValues() =>
        Services.Params.Snapshot().ToDictionary(e => e.Name, e => e.Value, StringComparer.Ordinal);

    private void SetRows(IEnumerable<CompassDevice> devices)
    {
        var orientNames = Services.Params.Meta(Services.Params.FirstExisting("COMPASS_ORIENT", "COMPASS1_ORIENT") ?? "COMPASS_ORIENT")?.Values;
        Rows.Clear();
        foreach (var d in devices)
            Rows.Add(new CompassRowViewModel(d, OrientationName(d.Orientation, orientNames), MoveUp, MoveDown));
        Renumber();
    }

    private static string OrientationName(int? value, IReadOnlyList<KeyValuePair<double, string>>? names)
    {
        if (value is not { } v)
            return "";
        var hit = names?.FirstOrDefault(n => n.Key == v);
        return hit is { Value: { } name } ? name : v.ToString(CultureInfo.InvariantCulture);
    }

    private void MoveUp(CompassRowViewModel row)
    {
        var i = Rows.IndexOf(row);
        if (i > 0)
            MoveRow(i, i - 1);
        Renumber();
    }

    private void MoveDown(CompassRowViewModel row)
    {
        var i = Rows.IndexOf(row);
        if (i >= 0 && i < Rows.Count - 1)
            MoveRow(i, i + 1);
        Renumber();
    }

    // remove + insert: the DataGrid does not reorder its rows on a collection Move
    private void MoveRow(int from, int to)
    {
        var row = Rows[from];
        Rows.RemoveAt(from);
        Rows.Insert(to, row);
    }

    private void Renumber()
    {
        for (var i = 0; i < Rows.Count; i++)
            Rows[i].Priority = i + 1;

        // pending once the user has changed the table; Save writes all three slots, as Mission Planner does
        var order = Rows.Select(r => r.Device).ToList();
        HasPendingOrder = IsPriorityLayout && !order.SequenceEqual(_savedOrder);
        var changes = CompassPriority.PriorityValues(order)
            .Where(kv => Services.Params.Get(kv.Key) is not { } cur || cur != kv.Value)
            .ToList();
        PendingText = changes.Count > 0
            ? string.Join(", ", changes.Select(kv => $"{kv.Key} = {ParamFormat.Value(kv.Value)}"))
            : "Order changed below the first three (no priority change)";
    }

    /// <summary>Drops the compasses that are prioritised but no longer detected (written with Save).</summary>
    [RelayCommand]
    private void RemoveMissing()
    {
        foreach (var r in Rows.Where(r => r.Missing).ToList())
            Rows.Remove(r);
        Renumber();
    }

    [RelayCommand(CanExecute = nameof(HasPendingOrder))]
    private void UndoPriority() => SetRows(_savedOrder);

    /// <summary>Writes COMPASS_PRIO1..3_ID from the first three rows; the autopilot then needs a reboot.</summary>
    [RelayCommand(CanExecute = nameof(CanSavePriority))]
    private async Task SavePriorityAsync()
    {
        var values = CompassPriority.PriorityValues(Rows.Select(r => r.Device).ToList());
        var text = string.Join("\n", values.Select(kv => $"{kv.Key} = {ParamFormat.Value(kv.Value)}"));
        if (!await Services.Dialogs.ConfirmAsync("Compass Priority",
                "Write the new compass priority?\n\n" + text + "\n\nA reboot is required to apply the new order.", "Write"))
            return;

        IsBusy = true;
        var failed = new List<string>();
        try
        {
            foreach (var name in CompassPriority.PriorityParams)
            {
                if (values.TryGetValue(name, out var v) && !await Services.Params.SetAsync(name, v))
                    failed.Add(name);
            }
        }
        finally
        {
            IsBusy = false;
        }

        // as Mission Planner: the order is the vehicle's next boot order even if one write failed
        RebootRequired = true;
        if (failed.Count > 0)
            Status.Set(StatusKind.Error, "Error setting parameter: " + string.Join(", ", failed));
        else
            Status.Set(StatusKind.Success, "Compass priority written. A reboot is required to adjust the ordering.");

        _savedOrder = Rows.Select(r => r.Device).ToList();
        Renumber();
    }

    private bool CanSavePriority() => HasPendingOrder && !IsBusy && IsConnected;

    // ---------------------------------------------------------------- legacy page

    private void RebuildLegacy(IReadOnlyDictionary<string, double> p)
    {
        string[] suffix = ["", "2", "3"];
        LegacyVisible.Clear();
        for (var i = 0; i < 3; i++)
        {
            var c = Legacy[i];
            c.Use.Refresh();
            c.External.Refresh();

            // compass 1 needs its offsets, 2 and 3 exist when their EXTERN parameter does (ConfigHWCompass)
            var ofs = i == 0 ? "COMPASS_OFS_" : $"COMPASS_OFS{suffix[i]}_";
            var present = i == 0 ? p.ContainsKey(ofs + "X") : p.ContainsKey("COMPASS_EXTERN" + suffix[i]);
            if (!present)
                continue;

            var orientName = "COMPASS_ORIENT" + suffix[i];
            c.Orientation = OrientationName(p.TryGetValue(orientName, out var o) ? (int)o : null, Services.Params.Meta(orientName)?.Values);

            var x = (int)p.GetValueOrDefault(ofs + "X");
            var y = (int)p.GetValueOrDefault(ofs + "Y");
            var z = (int)p.GetValueOrDefault(ofs + "Z");
            c.Offsets = $"X: {x},   Y: {y},   Z: {z}";
            var max = Math.Max(Math.Max(Math.Abs(x), Math.Abs(y)), Math.Abs(z));
            c.OffsetsKind = max > OffsetRed ? StatusKind.Error
                : max > OffsetYellow ? StatusKind.Warning
                : x == 0 && y == 0 && z == 0 ? StatusKind.Error
                : StatusKind.Success;

            var mot = i == 0 ? "COMPASS_MOT_" : $"COMPASS_MOT{suffix[i]}_";
            c.Motor = p.ContainsKey(mot + "X")
                ? $"X: {(int)p[mot + "X"]},   Y: {(int)p.GetValueOrDefault(mot + "Y")},   Z: {(int)p.GetValueOrDefault(mot + "Z")}"
                : "";
            LegacyVisible.Add(c);
        }

        if (p.TryGetValue("COMPASS_DEC", out var dec))
        {
            var deg = dec * 180 / Math.PI;
            var whole = (int)deg;
            var minutes = (deg - whole) * 60;
            Declination = $"{whole}° {Math.Abs(minutes):0}'";
        }
        else
        {
            Declination = "";
        }
    }

    // ---------------------------------------------------------------- onboard calibration

    [RelayCommand(CanExecute = nameof(CanStartMagCal))]
    private async Task StartMagCalAsync()
    {
        if (RebootRequired)
        {
            // Mission Planner insists on the new priority order being live first
            if (await Services.Dialogs.ConfirmAsync("Reboot", "Reboot required, reboot now?", "Reboot"))
                await RebootNowAsync();
            return;
        }

        if (!await Services.Dialogs.ConfirmAsync("Onboard Mag Calibration",
                "Start the onboard compass calibration?\n\nHold the vehicle in the air and rotate it slowly so that each side " +
                "(front, back, left, right, top and bottom) points down towards the earth for a few seconds in turn, " +
                "until every bar is complete.", "Start"))
            return;

        IsBusy = true;
        MagStatus.Clear();
        try
        {
            if (await MagCal.StartAsync())
            {
                IsMagCalComplete = false;
                MagBars.Clear();
                OnPropertyChanged(nameof(HasMagBars));
                OnPropertyChanged(nameof(ShowMagWaiting));
                MagStatus.Set(StatusKind.Info, "Calibrating: rotate the vehicle until every compass completes.");
            }
            else
            {
                MagStatus.Set(StatusKind.Error, "The autopilot refused to start the calibration (is it armed, or already calibrating?).");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("start mag cal", ex);
            MagStatus.Set(StatusKind.Error, "Failed to start MAG CAL, check the autopilot is still responding. " + ex.Message);
        }
        finally
        {
            IsBusy = false;
            SyncMag();
        }
    }

    private bool CanStartMagCal() => !IsBusy && !IsMagCalRunning && IsConnected;

    [RelayCommand(CanExecute = nameof(CanStopMagCal))]
    private Task AcceptMagCalAsync() => StopMagCalAsync(accept: true);

    [RelayCommand(CanExecute = nameof(CanStopMagCal))]
    private Task CancelMagCalAsync() => StopMagCalAsync(accept: false);

    private bool CanStopMagCal() => !IsBusy && IsMagCalRunning;

    private async Task StopMagCalAsync(bool accept)
    {
        IsBusy = true;
        try
        {
            var ok = accept ? await MagCal.AcceptAsync() : await MagCal.CancelAsync();
            if (accept)
                MagStatus.Set(ok ? StatusKind.Success : StatusKind.Error,
                    ok ? "Calibration accepted. Please reboot the autopilot." : "The autopilot did not accept the results.");
            else
                MagStatus.Set(ok ? StatusKind.Warning : StatusKind.Error, ok ? "Calibration cancelled." : "Cancel failed.");
            if (accept && ok)
                RebootRequired = true;
        }
        catch (Exception ex)
        {
            Log.Warn("stop mag cal", ex);
            MagStatus.Set(StatusKind.Error, ex.Message);
        }
        finally
        {
            IsBusy = false;
            SyncMag();
        }
    }

    private async Task OnMagCompleteAsync()
    {
        SyncMag();
        RebootRequired = true;
        MagStatus.Set(StatusKind.Success, "Calibration complete. Please reboot the autopilot.");
        await Services.Dialogs.ShowMessageAsync("Compass", "Please reboot the autopilot");
    }

    private void SyncMag()
    {
        _magDirty = false;
        IsMagCalRunning = MagCal.IsRunning;
        IsMagCalComplete = MagCal.IsComplete;

        foreach (var s in MagCal.Snapshot())
        {
            var bar = MagBars.FirstOrDefault(b => b.CompassId == s.CompassId);
            if (bar == null)
            {
                bar = new MagCalBarViewModel(s.CompassId);
                var at = MagBars.TakeWhile(b => b.CompassId < s.CompassId).Count();
                MagBars.Insert(at, bar);
                OnPropertyChanged(nameof(HasMagBars));
                OnPropertyChanged(nameof(ShowMagWaiting));
            }

            bar.Percent = s.Percent;
            bar.IsSuccess = s.Succeeded;
            bar.IsFailed = s.HasResult && !s.Succeeded;
            bar.Result = s.HasResult
                ? string.Create(CultureInfo.InvariantCulture,
                    $"x: {s.OffsetX:0.0}  y: {s.OffsetY:0.0}  z: {s.OffsetZ:0.0}  fit: {s.Fitness:0.0}  {s.ResultName}")
                : s.Attempt > 1 ? $"{s.Percent}%  (attempt {s.Attempt})" : $"{s.Percent}%";
        }
    }

    /// <summary>FIXED_MAG_CAL_YAW: calibrates from a known true heading (needs a GPS fix), for vehicles too large to rotate.</summary>
    [RelayCommand(CanExecute = nameof(CanCommand))]
    private async Task LargeVehicleMagCalAsync()
    {
        var text = await Services.Dialogs.PromptAsync("MagCal Yaw",
            "Enter current heading in degrees\nNOTE: gps lock is required. Heading is true, not magnetic", "0");
        if (text == null)
            return;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var heading) &&
            !double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out heading))
        {
            MagStatus.Set(StatusKind.Error, "Invalid number entered: " + text);
            return;
        }

        IsBusy = true;
        try
        {
            var ok = await _link.CommandAsync(MAVLink.MAV_CMD.FIXED_MAG_CAL_YAW, (float)heading, 0, 0, 0, 0, 0, 0);
            MagStatus.Set(ok ? StatusKind.Success : StatusKind.Error, ok ? "Completed. Please reboot the autopilot." : "Command failed.");
            if (ok)
                RebootRequired = true;
        }
        catch (Exception ex)
        {
            Log.Warn("fixed mag cal yaw", ex);
            MagStatus.Set(StatusKind.Error, "Command failed: " + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanCommand() => !IsBusy && IsConnected;

    // ---------------------------------------------------------------- reboot

    [RelayCommand(CanExecute = nameof(CanCommand))]
    private async Task RebootAsync()
    {
        if (await Services.Dialogs.ConfirmAsync("Reboot", "Reboot the autopilot?", "Reboot"))
            await RebootNowAsync();
    }

    private async Task RebootNowAsync()
    {
        IsBusy = true;
        try
        {
            var mav = Services.Session.Mav;
            var ok = await Task.Run(() => mav.doReboot(false, true));
            if (ok)
            {
                RebootRequired = false;
                Status.Set(StatusKind.Success, "The autopilot is rebooting.");
            }
            else
            {
                Status.Set(StatusKind.Error, "Reboot failed. Please manually reboot the hardware.");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("reboot", ex);
            Status.Set(StatusKind.Error, "Error communicating with the autopilot: " + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---------------------------------------------------------------- page

    private void OnLinkState(LinkState state)
    {
        if (state == LinkState.Disconnected)
        {
            if (MagCal.IsRunning)
            {
                MagCal.Detach();
                MagStatus.Set(StatusKind.Error, "The link was lost during the calibration.");
            }

            RebootRequired = false;
            SyncMag();
        }

        OnPropertyChanged(nameof(IsConnected));
        StartMagCalCommand.NotifyCanExecuteChanged();
        SavePriorityCommand.NotifyCanExecuteChanged();
        RebootCommand.NotifyCanExecuteChanged();
        LargeVehicleMagCalCommand.NotifyCanExecuteChanged();
    }

    public override void Tick()
    {
        if (_magDirty)
            SyncMag();
    }

    public override void OnShown()
    {
        Rebuild();
        SyncMag();
        OnLinkState(Services.Session.State);
    }

    public override void OnHidden()
    {
        // as Mission Planner's Deactivate: offer the reboot the new order needs
        if (RebootRequired && IsConnected && !IsMagCalRunning)
            _ = OfferRebootAsync();
    }

    private async Task OfferRebootAsync()
    {
        if (await Services.Dialogs.ConfirmAsync("Reboot", "Reboot required, reboot now?", "Reboot"))
            await RebootNowAsync();
    }
}
