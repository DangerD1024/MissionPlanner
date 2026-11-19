using Avalonia.Controls;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;

namespace MissionPlanner.App.Views.SetupScreens.Hardware;

/// <summary>A labelled parameter editor for setup cards (set a ParamField as DataContext); hidden without one.</summary>
public sealed partial class ParamRow : UserControl
{
    public ParamRow()
    {
        InitializeComponent();
        // a binding that yields null from the start raises no DataContextChanged: start hidden
        IsVisible = false;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        IsVisible = DataContext is ParamField;
    }
}
