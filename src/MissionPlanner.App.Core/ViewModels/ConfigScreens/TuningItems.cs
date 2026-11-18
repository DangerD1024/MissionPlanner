using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using MissionPlanner.App.Core.Params;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>Editor helpers shared by the tuning pages.</summary>
public static class TuningEditors
{
    /// <summary>
    /// A step for a value without a documented increment: whole numbers step by 1, gains by a
    /// fraction fitting their range or size (0.135 steps by 0.001).
    /// </summary>
    public static double GuessIncrement(double value, double? min, double? max)
    {
        static bool Whole(double v) => v == Math.Floor(v);
        if (min is { } a && max is { } b && b > a)
        {
            if (Whole(value) && Whole(a) && Whole(b) && b - a >= 10)
                return 1;
            var span = b - a;
            return span <= 2 ? 0.001 : span <= 20 ? 0.01 : span <= 200 ? 0.1 : 1;
        }

        var size = Math.Abs(value);
        if (Whole(value) && size >= 10)
            return 1;
        return size < 1 ? 0.001 : size < 10 ? 0.01 : 0.1;
    }

    /// <summary>
    /// Documentation for an editor: the parameter's own (with a step guessed when it has none), or
    /// an undocumented stand-in so the value can still be edited.
    /// </summary>
    public static ParamMeta EditorMeta(string name, ParamMeta? meta, double value, double? increment = null)
    {
        if (meta == null)
            return new ParamMeta(name, "", "", "", null, null, increment ?? GuessIncrement(value, null, null), [], [], "", false, false);

        // a gain documented with a few suggested settings is still a number to tune
        if (IsSuggestionList(meta))
            meta = meta with { Values = [] };
        if (meta.Increment is > 0)
            return meta;
        return meta with { Increment = increment ?? GuessIncrement(value, meta.Min, meta.Max) };
    }

    /// <summary>
    /// Documented values that are suggestions on a continuous scale (ATC_INPUT_TC: 0.15 Medium,
    /// ATC_ACC_R_MAX: 720 Slow...) rather than an enumeration (INS_HNTCH_MODE: range 0-5, a name
    /// for each): a value is not a whole number, or the range holds more values than are named.
    /// </summary>
    public static bool IsSuggestionList(ParamMeta meta) =>
        meta.Values.Count > 0 &&
        (meta.Values.Any(v => v.Key != Math.Floor(v.Key)) ||
         meta is { Min: { } min, Max: { } max } && Math.Floor(max) - Math.Ceiling(min) + 1 > meta.Values.Count);

    /// <summary>"NAME: Display name", the description and the documented range, for tooltips.</summary>
    public static string Tip(string name, ParamMeta? meta)
    {
        var sb = new StringBuilder(name);
        if (meta == null)
            return sb.Append("\n(no documentation loaded)").ToString();
        if (meta.DisplayName.Length > 0)
            sb.Append(": ").Append(meta.DisplayName);
        if (meta.Description.Length > 0)
            sb.Append("\n\n").Append(meta.Description);
        var range = new List<string>();
        if (meta.Min is { } a && meta.Max is { } b)
            range.Add($"Range {ParamValues.Format(a)} to {ParamValues.Format(b)}");
        if (meta.Units.Length > 0)
            range.Add(meta.Units);
        if (meta.RebootRequired)
            range.Add("reboot required");
        if (range.Count > 0)
            sb.Append("\n\n").Append(string.Join(" · ", range));
        if (IsSuggestionList(meta))
            sb.Append("\nSuggested: ").Append(string.Join(", ", meta.Values.Select(v => $"{ParamValues.Format(v.Key)} {v.Value}")));
        return sb.ToString();
    }

    /// <summary>
    /// The Copter 4.7 note for a parameter whose units or scale changed with its new name
    /// ("PSC_ACCZ_P → PSC_D_ACC_P: value is now 10x smaller"), or "" (also for plain renames).
    /// </summary>
    public static string ChangeNote(string name) =>
        ParamChanges47.changedByNewParamWarning(name) is { } note && !note.Contains("name only", StringComparison.OrdinalIgnoreCase) ? note : "";
}

