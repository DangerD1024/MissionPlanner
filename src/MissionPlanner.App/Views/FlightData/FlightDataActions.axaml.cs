using Avalonia.Controls;
using MissionPlanner.App.Core.ViewModels.FlightData;

namespace MissionPlanner.App.Views.FlightData;

public sealed partial class FlightDataActions : UserControl
{
    public FlightDataActions()
    {
        InitializeComponent();

        // Mission Planner re-reads the mode table when the list is opened (metadata may have arrived since)
        ModeCombo.DropDownOpened += (_, _) => (DataContext as FlightDataViewModel)?.ReloadModesCommand.Execute(null);
    }
}
