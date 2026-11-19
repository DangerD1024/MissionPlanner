using Avalonia.Controls;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;

namespace MissionPlanner.App.Views.SetupScreens.Hardware;

/// <summary>A labelled editor for a <see cref="ScaledField"/> (set it as DataContext); hidden without one.</summary>
public sealed partial class ScaledRow : UserControl
{
    public ScaledRow()
    {
        InitializeComponent();
        // a binding that yields null from the start raises no DataContextChanged: start hidden
        IsVisible = false;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        IsVisible = DataContext is ScaledField;
    }
}
