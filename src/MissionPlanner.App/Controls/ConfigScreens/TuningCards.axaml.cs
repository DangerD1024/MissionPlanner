using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MissionPlanner.App.Core.ViewModels.ConfigScreens;

namespace MissionPlanner.App.Controls.ConfigScreens;

/// <summary>
/// The cards of a tuning page with its Write Params / Refresh Screen bar. Tells the page which value
/// is being edited, for Mission Planner's "Change in 4.7" warning.
/// </summary>
public partial class TuningCards : UserControl
{
    public TuningCards()
    {
        InitializeComponent();
        AddHandler(GotFocusEvent, OnGotFocus, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (DataContext is TuningPageViewModel page)
            page.FocusedItem = (e.Source as StyledElement)?.DataContext as TuningParamItem;
    }
}
