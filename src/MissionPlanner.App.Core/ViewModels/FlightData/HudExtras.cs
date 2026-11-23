using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.FlightData;

/// <summary>
/// HUD options from Mission Planner's HUD menu: ground colour, battery cell count (shows volts per
/// cell) and user items (any telemetry value drawn on the HUD). Saved in the settings.
/// </summary>
public static class HudOptions
{
    public const string GroundColorKey = "next_hud_ground";
    public const string CellCountKey = "next_hud_cells";
    public const string UserItemsKey = "next_hud_items";

    /// <summary>"#rrggbb", or empty for the standard green.</summary>
    public static string GroundColor
    {
        get => Settings.Instance[GroundColorKey] ?? "";
        set => Settings.Instance[GroundColorKey] = value;
    }

    /// <summary>Cells in series; 0 = do not show volts per cell.</summary>
    public static int CellCount
    {
        get => Math.Clamp(Settings.Instance.GetInt32(CellCountKey, 0), 0, 24);
        set => Settings.Instance[CellCountKey] = Math.Clamp(value, 0, 24).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>CurrentState property names shown on the HUD.</summary>
    public static IReadOnlyList<string> UserItems
    {
        get => (Settings.Instance[UserItemsKey] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        set => Settings.Instance[UserItemsKey] = string.Join(',', value);
    }

    /// <summary>Raised when the options change, so the HUD redraws.</summary>
    public static event EventHandler? Changed;

    internal static void RaiseChanged() => Changed?.Invoke(null, EventArgs.Empty);

    /// <summary>The ground colours offered (Mission Planner lets you pick any).</summary>
    public static IReadOnlyList<(string Name, string Color)> Grounds { get; } =
    [
        ("Standard green", ""), ("Brown", "#8a5a2b"), ("Sand", "#c2a46b"), ("Grey", "#5f6670"), ("Dark green", "#2f4a1c"), ("Ocean blue", "#1d4d7a"),
    ];

    /// <summary>Volts per cell, or null when the cell count is not set or there is no voltage.</summary>
    public static double? PerCell(double voltage, int cells) => cells > 0 && voltage > 0 ? voltage / cells : null;
}

/// <summary>One user item on the HUD: a telemetry value with its label and unit.</summary>
public sealed record HudUserItem(string Name, string Label, string Unit);

/// <summary>The "HUD items…" dialog: ground colour, cell count and user items.</summary>
public sealed partial class HudOptionsViewModel : PageViewModel, IDialogPage
{
    public HudOptionsViewModel()
        : base("HUD options")
    {
        _cellCount = HudOptions.CellCount;
        _selectedGround = Grounds.FirstOrDefault(g => g.Color == HudOptions.GroundColor) ?? Grounds[0];
        foreach (var name in HudOptions.UserItems)
        {
            var f = QuickValueCatalog.Describe(name);
            Items.Add(new HudUserItem(name, f.Label, f.Unit));
        }

        FilterFields();
    }

    public event EventHandler<bool>? CloseRequested;

    public sealed record GroundChoice(string Name, string Color)
    {
        public override string ToString() => Name;
    }

    public IReadOnlyList<GroundChoice> Grounds { get; } = HudOptions.Grounds.Select(g => new GroundChoice(g.Name, g.Color)).ToList();

    [ObservableProperty]
    private GroundChoice _selectedGround;

    [ObservableProperty]
    private decimal _cellCount;

    public ObservableCollection<HudUserItem> Items { get; } = new();

    [ObservableProperty]
    private string _filter = "";

    public ObservableCollection<QuickField> Fields { get; } = new();

    [ObservableProperty]
    private QuickField? _selectedField;

    partial void OnFilterChanged(string value) => FilterFields();

    private void FilterFields()
    {
        Fields.Clear();
        foreach (var f in QuickValueCatalog.ListFields().Where(f => Filter.Length == 0 ||
                                                           f.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase) ||
                                                           f.Label.Contains(Filter, StringComparison.OrdinalIgnoreCase)).Take(300))
            Fields.Add(f);
    }

    [RelayCommand]
    private void AddItem()
    {
        if (SelectedField is not { } f || Items.Any(i => i.Name == f.Name) || Items.Count >= 12)
            return;
        Items.Add(new HudUserItem(f.Name, f.Label, f.Unit));
    }

    [RelayCommand]
    private void RemoveItem(HudUserItem item) => Items.Remove(item);

    [RelayCommand]
    private void Save()
    {
        HudOptions.GroundColor = SelectedGround.Color;
        HudOptions.CellCount = (int)CellCount;
        HudOptions.UserItems = Items.Select(i => i.Name).ToList();
        Settings.Instance.Save();
        HudOptions.RaiseChanged();
        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}

/// <summary>A bar in the EKF / vibration windows.</summary>
public sealed partial class StatusBar : ObservableObject
{
    public StatusBar(string label, double warn, double bad, double max)
    {
        Label = label;
        Warn = warn;
        Bad = bad;
        Max = max;
    }

    public string Label { get; }

    public double Warn { get; }

    public double Bad { get; }

    public double Max { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Level), nameof(IsWarning), nameof(IsBad))]
    private double _value;

    public bool IsWarning => Level == 1;

    public bool IsBad => Level == 2;

    [ObservableProperty]
    private string _text = "";

    /// <summary>0 ok, 1 warning, 2 bad.</summary>
    public int Level => Value >= Bad ? 2 : Value >= Warn ? 1 : 0;

