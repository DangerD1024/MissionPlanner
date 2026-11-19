using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.Params;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>A documented value of an enumerated parameter, as the value cell's list shows it.</summary>
public sealed record ParamChoice(double Value, string Label)
{
    public string ValueText => ParamValues.Format(Value);

    /// <summary>"1: Enabled", as Mission Planner lists options.</summary>
    public string Text => $"{ValueText}: {Label}";

    public override string ToString() => Text;
}

/// <summary>What a parameter page does when a row's editors are used.</summary>
public interface IParamRowHost
{
    /// <summary>Checks and queues a value picked in the value cell (from the list or the bitmask editor).</summary>
    Task<bool> QueueValueAsync(ParamRow row, double value);

    /// <summary>Opens the bitmask editor for the row and queues its result.</summary>
    Task<bool> EditBitmaskAsync(ParamRow row);
}

/// <summary>One parameter on the parameter pages (Full Parameter List, Full Parameter Tree, User Params).</summary>
public sealed partial class ParamRow : ObservableObject
{
    private bool _quiet;
    private IReadOnlyList<ParamChoice>? _choices;

    public ParamRow(string name, double value, double? defaultValue, ParamMeta? meta, bool favourite = false)
    {
        Name = name;
        _value = value;
        Default = defaultValue;
        Meta = meta;
        _isFavourite = favourite;
        Kind = meta?.Values.Count > 0 ? ParamEditorKind.Options
            : meta?.Bitmask.Count > 0 ? ParamEditorKind.Bitmask
            : ParamEditorKind.Number;
        (OptionsText, OptionsTip) = ParamText.Options(meta);
    }

    public string Name { get; }

    public ParamMeta? Meta { get; }

    /// <summary>The editor the value cell offers: a list for enumerations, the bitmask editor, or a number box.</summary>
    public ParamEditorKind Kind { get; }

    public bool IsOptions => Kind == ParamEditorKind.Options;

    public bool IsBitmask => Kind == ParamEditorKind.Bitmask;

    /// <summary>The vehicle's default (known when the list came over MAVFTP).</summary>
    public double? Default { get; }

    public string DefaultText => Default is { } d ? ParamValues.Format(d) : "";

    public string DisplayName => Meta?.DisplayName ?? "";

    public string Description => Meta?.Description ?? "";

    public string Units => Meta?.Units ?? "";

    public string OptionsText { get; }

    public string OptionsTip { get; }

    /// <summary>Tooltips are null when there is nothing to show (an empty tip would still pop up).</summary>
    public string? OptionsTipOrNull => OptionsTip.Length > 0 ? OptionsTip : null;

    public string? DescriptionTip => Description.Length > 0 ? Description : null;

    public string? NameTip => Meta == null ? null : $"{Meta.DisplayName}{(Meta.RebootRequired ? "\nReboot required after changing" : "")}{(Meta.ReadOnly ? "\nRead only" : "")}";

    public bool IsReadOnly => Meta?.ReadOnly == true;

    public bool IsEditable => !IsReadOnly;

    /// <summary>The documented values of an enumerated parameter, for the value cell's list.</summary>
    public IReadOnlyList<ParamChoice> Choices =>
        _choices ??= Meta?.Values.Select(v => new ParamChoice(v.Key, v.Value)).ToList() ?? [];

    /// <summary>The vehicle's value (or the file's, offline).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayValue), nameof(ValueText), nameof(IsNonDefault), nameof(ValueLabel), nameof(ValueTip))]
    private double _value;

    /// <summary>An edit waiting for Write Params.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayValue), nameof(ValueText), nameof(IsModified), nameof(IsNonDefault),
        nameof(ValueLabel), nameof(ValueTip))]
    private double? _pendingValue;

    /// <summary>Set when the last typed value was rejected.</summary>
    [ObservableProperty]
    private bool _isInvalid;

    [ObservableProperty]
    private bool _isFavourite;

    /// <summary>Text in the value cell's editor while it is open.</summary>
    [ObservableProperty]
    private string _editText = "";

    public double DisplayValue => PendingValue ?? Value;

    public string ValueText => ParamValues.Format(DisplayValue);

    /// <summary>
    /// What the value means: the documented label of an enumerated value, or the set bits of a
    /// bitmask. Empty for plain numbers and undocumented values.
    /// </summary>
    public string ValueLabel => Kind switch
    {
        ParamEditorKind.Options => Meta!.Values.FirstOrDefault(v => ParamValues.Same(v.Key, DisplayValue)).Value ?? "",
        ParamEditorKind.Bitmask => ParamBitmask.Describe(DisplayValue, Meta!.Bitmask) is { Count: > 0 } set ? string.Join(", ", set) : "none",
        _ => "",
    };

    /// <summary>The value cell's tooltip: the value and its meaning (every set bit on its own line).</summary>
    public string? ValueTip => Kind switch
    {
        ParamEditorKind.Options => ValueLabel.Length > 0 ? $"{ValueText}: {ValueLabel}" : $"{ValueText} (not a documented value)",
        ParamEditorKind.Bitmask => $"{ValueText}\n" + (ParamBitmask.Describe(DisplayValue, Meta!.Bitmask) is { Count: > 0 } set
            ? string.Join("\n", set)
            : "no bit set"),
        _ => null,
    };

    public bool IsModified => PendingValue.HasValue;

    public bool IsNonDefault => Default is { } d && !ParamValues.Same(d, DisplayValue);

    /// <summary>The page that queues this row's edits.</summary>
    internal IParamRowHost? Host { get; set; }

    /// <summary>Called when the user ticks or clears the favourite box.</summary>
    internal Action<ParamRow>? FavouriteToggled { get; set; }

    /// <summary>Queues a documented value picked from the value cell's list.</summary>
    public async Task<bool> ChooseAsync(ParamChoice choice) =>
        Host != null && await Host.QueueValueAsync(this, choice.Value);

    /// <summary>Opens the bitmask editor (Mission Planner's "Set Bitmask" button).</summary>
    [RelayCommand]
    private async Task EditBitmaskAsync()
    {
        if (Host != null)
            await Host.EditBitmaskAsync(this);
    }

    /// <summary>Sets the favourite flag without reporting it as a user change.</summary>
    internal void SetFavouriteQuietly(bool value)
    {
        _quiet = true;
        try
        {
            IsFavourite = value;
        }
        finally
        {
            _quiet = false;
        }
    }

    partial void OnIsFavouriteChanged(bool value)
    {
        if (!_quiet)
            FavouriteToggled?.Invoke(this);
    }

    public ParamEntry ToEntry() => new(Name, DisplayValue, Default, Meta);
}
