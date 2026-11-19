using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MissionPlanner.App.Controls.ConfigScreens;
using MissionPlanner.App.Core.ViewModels.ConfigScreens;

namespace MissionPlanner.App.Views.ConfigScreens;

public sealed partial class FullParameterTreeView : UserControl
{
    private FullParameterTreeViewModel? _vm;

    public FullParameterTreeView()
    {
        InitializeComponent();

        ParamGridEditing.Attach(ParamsGrid, () => _vm, item => (item as ParamTreeItem)?.Row);

        // Right / Left open and close the selected group, Space toggles it (before the grid moves its cell)
        ParamsGrid.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (_vm == null || ParamsGrid.SelectedItem is not ParamTreeItem { IsGroup: true } group || e.KeyModifiers != KeyModifiers.None)
                return;
            var open = e.Key switch
            {
                Key.Right => true,
                Key.Left => false,
                Key.Space => !group.IsExpanded,
                _ => (bool?)null,
            };
            if (open is not { } o)
                return;
            if (o != group.IsExpanded)
                _vm.Toggle(group);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);

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
        _vm = DataContext as FullParameterTreeViewModel;
        if (_vm != null)
            _vm.PropertyChanged += OnViewModelChanged;
        UpdateColumns();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FullParameterTreeViewModel.HasDefaults))
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
