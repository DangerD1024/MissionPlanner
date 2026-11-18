using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using MissionPlanner.App.Controls.Simulation;
using MissionPlanner.App.Core.ViewModels.Simulation;

namespace MissionPlanner.App.Views.Simulation;

public sealed partial class SimulationView : UserControl
{
    private SimulationViewModel? _vm;
    private SitlHomeLayer? _layer;

    public SimulationView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Bind(DataContext as SimulationViewModel);

        Map.MapClick += (_, e) =>
        {
            if (e.Hit == null)
                _vm?.MapClicked(e.Position.Lat, e.Position.Lng);
        };
        Map.ItemDragged += (_, e) =>
        {
            if (ReferenceEquals(e.Item, SitlHomeLayer.HomeToken))
                _vm?.MoveHome(e.Position.Lat, e.Position.Lng);
        };
        Map.ItemDragCompleted += (_, e) =>
        {
            if (ReferenceEquals(e.Item, SitlHomeLayer.HomeToken))
                _vm?.MapClicked(e.Position.Lat, e.Position.Lng);
        };
    }

    private void Bind(SimulationViewModel? vm)
    {
        if (_vm != null)
        {
            _vm.HomeChanged -= OnHomeChanged;
            _vm.ConsoleLines.CollectionChanged -= OnConsoleChanged;
            if (_layer != null)
                Map.Layers.Remove(_layer);
            _layer = null;
        }

        _vm = vm;
        if (vm == null)
            return;

        _layer = new SitlHomeLayer(vm);
        Map.Layers.Add(_layer);
        vm.HomeChanged += OnHomeChanged;
        vm.ConsoleLines.CollectionChanged += OnConsoleChanged;
    }

    private void OnHomeChanged(object? sender, EventArgs e) => Map.InvalidateVisual();

    // follow the simulator's output as it arrives
    private void OnConsoleChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_vm is { ConsoleLines.Count: > 0 } vm)
                ConsoleList.ScrollIntoView(vm.ConsoleLines.Count - 1);
        }, DispatcherPriority.Background);
    }
}
