using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using MissionPlanner.App.Controls.ConfigScreens;
using MissionPlanner.App.Core.ViewModels.ConfigScreens;

namespace MissionPlanner.App.Views.ConfigScreens;

public sealed partial class FullParameterListView : UserControl
{
    private FullParameterListViewModel? _vm;

    public FullParameterListView()
    {
        InitializeComponent();

        ParamGridEditing.Attach(ParamsGrid, () => _vm, item => item as ParamRow);

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
}
