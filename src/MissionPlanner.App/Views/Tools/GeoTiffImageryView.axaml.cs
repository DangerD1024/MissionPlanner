using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using MissionPlanner.App.Core.ViewModels.Tools;

namespace MissionPlanner.App.Views.Tools;

/// <summary>The GeoTIFF overlay page: the map shows the overlay with each image's outline; picking one zooms to it.</summary>
public sealed partial class GeoTiffImageryView : UserControl
{
    private readonly AreaLayer _outlines = new();
    private readonly DispatcherTimer _reload = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private GeoTiffImageryViewModel? _vm;

    public GeoTiffImageryView()
    {
        InitializeComponent();
        Map.Layers.Add(_outlines);
        _reload.Tick += (_, _) =>
        {
            _reload.Stop();
            Map.ReloadTiles();
        };
        DataContextChanged += (_, _) => Attach(DataContext as GeoTiffImageryViewModel);
        DetachedFromVisualTree += (_, _) =>
        {
            _reload.Stop();
            Attach(null);
        };
    }

    private void Attach(GeoTiffImageryViewModel? vm)
    {
        if (_vm == vm)
            return;
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnVmChanged;
            _vm.Images.CollectionChanged -= OnImagesChanged;
        }

        _vm = vm;
        if (vm == null)
            return;
        vm.PropertyChanged += OnVmChanged;
        vm.Images.CollectionChanged += OnImagesChanged;
        Update();
    }

    private void OnImagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Update();
        // the overlay's tiles changed
        Map.ReloadTiles();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GeoTiffImageryViewModel.SelectedImage))
        {
            Update();
            if (_vm?.SelectedImage is { } image)
                Dispatcher.UIThread.Post(() => Map.ZoomToFit(AreaLayer.Corners(image.Bounds), maxZoom: 20), DispatcherPriority.Background);
        }
        else if (e.PropertyName == nameof(GeoTiffImageryViewModel.Opacity))
        {
            // redraw once the slider settles
            _reload.Stop();
            _reload.Start();
        }
    }

    private void Update()
    {
        _outlines.Areas = _vm?.Images.Select(i => i.Bounds).ToList() ?? [];
        _outlines.Selected = _vm?.SelectedImage?.Bounds;
        Map.InvalidateVisual();
    }
}
