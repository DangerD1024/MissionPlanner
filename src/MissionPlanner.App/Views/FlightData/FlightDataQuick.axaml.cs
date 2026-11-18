using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MissionPlanner.App.Core.ViewModels.FlightData;

namespace MissionPlanner.App.Views.FlightData;

public sealed partial class FlightDataQuick : UserControl
{
    private FlightDataViewModel? _vm;

    public FlightDataQuick()
    {
        InitializeComponent();

        // a click (or Enter) picks; arrow keys only move the selection
        QuickList.Tapped += (_, _) => Pick();
        QuickList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Pick();
                e.Handled = true;
            }
        };
        QuickSearch.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Down when QuickList.ItemCount > 0:
                    QuickList.SelectedIndex = 0;
                    QuickList.Focus();
                    e.Handled = true;
                    break;
                case Key.Enter when _vm?.QuickFieldOptions.Count == 1:
                    _vm.PickQuickFieldCommand.Execute(_vm.QuickFieldOptions[0]);
                    e.Handled = true;
                    break;
                case Key.Escape:
                    _vm?.CloseQuickPickerCommand.Execute(null);
                    e.Handled = true;
                    break;
            }
        };

        DataContextChanged += (_, _) =>
        {
            if (_vm != null)
                _vm.PropertyChanged -= OnVmChanged;
            _vm = DataContext as FlightDataViewModel;
            if (_vm != null)
                _vm.PropertyChanged += OnVmChanged;
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FlightDataViewModel.IsQuickPickerOpen) && _vm?.IsQuickPickerOpen == true)
            Dispatcher.UIThread.Post(() => QuickSearch.Focus(), DispatcherPriority.Background);
    }

    private void Pick()
    {
        if (_vm != null && QuickList.SelectedItem is QuickField field)
            _vm.PickQuickFieldCommand.Execute(field);
    }
}
