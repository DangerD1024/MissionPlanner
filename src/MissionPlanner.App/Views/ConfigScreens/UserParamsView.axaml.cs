using Avalonia.Controls;
using Avalonia.Input;
using MissionPlanner.App.Controls.ConfigScreens;
using MissionPlanner.App.Core.ViewModels.ConfigScreens;

namespace MissionPlanner.App.Views.ConfigScreens;

public sealed partial class UserParamsView : UserControl
{
    public UserParamsView()
    {
        InitializeComponent();

        ParamGridEditing.Attach(ParamsGrid, () => DataContext as UserParamsViewModel, item => item as ParamRow);

        // double-click or Enter in the picker adds the parameter
        PickerList.DoubleTapped += (_, _) => AddPicked();
        PickerList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                AddPicked();
                e.Handled = true;
            }
        };
        PickerBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && PickerList.ItemCount > 0)
            {
                PickerList.SelectedIndex = Math.Max(0, PickerList.SelectedIndex);
                PickerList.ContainerFromIndex(PickerList.SelectedIndex)?.Focus();
                e.Handled = true;
            }
        };
    }

    private void AddPicked()
    {
        if (DataContext is UserParamsViewModel vm && PickerList.SelectedItem is ParamPickItem item)
            vm.AddCommand.Execute(item);
    }
}
