using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using MissionPlanner.App.Core.Maps;
using MissionPlanner.App.Core.ViewModels.Tools;

namespace MissionPlanner.App.Views.Tools;

/// <summary>The DEM list beside a map of the files' coverage; clicking a line zooms to it.</summary>
public sealed partial class DemInfoView : UserControl
{
    private readonly AreaLayer _areas = new();
    private DemInfoViewModel? _vm;

    public DemInfoView()
    {
        InitializeComponent();
        Map.Provider = MapTiles.CommonProviders.FirstOrDefault();
        Map.Layers.Add(_areas);
        DataContextChanged += (_, _) => Attach(DataContext as DemInfoViewModel);
        DetachedFromVisualTree += (_, _) => Attach(null);
    }

    private void Attach(DemInfoViewModel? vm)
    {
        if (_vm == vm)
            return;
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnVmChanged;
            _vm.Entries.CollectionChanged -= OnEntriesChanged;
        }

        _vm = vm;
        if (vm == null)
            return;
        vm.PropertyChanged += OnVmChanged;
        vm.Entries.CollectionChanged += OnEntriesChanged;
        Update();
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Update();
        if (_vm is { Entries.Count: > 0 } vm && vm.Selected == null)
            FitAll(vm);
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DemInfoViewModel.Selected))
            return;
        Update();
        if (_vm?.Selected is { } s)
            Dispatcher.UIThread.Post(() => Map.ZoomToFit(AreaLayer.Corners(s.Area), maxZoom: 18), DispatcherPriority.Background);
    }

    private void FitAll(DemInfoViewModel vm)
    {
        var corners = vm.Entries.SelectMany(e => AreaLayer.Corners(e.Area)).ToList();
        Dispatcher.UIThread.Post(() => Map.ZoomToFit(corners, maxZoom: 18), DispatcherPriority.Background);
    }

    private void Update()
    {
        _areas.Areas = _vm?.Entries.Select(e => e.Area).ToList() ?? [];
        _areas.Selected = _vm?.Selected?.Area;
        Map.InvalidateVisual();
    }
}
