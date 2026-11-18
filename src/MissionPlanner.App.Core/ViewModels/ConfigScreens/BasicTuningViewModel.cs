using CommunityToolkit.Mvvm.ComponentModel;
using log4net;
using MissionPlanner.App.Core.Params;
using MissionPlanner.ArduPilot;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>
/// Basic Tuning, as Mission Planner shows it per vehicle: for copters the ConfigSimplePids sliders
/// from acsimplepids.xml, which write to the vehicle as they move (a slider also sets its related
/// values: roll/pitch sensitivity the rate I terms and the pitch twin, climb sensitivity the accel I
/// at twice P) and list what they set below; for planes the ConfigArduplane page and for rovers the
/// ConfigArdurover page, whose edits wait for Write Params.
/// </summary>
public sealed partial class BasicTuningViewModel : TuningPageViewModel
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(BasicTuningViewModel));

    private Dictionary<string, TuningSliderItem> _sliderByName = new(StringComparer.Ordinal);
    private readonly Dictionary<TuningSliderItem, double> _queued = new();
    private CancellationTokenSource? _debounce;
    private bool _flushing;

    public BasicTuningViewModel(AppServices services)
        : base(TuningLayouts.BasicTitle, services)
    {
    }

    /// <summary>The copter sliders (empty for other vehicles).</summary>
    [ObservableProperty]
    private IReadOnlyList<TuningSliderItem> _sliders = [];

    [ObservableProperty]
    private bool _hasSliders;

    /// <summary>Mission Planner's box under the sliders: the note, then what the last move set.</summary>
    [ObservableProperty]
    private string _infoText = TuningSimplePids.Note;

    /// <summary>How long a slider rests before its value is sent (a drag sends once, at the end).</summary>
    internal TimeSpan WriteDelay { get; set; } = TimeSpan.FromMilliseconds(400);

    public TuningSliderItem? Slider(string name) => _sliderByName.GetValueOrDefault(name);

    /// <summary>Whether a slider value is still on its way to the vehicle.</summary>
    public bool IsWriting => _flushing || _queued.Count > 0;

    protected override TuningLayout? CardLayout() => TuningLayouts.Basic(Firmware);

    protected override IEnumerable<string> ScreenParams =>
        base.ScreenParams.Concat(_sliderByName.Values.SelectMany(i => i.Links.Select(l => l.Name).Prepend(i.Name)));

    internal override void Rebuild()
    {
        base.Rebuild();

        var p = Services.Params;
        var defs = TuningSimplePids.Resolve(TuningSimplePids.For(Firmware, XmlPath ?? TuningSimplePids.DefaultXmlPath), p.Has);
        var byName = new Dictionary<string, TuningSliderItem>(StringComparer.Ordinal);
        var items = new List<TuningSliderItem>();
        foreach (var def in defs)
        {
            var value = p.Get(def.Name) ?? 0;
            var links = def.Relations.Where(r => p.Has(r.Name)).ToList();
            var item = new TuningSliderItem(def, Meta(def.Name), value, links, OnSliderEdited);
            items.Add(item);
            byName[def.Name] = item;
        }

        CancelDebounce();
        _queued.Clear();
        _sliderByName = byName;
        Sliders = items;
        HasSliders = items.Count > 0;
        IsEmpty = !HasSliders && !HasCards;
        Heading = TuningLayouts.BasicTitle;
        VehicleText = FirmwareName(Firmware);
    }

    internal override void SyncValues()
    {
        base.SyncValues();
        if (IsWriting)
            return;
        foreach (var item in _sliderByName.Values)
        {
            var value = Services.Params.Get(item.Name) ?? item.Value;
            if (!ParamValues.Same(value, item.Value) || item.PendingValue != null)
                item.SetValues(value, null);
        }
    }

    /// <summary>Ctrl+S on the slider page sends a value still resting.</summary>
    protected override Task WriteCoreAsync() => HasSliders ? FlushAsync() : base.WriteCoreAsync();

    /// <summary>
    /// A slider or its box moved: the value goes to the vehicle once the slider rests, as
    /// ConfigSimplePids writes on every change. Back at the vehicle's value, nothing is sent.
    /// </summary>
    private void OnSliderEdited(TuningSliderItem item, double value)
    {
        if (ParamValues.Same(value, item.Value) && !_flushing)
        {
            _queued.Remove(item);
            item.SetPending(null);
            OnPropertyChanged(nameof(IsWriting));
            return;
        }

        _queued[item] = value;
        item.SetPending(value);
        OnPropertyChanged(nameof(IsWriting));

        CancelDebounce();
        if (WriteDelay == Timeout.InfiniteTimeSpan)
            return;
        var cts = _debounce = new CancellationTokenSource();
        _ = Task.Delay(WriteDelay, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
                Services.Dispatcher.Post(() => _ = FlushAsync());
        }, TaskScheduler.Default);
    }

    private void CancelDebounce()
    {
        _debounce?.Cancel();
        _debounce = null;
    }

    /// <summary>
    /// Sends the resting slider values with their related values (value × multiplier) and lists
    /// what was set, as Mission Planner's box under the sliders does. Call on the UI thread.
    /// </summary>
    public async Task FlushAsync()
    {
        CancelDebounce();
        if (_flushing)
            return;
        _flushing = true;
        try
        {
            while (_queued.Count > 0)
            {
                var batch = _queued.ToList();
                _queued.Clear();

                if (!Services.Session.IsConnected)
                {
                    foreach (var (item, _) in batch)
                        item.SetPending(null);
                    InfoText = "Not connected: nothing was written.";
                    break;
                }

                var values = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var (item, value) in batch)
                {
                    foreach (var kv in TuningSimplePids.Values(item.Def, value, Services.Params.Has))
                        values[kv.Key] = kv.Value;
                }

                IReadOnlyCollection<string> failed;
                var lines = new List<string>();
                try
                {
                    failed = (await Services.Params.SetManyAsync(values)).Failed;
                }
                catch (Exception ex)
                {
                    Log.Warn("simple pids", ex);
                    failed = values.Keys;
                    lines.Add("Failed to change setting " + ex.Message);
                }

                // in the order the slider sets them: the parameter, then its relations
                foreach (var (name, value) in values)
                    lines.Add(failed.Contains(name) ? $"Failed to set {name}" : $"set {name} {ParamValues.Format(value)}");
                InfoText = string.Join("\n", lines);
                StatusText = failed.Count == 0 ? "" : $"{failed.Count} value{(failed.Count == 1 ? "" : "s")} not set.";

                foreach (var (item, _) in batch)
                {
                    if (!_queued.ContainsKey(item))
                        item.SetValues(Services.Params.Get(item.Name) ?? item.Value, null);
                }
            }
        }
        finally
        {
            _flushing = false;
            OnPropertyChanged(nameof(IsWriting));
        }
    }
}
