using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MissionPlanner.App.Core.ViewModels.Tools;

namespace MissionPlanner.App.Views.Tools;

/// <summary>The OSD video page: hands the view-model the HUD compositor and shows the preview frame.</summary>
public sealed partial class OsdVideoView : UserControl
{
    private OsdVideoViewModel? _vm;
    private WriteableBitmap? _bitmap;

    public OsdVideoView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as OsdVideoViewModel);
        DetachedFromVisualTree += (_, _) => Attach(null);
    }

    private void Attach(OsdVideoViewModel? vm)
    {
        if (_vm == vm)
            return;
        if (_vm != null)
            _vm.PropertyChanged -= OnVmChanged;
        _vm = vm;
        if (vm != null)
        {
            vm.Compositor ??= new OsdVideoCompositor();
            vm.PropertyChanged += OnVmChanged;
            ShowPreview();
        }
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OsdVideoViewModel.PreviewFrame))
            ShowPreview();
    }

    private void ShowPreview()
    {
        if (_vm?.PreviewFrame is not { } f)
        {
            Preview.Source = null;
            return;
        }

        if (_bitmap == null || _bitmap.PixelSize.Width != f.Width || _bitmap.PixelSize.Height != f.Height)
            _bitmap = new WriteableBitmap(new PixelSize(f.Width, f.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var fb = _bitmap.Lock())
        {
            var row = f.Width * 4;
            for (var y = 0; y < f.Height; y++)
                Marshal.Copy(f.Pixels, y * row, fb.Address + y * fb.RowBytes, row);
        }

        Preview.Source = null;
        Preview.Source = _bitmap;
    }
}
