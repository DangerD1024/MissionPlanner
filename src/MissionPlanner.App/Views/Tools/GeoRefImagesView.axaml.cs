using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using GMap.NET;
using MissionPlanner.App.Controls.Logs;
using MissionPlanner.App.Core.Maps;
using MissionPlanner.App.Core.ViewModels.Tools;

namespace MissionPlanner.App.Views.Tools;

/// <summary>
/// Geo-referencing: the vehicle's path, where the camera fired and the located photos on the map
/// (Mission Planner's GeoRef window map); picking a photo marks it.
/// </summary>
public sealed partial class GeoRefImagesView : UserControl
{
    private readonly TrackLayer _track = new();
    private readonly DotLayer _shots = new(Color.Parse("#e5534b"), 3);
    private readonly DotLayer _photos = new(Color.Parse("#8cc63f"), 4);
    private GeoRefImagesViewModel? _vm;

    public GeoRefImagesView()
    {
        InitializeComponent();
        Map.Provider = MapTiles.CommonProviders.FirstOrDefault();
        Map.Layers.Add(_track);
        Map.Layers.Add(_shots);
        Map.Layers.Add(_photos);
        DataContextChanged += (_, _) => Attach(DataContext as GeoRefImagesViewModel);
        DetachedFromVisualTree += (_, _) => Attach(null);
    }

    private void Attach(GeoRefImagesViewModel? vm)
    {
        if (_vm == vm)
            return;
        if (_vm != null)
            _vm.PropertyChanged -= OnVmChanged;
        _vm = vm;
        if (vm == null)
            return;
        vm.PropertyChanged += OnVmChanged;
        Update(fit: true);
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(GeoRefImagesViewModel.Processed):
                Update(fit: true);
                break;
            case nameof(GeoRefImagesViewModel.SelectedPhoto):
                Update(fit: false);
                if (_vm?.SelectedPhoto is { } p)
                    Map.CenterOn(p.Lat, p.Lng);
                break;
        }
    }

    private void Update(bool fit)
    {
        var vm = _vm;
        _track.Points = vm?.Track ?? [];
        _shots.Points = vm?.Shots.Select(s => new PointLatLng(s.Lat, s.Lng)).ToList() ?? [];
        _photos.Points = vm?.Photos.Select(p => new PointLatLng(p.Lat, p.Lng)).ToList() ?? [];
        _photos.Selected = vm?.SelectedPhoto is { } sel ? new PointLatLng(sel.Lat, sel.Lng) : null;
        Map.InvalidateVisual();

        if (!fit)
            return;
        var all = _track.Points.Select(t => new PointLatLng(t.Lat, t.Lng)).Concat(_shots.Points).Concat(_photos.Points)
            .Where(p => p.Lat != 0 || p.Lng != 0)
            .ToList();
        if (all.Count > 0)
            Dispatcher.UIThread.Post(() => Map.ZoomToFit(all, maxZoom: 19), DispatcherPriority.Background);
    }
}
