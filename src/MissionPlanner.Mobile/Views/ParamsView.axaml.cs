using Avalonia.Controls;
using Avalonia.Input;
using MissionPlanner.App.Core.ViewModels.Mobile;

namespace MissionPlanner.Mobile.Views;

public sealed partial class ParamsView : UserControl
{
    public ParamsView() => InitializeComponent();

    private void OnScrim(object? sender, PointerPressedEventArgs e) =>
        (DataContext as MobileParamsViewModel)?.CancelEditCommand.Execute(null);
}