/// <summary>One value on an Extended Tuning card: a short label and the parameter's editor.</summary>
public sealed class TuningParamItem(string label, FriendlyParamItem editor, string tip)
{
    public string Label { get; } = label;

    public FriendlyParamItem Editor { get; } = editor;

    /// <summary>Units beside number boxes (lists and bitmasks use the space).</summary>
    public bool ShowUnits => Editor.IsNumber && Editor.HasUnits;

    public string Name => Editor.Name;

    public string Tip { get; } = tip;

    /// <summary>Copter 4.7 renamed and rescaled some parameters; shown so old values are not copied blindly.</summary>
    public string ChangeNote { get; } = TuningEditors.ChangeNote(editor.Name);

    public bool HasChangeNote => ChangeNote.Length > 0;
}

/// <summary>An Extended Tuning card.</summary>
public sealed class TuningGroupItem(string id, string title, IReadOnlyList<TuningParamItem> items)
{
    public string Id { get; } = id;

    public string Title { get; } = title;

    public IReadOnlyList<TuningParamItem> Items { get; } = items;
}

/// <summary>A heading and its cards.</summary>
public sealed class TuningSectionItem(string title, IReadOnlyList<TuningGroupItem> groups)
{
    public string Title { get; } = title;

    public IReadOnlyList<TuningGroupItem> Groups { get; } = groups;
}

/// <summary>A parameter a Basic Tuning slider also sets, with its current and queued values.</summary>
public sealed partial class TuningLinkItem(string name, double multiplier) : ObservableObject
{
    public string Name { get; } = name;

    public double Multiplier { get; } = multiplier;

    public string MultiplierText => "×" + ParamValues.Format(Multiplier);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValueText), nameof(IsModified))]
    private double? _current;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValueText), nameof(IsModified))]
    private double? _pending;

    public bool IsModified => Pending.HasValue;

    public string ValueText => Pending is { } p
        ? $"{(Current is { } c ? ParamValues.Format(c) : "?")} → {ParamValues.Format(p)}"
        : Current is { } v ? ParamValues.Format(v) : "?";
}

/// <summary>A Basic Tuning slider with its value box, in the style of Mission Planner's ConfigSimplePids.</summary>
public sealed partial class TuningSliderItem : ObservableObject
{
    /// <summary>The slider runs over 0..Steps; the item maps positions to values (and snaps them).</summary>
    public const double Steps = 1000;

    private readonly Action<TuningSliderItem, double> _edit;
    private bool _updating;
    private double _position;
    private decimal? _boxValue;

    public TuningSliderItem(TuningSliderDef def, ParamMeta? meta, double value, IReadOnlyList<TuningLinkItem> links,
        Action<TuningSliderItem, double> edit)
    {
        Def = def;
        Meta = meta;
        Links = links;
        _edit = edit;
        _value = value;

        // the documented range wins over the xml's, as in Mission Planner; never hide the current value
        var (min, max) = meta is { Min: { } a, Max: { } b } && b > a ? (a, b) : (def.Min, def.Max);
        Minimum = Math.Min(min, value);
        Maximum = Math.Max(max, value);
        if (Maximum <= Minimum)
            Maximum = Minimum + 1;
        Increment = meta?.Increment is > 0 ? meta.Increment.Value : TuningEditors.GuessIncrement(value, Minimum, Maximum);
        Show();
    }

    public TuningSliderDef Def { get; }

    public ParamMeta? Meta { get; }

    public string Name => Def.Name;

    public string Title => Def.Title;

    public string Description => Def.Description.Length > 0 ? Def.Description : Meta?.Description ?? "";

    public string Tip => TuningEditors.Tip(Name, Meta);

    public string Units => Meta?.Units ?? "";

    public bool HasUnits => Units.Length > 0;

    public string ChangeNote => TuningEditors.ChangeNote(Name);

    public bool HasChangeNote => ChangeNote.Length > 0;

    /// <summary>Right is "less" (e.g. a time constant where smaller is crisper).</summary>
    public bool Reversed => Def.Reversed;

    public double Minimum { get; }

