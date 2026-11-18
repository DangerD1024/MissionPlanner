using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using MissionPlanner.App.Core.Params;
using MissionPlanner.ArduPilot;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>
/// What the tuning pages share, after Mission Planner's ConfigArducopter / ConfigArduplane /
/// ConfigArdurover: cards of values in the WinForms arrangement, edits highlighted and queued until
/// Write Params (asking first about values that "more than doubled"), Refresh Screen (read the
/// page's values from the vehicle again and drop the edits), "Lock Pitch and Roll Values", and the
/// Copter 4.7 change warning for the box being edited.
/// </summary>
public abstract partial class TuningPageViewModel : PageViewModel
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(TuningPageViewModel));

    /// <summary>Space between cards, horizontally and vertically.</summary>
    public const double Gap = 12;

    private bool _shown;
    private bool _dirty = true;
    private int _ticks;
    private Dictionary<string, TuningParamItem> _byName = new(StringComparer.Ordinal);
    private ResolvedTuningLayout _layout = ResolvedTuningLayout.Empty;

    protected TuningPageViewModel(string title, AppServices services)
        : base(title)
    {
        Services = services;
        MetaSource = services.Params.Meta;
        _heading = title;
        services.Params.ParamsChanged += (_, _) => Invalidate();
        services.Params.MetadataChanged += (_, _) => Invalidate();
        Edits.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(PendingCount));
            OnPropertyChanged(nameof(HasPending));
            OnPropertyChanged(nameof(PendingText));
        };
    }

    public AppServices Services { get; }

    public ParamEditBuffer Edits { get; } = new();

    /// <summary>Parameter documentation (replaceable in tests).</summary>
    internal Func<string, ParamMeta?> MetaSource { get; set; }

    /// <summary>The acsimplepids.xml to read (null: the one next to the executable).</summary>
    internal string? XmlPath { get; set; }

    protected Firmwares Firmware => Services.Session.Cs.firmware;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand), nameof(RefreshScreenCommand))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand), nameof(RefreshScreenCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "";

    /// <summary>The page title as Mission Planner names it for this vehicle ("QP Extended Tuning").</summary>
    [ObservableProperty]
    private string _heading;

    [ObservableProperty]
    private string _vehicleText = "";

    [ObservableProperty]
    private bool _isEmpty = true;

    /// <summary>The cards, in rows of columns.</summary>
    [ObservableProperty]
    private IReadOnlyList<TuningRowItem> _rows = [];

    [ObservableProperty]
    private bool _hasCards;

    /// <summary>Shown when the vehicle has roll and pitch controllers to lock together.</summary>
    [ObservableProperty]
    private bool _canLockRollPitch;

    /// <summary>"Lock Pitch and Roll Values": an edit to a roll value is copied to its pitch twin and back.</summary>
    [ObservableProperty]
    private bool _lockRollPitch;

    /// <summary>The value being edited (the view reports focus), for the 4.7 change warning.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnitWarning), nameof(HasUnitWarning), nameof(UnitWarningIsScale))]
    private TuningParamItem? _focusedItem;

    /// <summary>Mission Planner's "Change in 4.7: ..." line for the box being edited.</summary>
    public string UnitWarning => FocusedItem is { HasChangeNote: true } f ? "Change in 4.7: " + f.ChangeNote : "";

    public bool HasUnitWarning => UnitWarning.Length > 0;

    /// <summary>The change is in units or scale, not just the name.</summary>
    public bool UnitWarningIsScale => FocusedItem is { } f && TuningEditors.IsScaleChange(f.ChangeNote);

    public int PendingCount => Edits.Count;

    public bool HasPending => Edits.Count > 0;

    public string PendingText => Edits.Count == 0 ? "" : $"{Edits.Count} unsaved change{(Edits.Count == 1 ? "" : "s")}";

    public virtual string EmptyText => !Services.Params.HasParams
        ? "Connect to a vehicle and download its parameters to tune it."
        : "None of the tuning parameters of this page were found on the vehicle.";

    public ResolvedTuningLayout Layout => _layout;

    public IReadOnlyCollection<TuningParamItem> AllItems => _byName.Values;

    public TuningParamItem? Item(string name) => _byName.GetValueOrDefault(name);

    /// <summary>The cards this page shows for the connected vehicle (null: none).</summary>
    protected abstract TuningLayout? CardLayout();

    /// <summary>The parameters on screen, for Refresh Screen.</summary>
    protected virtual IEnumerable<string> ScreenParams => _byName.Keys;

    /// <summary>The parameter documentation, null when unknown.</summary>
    protected ParamMeta? Meta(string name) => MetaSource(name);

    protected static string FirmwareName(Firmwares fw) => fw switch
    {
        Firmwares.ArduCopter2 => "ArduCopter",
        Firmwares.ArduPlane => "ArduPlane",
        Firmwares.ArduRover => "ArduRover",
        _ => fw.ToString(),
    };

    /// <summary>Builds the page from the parameter list and documentation.</summary>
    internal virtual void Rebuild() => BuildCards();

    /// <summary>Copies the vehicle's values and the queued edits into the editors.</summary>
    internal virtual void SyncValues()
    {
        foreach (var item in _byName.Values)
        {
            var editor = item.Editor;
            var value = Services.Params.Get(item.Name) ?? editor.Value;
            var pending = PendingFor(item.Name, value);
            if (!ParamValues.Same(value, editor.Value) || pending != editor.PendingValue)
                editor.SetValues(value, pending);
        }
    }

    /// <summary>The queued value of a parameter, dropped if the vehicle now has it.</summary>
    protected double? PendingFor(string name, double current) =>
        Edits.TryGet(name, out var p) && Edits.Set(name, p, current) ? p : null;

    /// <summary>Doubling means nothing for lists and bitmasks (gains with suggested values still count).</summary>
    internal bool IsEnumerated(string name) =>
        Meta(name) is { } m && (m.Bitmask.Count > 0 || (m.Values.Count > 0 && !TuningEditors.IsSuggestionList(m)));

    private void BuildCards()
    {
        var p = Services.Params;
        var layout = CardLayout();
        _layout = layout == null ? ResolvedTuningLayout.Empty : TuningLayouts.Resolve(layout, p.Has);

        var byName = new Dictionary<string, TuningParamItem>(StringComparer.Ordinal);
        var rows = new List<TuningRowItem>();
        foreach (var row in _layout.Rows)
        {
            var cells = new List<TuningCellItem>();
            foreach (var cell in row.Cells)
            {
                var groups = new List<TuningGroupItem>();
                foreach (var g in cell.Groups)
                {
                    var items = new List<TuningParamItem>();
                    foreach (var rp in g.Params)
                    {
                        var value = p.Get(rp.Name) ?? 0;
                        var meta = Meta(rp.Name);
                        var editor = new FriendlyParamItem(rp.Name, TuningEditors.EditorMeta(rp.Name, meta, value, rp.Def.Increment), value, OnEdited);
                        if (PendingFor(rp.Name, value) is { } pending)
                            editor.SetValues(value, pending);
                        var item = new TuningParamItem(rp.Label, editor, Tip(rp.Name, meta), layout!.LabelWidth);
                        items.Add(item);
                        byName[rp.Name] = item;
                    }

                    groups.Add(new TuningGroupItem(g.Id, g.Title, TuningGroupItem.Split(items, g.Def.Columns)));
                }

                cells.Add(new TuningCellItem(groups, cell.Span * layout!.CardWidth + (cell.Span - 1) * Gap));
            }

            rows.Add(new TuningRowItem(this, cells, row.LockAbove && _layout.HasLocks));
        }

        // edits of parameters no longer on the page cannot be written from it
        Edits.RemoveAll(Edits.Pending.Keys.Where(k => !byName.ContainsKey(k)).ToList());

        _byName = byName;
        FocusedItem = null;
        Rows = rows;
        HasCards = rows.Count > 0;
        CanLockRollPitch = _layout.HasLocks;
        LockRollPitch = _layout.LockByDefault(p.Get, p.Has);
        OnPropertyChanged(nameof(AllItems));
        OnPropertyChanged(nameof(Layout));
    }

    private static string Tip(string name, ParamMeta? meta)
    {
        var tip = TuningEditors.Tip(name, meta);
        var note = TuningEditors.ChangeNote(name);
        return note.Length > 0 ? tip + "\n\nChange in 4.7: " + note : tip;
    }

    private void OnEdited(FriendlyParamItem editor, double value)
    {
        if (editor.IsReadOnly)
            return;
        editor.SetPending(Edits.Set(editor.Name, value, editor.Value) ? value : null);

        var twin = "";
        if (LockRollPitch && _layout.LockPartners.TryGetValue(editor.Name, out var partner) && _byName.TryGetValue(partner, out var other)
            && !other.Editor.IsReadOnly)
        {
            var o = other.Editor;
            o.SetValues(o.Value, Edits.Set(o.Name, value, o.Value) ? value : null);
            twin = $" and {o.Name}";
        }

        StatusText = editor.IsModified || twin.Length > 0
            ? $"{editor.Name}{twin} = {ParamValues.Format(value)} (not written yet)"
            : "";
    }

    public override void OnShown()
    {
        _shown = true;
        IsConnected = Services.Session.IsConnected;
        if (_dirty)
            RebuildNow();
        else
            SyncValues();
    }

    public override void OnHidden() => _shown = false;

    public override void Tick()
    {
        IsConnected = Services.Session.IsConnected;

        // values change behind the page (other pages, the transmitter tuning knob): follow them once a second
        if (++_ticks % 20 == 0 && !IsBusy)
            SyncValues();
    }

    private void Invalidate()
    {
        _dirty = true;
        if (_shown)
            RebuildNow();
    }

    private void RebuildNow()
    {
        _dirty = false;
        try
        {
            Rebuild();
        }
        catch (Exception ex)
        {
            Log.Error("tuning page " + Title, ex);
            StatusText = "Could not build the page: " + ex.Message;
        }

        OnPropertyChanged(nameof(EmptyText));
    }

    private bool CanWrite => IsConnected && !IsBusy;

    /// <summary>Write Params (Ctrl+S): the queued edits, as Mission Planner's tuning pages write them.</summary>
    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task WriteAsync()
    {
        IsBusy = true;
        try
        {
            await WriteCoreAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>What Write Params does; the slider page writes as it goes.</summary>
    protected virtual async Task WriteCoreAsync()
    {
        var written = await TuningWriteFlow.WriteAsync(Services, Edits, IsEnumerated, s => StatusText = s);
        SyncValues();
        if (written.Count > 0)
            StatusText = $"Wrote {written.Count} parameter{(written.Count == 1 ? "" : "s")}.";
    }

    /// <summary>
    /// Mission Planner's "Refresh Screen": reads this page's parameters from the vehicle again, one by
    /// one, then shows them afresh (unwritten edits are dropped).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task RefreshScreenAsync()
    {
        var names = ScreenParams.Distinct().ToList();
        if (names.Count == 0)
            return;

        IsBusy = true;
        try
        {
            var session = Services.Session;
            var (sysid, compid) = (session.Vehicle.sysid, session.Vehicle.compid);
            var dispatcher = Services.Dispatcher;
            var (read, failed) = await Task.Run(() =>
            {
                int ok = 0, missed = 0, inARow = 0;
                foreach (var name in names)
                {
                    dispatcher.Post(() => StatusText = $"Reading {name} ({ok + missed + 1}/{names.Count})…");
                    try
                    {
                        session.Mav.GetParam(sysid, compid, name);
                        ok++;
                        inARow = 0;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("read " + name, ex);
                        missed++;

                        // a vehicle that stopped answering would take three seconds per value
                        if (++inARow >= 3)
                            break;
                    }
                }

                return (ok, missed);
            });

            Edits.Clear();
            RebuildNow();
            StatusText = failed == 0
                ? $"Read {read} parameters from the vehicle."
                : $"Read {read} of {names.Count} parameters; the vehicle did not answer for {failed}.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
