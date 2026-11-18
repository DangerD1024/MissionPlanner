using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MissionPlanner.App.Core.ViewModels.ConfigScreens;

namespace MissionPlanner.App.Views.ConfigScreens;

public sealed partial class FullParameterListView : UserControl
{
    private FullParameterListViewModel? _vm;
    private bool _clickedValue;

    public FullParameterListView()
    {
        InitializeComponent();

        // the value editor starts from the shown value; the typed text is checked when the edit commits
        ParamsGrid.BeginningEdit += (_, e) =>
        {
            if (e.Row.DataContext is ParamRow row)
                row.EditText = row.ValueText;
        };
        ParamsGrid.PreparingCellForEdit += (_, e) =>
        {
            if (e.EditingElement is TextBox box)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    box.Focus();
                    box.SelectAll();
                });
            }
        };
        ParamsGrid.CellEditEnded += OnCellEditEnded;

        // one click on a value starts editing it (F2 and a second click work too); the edit starts
        // once the grid has moved its current cell on release
        ParamsGrid.CellPointerPressed += (_, e) =>
            _clickedValue = Equals(e.Column.Header, "Value") &&
                            e.PointerPressedEventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed;
        ParamsGrid.AddHandler(PointerReleasedEvent, (_, _) =>
        {
            if (!_clickedValue)
                return;
            _clickedValue = false;
            Dispatcher.UIThread.Post(() =>
            {
                if (Equals(ParamsGrid.CurrentColumn?.Header, "Value"))
                    ParamsGrid.BeginEdit();
            }, DispatcherPriority.Background);
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
            }
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null)
            _vm.PropertyChanged -= OnViewModelChanged;
        _vm = DataContext as FullParameterListViewModel;
        if (_vm != null)
            _vm.PropertyChanged += OnViewModelChanged;
        UpdateColumns();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FullParameterListViewModel.HasDefaults))
            UpdateColumns();
    }

    // DataGrid columns are not in the visual tree, so their visibility is set here
    private void UpdateColumns()
    {
        var defaults = ParamsGrid.Columns.FirstOrDefault(c => Equals(c.Header, "Default"));
        if (defaults != null)
            defaults.IsVisible = _vm?.HasDefaults == true;
    }

    private async void OnCellEditEnded(object? sender, DataGridCellEditEndedEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Row.DataContext is not ParamRow row || _vm == null)
            return;
        try
        {
            await _vm.CommitEditAsync(row, row.EditText);
        }
        catch (Exception ex)
        {
            _vm.StatusText = ex.Message;
        }
    }
}