    public double Maximum { get; }

    public double Increment { get; }

    public string LeftText => ParamValues.Format(Reversed ? Maximum : Minimum);

    public string RightText => ParamValues.Format(Reversed ? Minimum : Maximum);

    public decimal BoxMinimum => FriendlyParamItem.ToDecimal(Minimum);

    public decimal BoxMaximum => FriendlyParamItem.ToDecimal(Maximum);

    public decimal BoxIncrement => FriendlyParamItem.ToDecimal(Increment);

    /// <summary>Keyboard step of the slider, in positions.</summary>
    public double SmallChange => Math.Max(1, Increment / (Maximum - Minimum) * Steps);

    public double LargeChange => Steps / 10;

    public IReadOnlyList<TuningLinkItem> Links { get; }

    public bool HasLinks => Links.Count > 0;

    /// <summary>The vehicle's value.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayValue), nameof(ValueText), nameof(OriginalText))]
    private double _value;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayValue), nameof(ValueText), nameof(IsModified), nameof(OriginalText))]
    private double? _pendingValue;

    public double DisplayValue => PendingValue ?? Value;

    public string ValueText => ParamValues.Format(DisplayValue);

    public bool IsModified => PendingValue.HasValue;

    /// <summary>"was 0.135" while an edit is queued.</summary>
    public string OriginalText => IsModified ? "vehicle " + ParamValues.Format(Value) : "";

    /// <summary>Slider position (0..<see cref="Steps"/>); setting it queues the snapped value.</summary>
    public double Position
    {
        get => _position;
        set
        {
            if (double.IsNaN(value) || Math.Abs(value - _position) < 1e-9)
                return;
            _position = value;
            OnPropertyChanged();
            if (!_updating)
                _edit(this, Snap(FromPosition(value)));
        }
    }

    /// <summary>Value box. Null pushes (from control recycling) are ignored.</summary>
    public decimal? BoxValue
    {
        get => _boxValue;
        set
        {
            if (value == null || value == _boxValue)
                return;
            _boxValue = value;
            OnPropertyChanged();
            if (!_updating)
                _edit(this, TuningSimplePids.Clean((double)value.Value));
        }
    }

    public double ToPosition(double value) =>
        Math.Clamp((value - Minimum) / (Maximum - Minimum), 0, 1) * Steps;

    public double FromPosition(double position) =>
        Minimum + Math.Clamp(position / Steps, 0, 1) * (Maximum - Minimum);

    /// <summary>Rounds to the increment (counted from zero, as the documentation states them) inside the range.</summary>
    public double Snap(double value)
    {
        var snapped = Math.Round(value / Increment) * Increment;
        if (snapped < Minimum - 1e-12)
            snapped += Increment;
        if (snapped > Maximum + 1e-12)
            snapped -= Increment;
        return TuningSimplePids.Clean(Math.Clamp(snapped, Minimum, Maximum));
    }

    /// <summary>Sets the vehicle value and the queued edit without reporting an edit.</summary>
    internal void SetValues(double value, double? pending)
    {
        Value = value;
        PendingValue = pending;
        Show();
    }

    /// <summary>Marks the result of an edit the page has queued (or dropped).</summary>
    internal void SetPending(double? pending)
    {
        PendingValue = pending;
        Show();
    }

    private void Show()
    {
        _updating = true;
        try
        {
            var position = ToPosition(DisplayValue);
            if (Math.Abs(position - _position) > 1e-9)
            {
                _position = position;
                OnPropertyChanged(nameof(Position));
            }

            var box = FriendlyParamItem.ToDecimal(DisplayValue);
            if (box != _boxValue)
            {
                _boxValue = box;
                OnPropertyChanged(nameof(BoxValue));
            }
        }
        finally
        {
            _updating = false;
        }
    }
}

/// <summary>A heading and its sliders.</summary>
public sealed class TuningSliderSection(string title, IReadOnlyList<TuningSliderItem> items)
{
    public string Title { get; } = title;

    public bool HasTitle => Title.Length > 0;

    public IReadOnlyList<TuningSliderItem> Items { get; } = items;
}
