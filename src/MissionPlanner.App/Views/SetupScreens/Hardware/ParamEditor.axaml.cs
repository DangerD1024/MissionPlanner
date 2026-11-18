using Avalonia;
using Avalonia.Controls;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;

namespace MissionPlanner.App.Views.SetupScreens.Hardware;

/// <summary>Editor for one <see cref="ParamField"/> (set it as DataContext); shows nothing without one.</summary>
public sealed partial class ParamEditor : UserControl
{
    /// <summary>Numbers without spin buttons, for tables (arrow keys and the wheel still step).</summary>
    public static readonly StyledProperty<bool> CompactProperty =
        AvaloniaProperty.Register<ParamEditor, bool>(nameof(Compact));

    public ParamEditor()
    {
        InitializeComponent();
    }

    public bool Compact
    {
        get => GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CompactProperty)
            Number.ShowButtonSpinner = !Compact;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Root.IsVisible = DataContext is ParamField;
    }
}
