using Avalonia.Controls;
using Avalonia.Input;
using MissionPlanner.App.Core.ViewModels.SetupScreens;

namespace MissionPlanner.App.Views.SetupScreens;

public sealed partial class FlightModesView : UserControl
{
    public FlightModesView()
    {
        InitializeComponent();
    }

    // Ctrl+S saves, as in Mission Planner
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control) && DataContext is FlightModesViewModel vm &&
            vm.SaveCommand.CanExecute(null))
        {
            vm.SaveCommand.Execute(null);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
