using Avalonia.Controls;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;

namespace MissionPlanner.Mobile.Views.Setup;

/// <summary>Editor for one <see cref="ParamField"/> (its DataContext); shows nothing without one.</summary>
public sealed partial class ParamFieldRow : UserControl
{
    /// <summary>False where the page already names the parameter (a flight mode slot).</summary>
    public static readonly Avalonia.StyledProperty<bool> ShowLabelProperty =
        Avalonia.AvaloniaProperty.Register<ParamFieldRow, bool>(nameof(ShowLabel), true);

    public ParamFieldRow()
    {
        InitializeComponent();
    }

    protected override void OnPropertyChanged(Avalonia.AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ShowLabelProperty)
            LabelRow.IsVisible = ShowLabel;
    }

    public bool ShowLabel
    {
        get => GetValue(ShowLabelProperty);
        set => SetValue(ShowLabelProperty, value);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Root.IsVisible = DataContext is ParamField;
    }
}
