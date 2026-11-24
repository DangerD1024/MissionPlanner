using Avalonia.Controls;
using MissionPlanner.App.Core.ViewModels.Tools;

namespace MissionPlanner.App.Views.Tools;

public sealed partial class ProximityView : UserControl
{
    public ProximityView()
    {
        InitializeComponent();
        // Mission Planner's keys: + / - change the radius, [ / ] the vehicle size; the wheel zooms too
        Radar.PointerWheelChanged += (_, e) =>
        {
            if (DataContext is not ProximityViewModel vm)
                return;
            if (e.Delta.Y > 0)
                vm.ZoomInCommand.Execute(null);
            else if (e.Delta.Y < 0)
                vm.ZoomOutCommand.Execute(null);
            e.Handled = true;
        };
        Radar.PointerPressed += (_, _) => Radar.Focus();
        Radar.TextInput += (_, e) =>
        {
            if (DataContext is not ProximityViewModel vm)
                return;
            switch (e.Text)
            {
                case "+":
                    vm.ZoomInCommand.Execute(null);
                    break;
                case "-":
                    vm.ZoomOutCommand.Execute(null);
                    break;
                case "]":
                    vm.LargerCommand.Execute(null);
                    break;
                case "[":
                    vm.SmallerCommand.Execute(null);
                    break;
                default:
                    return;
            }

            e.Handled = true;
        };
    }
}
