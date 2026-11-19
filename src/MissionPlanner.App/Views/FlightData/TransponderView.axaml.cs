using Avalonia.Controls;
using Avalonia.Input;
using MissionPlanner.App.Core.ViewModels.FlightData;

namespace MissionPlanner.App.Views.FlightData;

public sealed partial class TransponderView : UserControl
{
    public TransponderView()
    {
        InitializeComponent();
        // the flight ID goes out when confirmed, not on every keystroke
        FlightIdBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                Apply();
        };
        FlightIdBox.LostFocus += (_, _) => Apply();
    }

    private void Apply() => (DataContext as TransponderViewModel)?.ApplyFlightIdCommand.Execute(null);
}
