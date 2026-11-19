using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;
using MissionPlanner.App.Core.ViewModels.SetupScreens.OptionalHardware;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>A grid choice for the layout editor.</summary>
public sealed record OsdGridOption(OsdGrid Grid, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One OSD screen on the page: its layout and its own settings.</summary>
public sealed partial class OsdScreenViewModel : ObservableObject
{
    public OsdScreenViewModel(OsdScreen screen, IReadOnlyList<ParamField> settings, ParamField? textResolution)
    {
        Screen = screen;
        Settings = settings;
        TextResolution = textResolution;
        foreach (var item in screen.Items)
            item.PropertyChanged += OnItemChanged;
        _enabledCount = screen.Items.Count(i => i.Enabled);
    }

    public OsdScreen Screen { get; }

    public int Number => Screen.Number;

    public string Title => Screen.Title + (Screen.IsParamScreen ? " (parameters)" : "");

    public IReadOnlyList<OsdItem> Items => Screen.Items;

    /// <summary>OSDn_ENABLE, OSDn_CHAN_MIN/MAX, OSDn_TXT_RES, OSDn_FONT...</summary>
    public IReadOnlyList<ParamField> Settings { get; }

    public ParamField? TextResolution { get; }

    /// <summary>Bumped whenever an element moves or is switched on / off (the editor redraws).</summary>
    [ObservableProperty]
    private int _layoutVersion;

    [ObservableProperty]
    private int _enabledCount;

    public event EventHandler? ItemsChanged;

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OsdItem.X) or nameof(OsdItem.Y) or nameof(OsdItem.Enabled) or nameof(OsdItem.IsSelected))
            LayoutVersion++;
        if (e.PropertyName is nameof(OsdItem.IsChanged) or nameof(OsdItem.Enabled))
        {
            EnabledCount = Items.Count(i => i.Enabled);
            ItemsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Detach()
    {
        foreach (var item in Screen.Items)
            item.PropertyChanged -= OnItemChanged;
    }

    public override string ToString() => Title;
}

