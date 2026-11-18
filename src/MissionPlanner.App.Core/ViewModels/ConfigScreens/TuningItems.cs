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
    /// Documentation for an editor: the parameter's own (with Mission Planner's step, or a guessed
    /// one, when it documents none), or an undocumented stand-in so the value can still be edited.
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
    /// The Copter/QuadPlane 4.7 note for a parameter under its new name ("PSC_ACCZ_P → PSC_D_ACC_P:
    /// value is now 10x smaller"), or "". Mission Planner shows it when the box is entered.
    /// </summary>
    public static string ChangeNote(string name) => ParamChanges47.changedByNewParamWarning(name) ?? "";

    /// <summary>Whether a 4.7 note means more than a new name (units or scale changed).</summary>
    public static bool IsScaleChange(string note) =>
        note.Length > 0 && !note.Contains("name only", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One value on a tuning card: Mission Planner's label and the parameter's editor.</summary>
public sealed class TuningParamItem(string label, FriendlyParamItem editor, string tip, double labelWidth)
{
    public string Label { get; } = label;

    public FriendlyParamItem Editor { get; } = editor;

    public string Name => Editor.Name;

    public string Tip { get; } = tip;

    public double LabelWidth { get; } = labelWidth;

    /// <summary>Units beside number boxes, unless the label already says them ("Climb Max (m/s)").</summary>
    public bool ShowUnits => Editor.IsNumber && Editor.HasUnits && !Label.Contains('(');

    /// <summary>The units column of number boxes (kept, empty, so the boxes line up).</summary>
    public string UnitsText => ShowUnits ? Editor.Units : "";

    /// <summary>What changed in 4.7 for this name; shown below the cards while the box has focus.</summary>
    public string ChangeNote { get; } = TuningEditors.ChangeNote(editor.Name);

    public bool HasChangeNote => ChangeNote.Length > 0;
}

/// <summary>A column of values inside a card.</summary>
public sealed class TuningColumnItem(IReadOnlyList<TuningParamItem> items)
{
    public IReadOnlyList<TuningParamItem> Items { get; } = items;
}

/// <summary>A tuning card (a Mission Planner group box).</summary>
public sealed class TuningGroupItem(string id, string title, IReadOnlyList<TuningColumnItem> columns)
{
    public string Id { get; } = id;

    public string Title { get; } = title;

    public IReadOnlyList<TuningColumnItem> Columns { get; } = columns;

    public IEnumerable<TuningParamItem> Items => Columns.SelectMany(c => c.Items);

    /// <summary>
    /// Splits values into columns filled top to bottom (Enabled, Mode... | Attenuation, Bandwidth...).
    /// Always <paramref name="columns"/> of them, so a card showing fewer values keeps its boxes narrow.
    /// </summary>
    public static IReadOnlyList<TuningColumnItem> Split(IReadOnlyList<TuningParamItem> items, int columns)
    {
        if (columns <= 1)
            return [new TuningColumnItem(items)];
        var perColumn = Math.Max(1, (int)Math.Ceiling(items.Count / (double)columns));
        return Enumerable.Range(0, columns)
            .Select(c => new TuningColumnItem(items.Skip(c * perColumn).Take(perColumn).ToList()))
            .ToList();
    }
}

/// <summary>Cards stacked in one column of a row, <see cref="Width"/> wide.</summary>
public sealed class TuningCellItem(IReadOnlyList<TuningGroupItem> groups, double width)
{
    public IReadOnlyList<TuningGroupItem> Groups { get; } = groups;

    public double Width { get; } = width;
}

/// <summary>A row of card columns, with Mission Planner's "Lock Pitch and Roll Values" above it when it has one.</summary>
public sealed class TuningRowItem(TuningPageViewModel page, IReadOnlyList<TuningCellItem> cells, bool lockAbove)
{
    public TuningPageViewModel Page { get; } = page;

    public IReadOnlyList<TuningCellItem> Cells { get; } = cells;

    public bool LockAbove { get; } = lockAbove;
}

/// <summary>A Basic Tuning slider with its value box, as Mission Planner's ConfigSimplePids range controls.</summary>
public sealed partial class TuningSliderItem : ObservableObject
{
    /// <summary>The slider runs over 0..Steps; the item maps positions to values (and snaps them).</summary>
    public const double Steps = 1000;

    private readonly Action<TuningSliderItem, double> _edit;
    private bool _updating;
    private double _position;
    private decimal? _boxValue;

    public TuningSliderItem(TuningSliderDef def, ParamMeta? meta, double value, IReadOnlyList<TuningRelation> links,
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

    public string Tip
    {
        get
        {
            var tip = TuningEditors.Tip(Name, Meta);
            if (Links.Count > 0)
                tip += "\n\nAlso sets " + string.Join(", ", Links.Select(l => l.Multiplier == 1 ? l.Name : $"{l.Name} (×{ParamValues.Format(l.Multiplier)})"));
            return tip;
        }
    }

    public string Units => Meta?.Units ?? "";

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

    /// <summary>The related parameters the vehicle has, set with the slider (value × multiplier).</summary>
    public IReadOnlyList<TuningRelation> Links { get; }

    /// <summary>The vehicle's value.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayValue), nameof(ValueText))]
    private double _value;

    /// <summary>A value on its way to the vehicle.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayValue), nameof(ValueText), nameof(IsModified))]
    private double? _pendingValue;

    public double DisplayValue => PendingValue ?? Value;

    public string ValueText => ParamValues.Format(DisplayValue);

    public bool IsModified => PendingValue.HasValue;

    /// <summary>Slider position (0..<see cref="Steps"/>); setting it reports the snapped value.</summary>
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

    /// <summary>Sets the vehicle value and the value being written without reporting an edit.</summary>
    internal void SetValues(double value, double? pending)
    {
        Value = value;
        PendingValue = pending;
        Show();
    }

    /// <summary>Marks a value on its way to the vehicle (or none).</summary>
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
