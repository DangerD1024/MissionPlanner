using CommunityToolkit.Mvvm.ComponentModel;
using MissionPlanner.App.Core.Services;
using MissionPlanner.ArduPilot;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>
/// Basic Tuning: a few sliders for the main tuning values. For copters these are Mission Planner's
/// ConfigSimplePids sliders from acsimplepids.xml, where one slider also sets related values
/// (roll/pitch sensitivity sets the rate I terms and the pitch twin; climb sensitivity sets the
/// accel I term at twice P). Planes and rovers get their main gains. Changes are queued until Write Params.
/// </summary>
public sealed partial class BasicTuningViewModel : TuningPageViewModel
{
    private Dictionary<string, TuningSliderItem> _byName = new(StringComparer.Ordinal);

    public BasicTuningViewModel(AppServices services)
        : base("Basic Tuning", services)
    {
    }

    [ObservableProperty]
    private IReadOnlyList<TuningSliderSection> _sections = [];

    [ObservableProperty]
    private string _intro = "";

    public IReadOnlyCollection<TuningSliderItem> AllItems => _byName.Values;

    public TuningSliderItem? Item(string name) => _byName.GetValueOrDefault(name);

    protected override string ItemNoun => "slider";

    protected override IEnumerable<string> ScreenParams =>
        _byName.Values.SelectMany(i => i.Links.Select(l => l.Name).Prepend(i.Name));

    internal override void Rebuild()
    {
        var p = Services.Params;
        var defs = TuningSimplePids.Resolve(TuningSimplePids.For(Firmware, p.Get, XmlPath ?? TuningSimplePids.DefaultXmlPath), p.Has);

        var byName = new Dictionary<string, TuningSliderItem>(StringComparer.Ordinal);
        var sections = new List<TuningSliderSection>();
        foreach (var section in defs.GroupBy(d => d.Section))
        {
            var items = new List<TuningSliderItem>();
            foreach (var def in section)
            {
                var value = p.Get(def.Name) ?? 0;
                var links = def.Relations.Where(r => p.Has(r.Name)).Select(r => new TuningLinkItem(r.Name, r.Multiplier)).ToList();
                var item = new TuningSliderItem(def, Meta(def.Name), value, links, OnSliderEdited);
                items.Add(item);
                byName[def.Name] = item;
            }

            sections.Add(new TuningSliderSection(section.Key, items));
        }

        _byName = byName;
        var onPage = ScreenParams.ToHashSet(StringComparer.Ordinal);
        Edits.RemoveAll(Edits.Pending.Keys.Where(k => !onPage.Contains(k)).ToList());
        Sections = sections;
        SyncValues();

        ShownCount = byName.Count;
        IsEmpty = byName.Count == 0;
        VehicleText = FirmwareName(Firmware);
        Intro = Firmware == Firmwares.ArduCopter2
            ? "Slide to adjust the main tuning values. Values linked to a slider are set with it, as Mission Planner's Basic Tuning does. Nothing is sent until you press Write Params."
            : "The main gains of the vehicle. Slide or type a value; nothing is sent until you press Write Params. Every controller is on Extended Tuning.";
        OnPropertyChanged(nameof(AllItems));
    }

    internal override void SyncValues()
    {
        foreach (var item in _byName.Values)
        {
            var value = Services.Params.Get(item.Name) ?? item.Value;
            var pending = PendingFor(item.Name, value);
            if (!ParamValues.Same(value, item.Value) || pending != item.PendingValue)
                item.SetValues(value, pending);
            SyncLinks(item, pending.HasValue);
        }
    }

    /// <summary>
    /// A slider move queues its value and, as ConfigSimplePids writes them, every related value at
    /// value × multiplier. Back at the vehicle's value, the related edits are dropped too.
    /// </summary>
    private void OnSliderEdited(TuningSliderItem item, double value)
    {
        var modified = Edits.Set(item.Name, value, item.Value);
        item.SetPending(modified ? value : null);
        foreach (var link in item.Links)
        {
            if (modified)
                Edits.Set(link.Name, TuningSimplePids.Linked(value, link.Multiplier), Services.Params.Get(link.Name));
            else
                Edits.Remove(link.Name);
        }

        SyncLinks(item, modified);
        StatusText = modified
            ? $"{item.Name} = {ParamValues.Format(value)}" +
              (item.Links.Count > 0 ? $", with {string.Join(", ", item.Links.Select(l => l.Name))}" : "") + " (not written yet)"
            : "";
    }

    private void SyncLinks(TuningSliderItem item, bool sliderModified)
    {
        foreach (var link in item.Links)
        {
            if (!sliderModified)
                Edits.Remove(link.Name);
            link.Current = Services.Params.Get(link.Name);
            link.Pending = Edits.TryGet(link.Name, out var v) ? v : null;
        }
    }
}
