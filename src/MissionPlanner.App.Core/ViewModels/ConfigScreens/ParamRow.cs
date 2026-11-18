using CommunityToolkit.Mvvm.ComponentModel;
using MissionPlanner.App.Core.Params;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>One line of the Full Parameter List.</summary>
public sealed partial class ParamRow : ObservableObject
{
    private bool _quiet;

    public ParamRow(string name, double value, double? defaultValue, ParamMeta? meta, bool favourite = false)
    {
        Name = name;
        _value = value;
        Default = defaultValue;
        Meta = meta;
        _isFavourite = favourite;
        (OptionsText, OptionsTip) = ParamText.Options(meta);
    }

    public string Name { get; }

    public ParamMeta? Meta { get; }

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

    /// <summary>The vehicle's value (or the file's, offline).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayValue), nameof(ValueText), nameof(IsNonDefault))]
    private double _value;

    /// <summary>An edit waiting for Write Params.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayValue), nameof(ValueText), nameof(IsModified), nameof(IsNonDefault))]
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

    public bool IsModified => PendingValue.HasValue;

    public bool IsNonDefault => Default is { } d && !ParamValues.Same(d, DisplayValue);

    /// <summary>Called when the user ticks or clears the favourite box.</summary>
    internal Action<ParamRow>? FavouriteToggled { get; set; }

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