    public void Set(double v, string format = "0.00")
    {
        Value = Math.Min(v, Max);
        Text = v.ToString(format, CultureInfo.InvariantCulture);
    }
}

/// <summary>Mission Planner's EKF status window (click EKF on the HUD): variances and flags.</summary>
public sealed partial class EkfStatusViewModel : PageViewModel
{
    private readonly AppServices _services;

    public EkfStatusViewModel(AppServices services)
        : base("EKF status")
    {
        _services = services;
    }

    public IReadOnlyList<StatusBar> Bars { get; } =
    [
        new("Velocity", 0.5, 0.8, 1), new("Position (horizontal)", 0.5, 0.8, 1), new("Position (vertical)", 0.5, 0.8, 1),
        new("Compass", 0.5, 0.8, 1), new("Terrain altitude", 0.5, 0.8, 1),
    ];

    public ObservableCollection<string> Flags { get; } = new();

    [ObservableProperty]
    private string _accuracy = "";

    public override void OnShown() => Tick();

    public override void Tick()
    {
        var cs = _services.Session.Cs;
        Bars[0].Set(cs.ekfvelv);
        Bars[1].Set(cs.ekfposhor);
        Bars[2].Set(cs.ekfposvert);
        Bars[3].Set(cs.ekfcompv);
        Bars[4].Set(cs.ekfteralt);
        Accuracy = string.Create(CultureInfo.InvariantCulture, $"Estimated accuracy: horizontal {cs.ekfposhoracc:0.00} m, vertical {cs.ekfposvertacc:0.00} m");
        var flags = Enum.GetValues<MAVLink.EKF_STATUS_FLAGS>()
            .Where(f => (cs.ekfflags & (int)f) != 0)
            .Select(f => f.ToString().Replace("EKF_", "").Replace('_', ' ').ToLowerInvariant())
            .ToList();
        if (!flags.SequenceEqual(Flags))
        {
            Flags.Clear();
            foreach (var f in flags)
                Flags.Add(f);
        }
    }
}

/// <summary>Mission Planner's vibration window (click Vibe on the HUD): levels and clipping counts.</summary>
public sealed partial class VibrationViewModel : PageViewModel
{
    private readonly AppServices _services;

    public VibrationViewModel(AppServices services)
        : base("Vibration")
    {
        _services = services;
    }

    /// <summary>m/s/s: below 30 good, above 60 bad (ArduPilot's guidance).</summary>
    public IReadOnlyList<StatusBar> Bars { get; } = [new("X", 30, 60, 100), new("Y", 30, 60, 100), new("Z", 30, 60, 100)];

    [ObservableProperty]
    private string _clipping = "";

    public override void OnShown() => Tick();

    public override void Tick()
    {
        var cs = _services.Session.Cs;
        Bars[0].Set(cs.vibex, "0.0");
        Bars[1].Set(cs.vibey, "0.0");
        Bars[2].Set(cs.vibez, "0.0");
        Clipping = string.Create(CultureInfo.InvariantCulture,
            $"Accelerometer clipping: IMU1 {cs.vibeclip0}, IMU2 {cs.vibeclip1}, IMU3 {cs.vibeclip2} (should stay at 0)");
    }
}

/// <summary>Mission Planner's pre-arm window (click "Not Ready to Arm"): asks the vehicle to run its checks and lists why it will not arm.</summary>
public sealed partial class PrearmViewModel : PageViewModel
{
    private readonly AppServices _services;
    private DateTime _since = DateTime.MinValue;

    public PrearmViewModel(AppServices services)
        : base("Pre-arm checks")
    {
        _services = services;
    }

    public ObservableCollection<string> Problems { get; } = new();

    [ObservableProperty]
    private string _status = "";

    public override void OnShown() => _ = RunAsync();

    public override void Tick() => Collect();

    [RelayCommand]
    private async Task RunAsync()
    {
        var s = _services.Session;
        if (!s.IsConnected)
        {
            Status = "Not connected.";
            return;
        }

        Problems.Clear();
        _since = DateTime.Now.AddSeconds(-1);
        Status = "Asking the vehicle to run its pre-arm checks…";
        var v = s.Vehicle;
        try
        {
            await Task.Run(() => s.Mav.doCommand(v.sysid, v.compid, MAVLink.MAV_CMD.RUN_PREARM_CHECKS, 0, 0, 0, 0, 0, 0, 0, false));
        }
        catch (Exception ex)
        {
            Status = "Could not ask: " + ex.Message;
            return;
        }

        await Task.Delay(2500);
        Collect();
        // the reasons the vehicle gives win over its ready flag, which follows a little later
        Status = Problems.Count > 0 ? "Not ready to arm:"
            : s.Cs.prearmstatus ? "The vehicle is ready to arm."
            : "Not ready to arm; the vehicle sent no reason yet.";
    }

    private void Collect()
    {
        if (_since == DateTime.MinValue)
            return;
        List<(DateTime time, string message)> messages;
        try
        {
            messages = _services.Session.Cs.messages.ToList();
        }
        catch (InvalidOperationException)
        {
            return; // the reader thread added one meanwhile; next tick
        }

        foreach (var (time, text) in messages)
        {
            if (time < _since || !text.Contains("PreArm", StringComparison.OrdinalIgnoreCase) && !text.Contains("Arm:", StringComparison.OrdinalIgnoreCase))
                continue;
            var t = text.Trim();
            if (!Problems.Contains(t))
                Problems.Add(t);
        }
    }
}
