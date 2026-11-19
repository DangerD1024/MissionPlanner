using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;

/// <summary>
/// A number parameter shown in other units than it is stored in (centidegrees as degrees,
/// Mission Planner's scaled MavlinkNumericUpDown). Edits go through the wrapped
/// <see cref="ParamField"/>, so pauses, queuing, failures and reboot tracking stay the same.
/// </summary>
public sealed partial class ScaledField : ObservableObject
{
    private bool _syncing;

    /// <param name="field">The parameter.</param>
    /// <param name="scale">Stored value = shown value × scale (100 for centidegrees shown in degrees).</param>
    /// <param name="units">Units of the shown value.</param>
    /// <param name="min">Lowest shown value.</param>
    /// <param name="max">Highest shown value.</param>
    /// <param name="increment">Step of the shown value.</param>
    /// <param name="label">Label (defaults to the field's).</param>
    public ScaledField(ParamField field, double scale, string units, double min, double max, double increment, string? label = null)
    {
        Field = field;
        Scale = scale == 0 ? 1 : scale;
        Units = units;
        RangeMin = min;
        RangeMax = max;
        Increment = (decimal)increment;
        Label = label ?? field.Label;
        FormatString = increment >= 1 ? "0" : "0.###";
        field.PropertyChanged += OnFieldChanged;
        Sync();
    }

    public ParamField Field { get; }

    public double Scale { get; }

    public string Label { get; }

    public string Name => Field.Name;

    public string Units { get; }

    public double RangeMin { get; }

    public double RangeMax { get; }

    public decimal Increment { get; }

    public string FormatString { get; }

    public string ToolTip => Field.ToolTip + $"\nShown in {Units} (stored × {Scale.ToString(System.Globalization.CultureInfo.InvariantCulture)}).";

    [ObservableProperty]
    private decimal _minimum;

    [ObservableProperty]
    private decimal _maximum;

    /// <summary>The value in shown units; setting it edits the parameter.</summary>
    [ObservableProperty]
    private decimal? _value;

    partial void OnValueChanged(decimal? value)
    {
        if (_syncing || value is not { } v)
            return;
        Field.Edit(Math.Round((double)v * Scale, 6));
    }

    /// <summary>Converts a stored value to shown units.</summary>
    public double ToShown(double stored) => stored / Scale;

    private void OnFieldChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ParamField.Value) or nameof(ParamField.PendingValue))
            Sync();
    }

    private void Sync()
    {
        var stored = Field.PendingValue ?? Field.Value;
        _syncing = true;
        try
        {
            if (stored is not { } s)
            {
                Value = null;
                return;
            }

            var shown = Math.Round(ToShown(s), 4);
            // never clamp the vehicle's value (the editor would write the clamped one back)
            Minimum = (decimal)Math.Min(RangeMin, shown);
            Maximum = (decimal)Math.Max(RangeMax, shown);
            Value = (decimal)shown;
        }
        finally
        {
            _syncing = false;
        }
    }
}
