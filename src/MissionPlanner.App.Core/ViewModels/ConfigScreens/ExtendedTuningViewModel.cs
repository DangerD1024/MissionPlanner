using CommunityToolkit.Mvvm.ComponentModel;
using MissionPlanner.App.Core.Services;
using MissionPlanner.ArduPilot;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>
/// Extended Tuning: every controller of the vehicle as a card of values (Mission Planner's
/// ConfigArducopter, which also serves QuadPlanes with Q_ names, ConfigArduplane and
/// ConfigArdurover). Edits are queued and highlighted until Write Params; "Lock pitch and roll
/// values" keeps the roll and pitch controllers equal.
/// </summary>
public sealed partial class ExtendedTuningViewModel : TuningPageViewModel
{
    private Dictionary<string, TuningParamItem> _byName = new(StringComparer.Ordinal);
    private ResolvedTuningLayout _layout = new([], new Dictionary<string, string>());

    public ExtendedTuningViewModel(AppServices services)
        : base("Extended Tuning", services)
    {
    }

    [ObservableProperty]
    private IReadOnlyList<TuningSectionItem> _sections = [];

    /// <summary>Shown when the vehicle has roll and pitch controllers to lock together.</summary>
    [ObservableProperty]
    private bool _canLockRollPitch;

    /// <summary>"Lock pitch and roll values": an edit to a roll value is copied to its pitch twin and back.</summary>
    [ObservableProperty]
    private bool _lockRollPitch;

    public string Intro => "Every controller of the vehicle. Edits are highlighted and sent with Write Params; arrow keys step a value.";

    public ResolvedTuningLayout Layout => _layout;

    public IReadOnlyCollection<TuningParamItem> AllItems => _byName.Values;

    public TuningParamItem? Item(string name) => _byName.GetValueOrDefault(name);

    protected override IEnumerable<string> ScreenParams => _byName.Keys;

    internal override void Rebuild()
    {
        var p = Services.Params;
        var layout = TuningLayouts.For(Firmware, p.Get);
        _layout = layout == null ? new ResolvedTuningLayout([], new Dictionary<string, string>()) : TuningLayouts.Resolve(layout, p.Has);

        var byName = new Dictionary<string, TuningParamItem>(StringComparer.Ordinal);
        var sections = new List<TuningSectionItem>();
        foreach (var section in _layout.Groups.GroupBy(g => g.Section))
        {
            var groups = new List<TuningGroupItem>();
            foreach (var g in section)
            {
                var items = new List<TuningParamItem>();
                foreach (var rp in g.Params)
                {
                    var value = p.Get(rp.Name) ?? 0;
                    var meta = Meta(rp.Name);
                    var editor = new FriendlyParamItem(rp.Name, TuningEditors.EditorMeta(rp.Name, meta, value, rp.Def.Increment), value, OnEdited);
                    if (PendingFor(rp.Name, value) is { } pending)
                        editor.SetValues(value, pending);
                    var item = new TuningParamItem(rp.Label, editor, TuningEditors.Tip(rp.Name, meta));
                    items.Add(item);
                    byName[rp.Name] = item;
                }

                groups.Add(new TuningGroupItem(g.Id, g.Title, items));
            }

            sections.Add(new TuningSectionItem(section.Key, groups));
        }

        // edits of parameters no longer on the page cannot be written from it
        Edits.RemoveAll(Edits.Pending.Keys.Where(k => !byName.ContainsKey(k)).ToList());

        _byName = byName;
        Sections = sections;
        ShownCount = byName.Count;
        IsEmpty = byName.Count == 0;
        CanLockRollPitch = _layout.HasLocks;
        LockRollPitch = _layout.LockByDefault(p.Get, p.Has);
        VehicleText = Firmware == Firmwares.ArduPlane && _layout.Groups.Any(g => g.Section == TuningLayouts.QuadPlaneSection)
            ? "ArduPlane with QuadPlane"
            : FirmwareName(Firmware);
        OnPropertyChanged(nameof(AllItems));
        OnPropertyChanged(nameof(Layout));
    }

    internal override void SyncValues()
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
}