/// <summary>
/// OSD (ConfigHWOSD + ConfigOSD): OSD type and general settings, each screen's settings, and a
/// layout editor where the elements are switched on and dragged into place on the character grid.
/// Settings write as they change; layout edits are held until Write (and, as in Mission Planner,
/// written on leaving the page unless that is switched off).
/// </summary>
public sealed partial class OsdViewModel : HardwarePageViewModel
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(OsdViewModel));

    /// <summary>MinimOSD-style OSDs read MAVLink telemetry: Mission Planner's "Enable Telemetry" stream rates.</summary>
    internal static readonly string[] TelemetryRateParams =
    [
        "EXT_STAT", "EXTRA1", "EXTRA2", "EXTRA3", "POSITION", "RAW_CTRL", "RAW_SENS", "RC_CHAN",
    ];

    private OsdScreenViewModel? _clipboard;

    public OsdViewModel(AppServices services)
        : this(services, null)
    {
    }

    internal OsdViewModel(AppServices services, IParamSource? source)
        : base("OSD", services, source)
    {
        Grids =
        [
            new OsdGridOption(OsdGrid.Sd, "SD 30×16 (PAL; NTSC 13 rows)"),
            new OsdGridOption(OsdGrid.Hd50, "HD 50×18"),
            new OsdGridOption(OsdGrid.Hd60, "HD 60×22"),
        ];
    }

    public IReadOnlyList<OsdGridOption> Grids { get; }

    [ObservableProperty]
    private ParamField? _osdType;

    /// <summary>The other OSD_ parameters (switching, units, warnings...).</summary>
    [ObservableProperty]
    private IReadOnlyList<ParamField> _general = [];

    [ObservableProperty]
    private IReadOnlyList<OsdScreenViewModel> _screens = [];

    [ObservableProperty]
    private OsdScreenViewModel? _selectedScreen;

    [ObservableProperty]
    private OsdItem? _selectedItem;

    [ObservableProperty]
    private OsdGridOption? _selectedGrid;

    /// <summary>Draw element names instead of sample values.</summary>
    [ObservableProperty]
    private bool _showNames;

    [ObservableProperty]
    private bool _autoWriteOnLeave = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    [NotifyPropertyChangedFor(nameof(WriteText))]
    private int _changeCount;

    public bool HasChanges => ChangeCount > 0;

    public string WriteText => ChangeCount > 0 ? $"Write layout ({ChangeCount})" : "Write layout";

    [ObservableProperty]
    private bool _canPaste;

    [ObservableProperty]
    private bool _hasTelemetryRates;

    protected override void Build()
    {
        foreach (var s in Screens)
        {
            s.ItemsChanged -= OnItemsChanged;
            s.Detach();
        }

        OsdType = Field("OSD_TYPE", new ParamFieldSpec { Kind = ParamEditorKind.Options, Label = "OSD type" });
        if (OsdType != null)
            OsdType.Written += (_, e) =>
            {
                if (e.OldValue != e.NewValue)
                    RebootRequired = true;
            };

        var names = Source.Names();
        General = names
            .Where(n => n.StartsWith("OSD_", StringComparison.Ordinal) && n != "OSD_TYPE")
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => Field(n))
            .OfType<ParamField>()
            .ToList();

        var layout = OsdLayout.Parse(names
            .Where(n => n.StartsWith("OSD", StringComparison.Ordinal) && n.Length > 4 && char.IsDigit(n[3]))
            .Select(n => new KeyValuePair<string, double>(n, Source.Get(n) ?? 0)));
        var screens = new List<OsdScreenViewModel>();
        foreach (var screen in layout)
        {
            var settings = screen.Settings.Select(n => Field(n)).OfType<ParamField>().ToList();
            var txtRes = settings.FirstOrDefault(f => f.Name.EndsWith("_TXT_RES", StringComparison.Ordinal));
            if (txtRes != null)
                txtRes.Written += (_, _) => PickGrid();
            var vm = new OsdScreenViewModel(screen, settings, txtRes);
            vm.ItemsChanged += OnItemsChanged;
            screens.Add(vm);
        }

        var keep = SelectedScreen?.Number;
        _clipboard = null;
        CanPaste = false;
        Screens = screens;
        SelectedScreen = screens.FirstOrDefault(s => s.Number == keep) ?? screens.FirstOrDefault();
        HasTelemetryRates = Source.Has("SR0_EXTRA1") || Source.Has("SR1_EXTRA1");
        Count();
        Status = OsdType == null ? "This vehicle has no OSD_TYPE parameter." : null;
    }

    partial void OnSelectedScreenChanged(OsdScreenViewModel? value)
    {
        SelectedItem = null;
        PickGrid();
    }

    partial void OnSelectedItemChanged(OsdItem? oldValue, OsdItem? newValue)
    {
        if (oldValue != null)
            oldValue.IsSelected = false;
        if (newValue != null)
            newValue.IsSelected = true;
    }

    /// <summary>The grid the screen's text resolution asks for (MSP DisplayPort HD), SD otherwise.</summary>
    private void PickGrid()
    {
        var grid = OsdGrid.FromTextResolution(SelectedScreen?.TextResolution?.Value);
        SelectedGrid = Grids.FirstOrDefault(g => g.Grid == grid) ?? Grids[0];
    }

    private void OnItemsChanged(object? sender, EventArgs e) => Count();

    private void Count() => ChangeCount = Screens.Sum(s => s.Items.Count(i => i.IsChanged));

    /// <summary>Moves an element to a cell (kept on the grid); the layout editor calls this while dragging.</summary>
    public void Move(OsdItem item, int x, int y)
    {
        var (cx, cy) = OsdLayout.Clamp(x, y, SelectedGrid?.Grid ?? OsdGrid.Sd);
        item.X = cx;
        item.Y = cy;
    }

    [RelayCommand]
    private void ClearAll()
    {
        if (SelectedScreen == null)
            return;
        foreach (var item in SelectedScreen.Items)
            item.Enabled = false;
    }

    [RelayCommand]
    private void CopyLayout()
    {
        if (SelectedScreen is not { Items.Count: > 0 } s)
            return;
        _clipboard = s;
        CanPaste = true;
        Status = s.Title + " copied: choose another screen and paste.";
    }

    [RelayCommand]
    private void PasteLayout()
    {
        if (_clipboard == null || SelectedScreen == null || ReferenceEquals(_clipboard, SelectedScreen))
            return;
        OsdLayout.CopyLayout(_clipboard.Screen, SelectedScreen.Screen);
        Status = $"{_clipboard.Title} layout pasted on {SelectedScreen.Title} (not written yet).";
    }

    [RelayCommand]
    private async Task DiscardAsync()
    {
        if (!HasChanges)
            return;
        if (!await Services.Dialogs.ConfirmAsync("Discard", "Discard all layout changes?", "Discard"))
            return;
        foreach (var s in Screens)
        foreach (var item in s.Items)
            item.Discard();
    }

    [RelayCommand]
    private Task WriteAsync() => WriteLayoutAsync(silent: false);

    private async Task WriteLayoutAsync(bool silent)
    {
        var changes = Screens.SelectMany(s => s.Screen.Changes()).ToDictionary(c => c.Key, c => c.Value);
        if (changes.Count == 0)
        {
            if (!silent)
                await Services.Dialogs.ShowMessageAsync("OSD", "No changes to write.");
            return;
        }

        if (!Services.Session.IsConnected)
        {
            if (!silent)
                await Services.Dialogs.ShowMessageAsync("OSD", "Not connected.", MessageKind.Warning);
            return;
        }

        var items = Screens.SelectMany(s => s.Items).ToList();
        IsBusy = true;
        Status = $"Writing {changes.Count} parameters…";
        try
        {
            var result = await Services.Params.SetManyAsync(changes);
            var failed = result.Failed.ToHashSet(StringComparer.Ordinal);
            foreach (var name in changes.Keys.Where(n => !failed.Contains(n)))
                items.FirstOrDefault(i => i.EnabledParam == name || i.XParam == name || i.YParam == name)?.Accept(name);
            Count();
            var message = failed.Count == 0
                ? $"{changes.Count} parameters written."
                : $"Write failed for {failed.Count} parameters: {string.Join(", ", failed.Take(3))}{(failed.Count > 3 ? "…" : "")}";
            Status = message;
            if (failed.Count > 0 && !silent)
                await Services.Dialogs.ShowMessageAsync("OSD", message, MessageKind.Warning);
        }
        catch (Exception ex)
        {
            Log.Warn("osd write", ex);
            Status = "Write failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshParamsAsync()
    {
        if (!Services.Session.IsConnected)
            return;
        if (HasChanges && !await Services.Dialogs.ConfirmAsync("Refresh", "This will reset your layout changes. Continue?", "Refresh"))
            return;
        if (Services.Session.Cs.armed &&
            !await Services.Dialogs.ConfirmAsync("Refresh Params", "The vehicle is armed. Download all parameters again anyway?", "Refresh"))
            return;

        IsBusy = true;
        Status = "Getting parameters…";
        try
        {
            await Services.Params.RefreshAsync();
            Status = null;
        }
        catch (Exception ex)
        {
            Log.Warn("refresh", ex);
            Status = "Could not refresh parameters: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Mission Planner's "Enable Telemetry": stream rates a MAVLink OSD (MinimOSD) needs, on SR0, SR1 and SR3.</summary>
    [RelayCommand]
    private async Task EnableTelemetryAsync()
    {
        var names = new[] { "SR0_", "SR1_", "SR3_" }
            .SelectMany(p => TelemetryRateParams.Select(k => p + k))
            .Where(Source.Has)
            .ToList();
        if (names.Count == 0)
            return;
        IsBusy = true;
        var failed = 0;
        try
        {
            foreach (var n in names)
            {
                if (!await Source.SetAsync(n, 2))
                    failed++;
            }
        }
        finally
        {
            IsBusy = false;
        }

        Status = failed == 0 ? $"Telemetry rates set ({names.Count} parameters at 2 Hz)." : "Failed to set OSD rates.";
    }

    public override void OnHidden()
    {
        if (AutoWriteOnLeave && HasChanges)
            _ = WriteLayoutAsync(silent: true);
        base.OnHidden();
    }
}
