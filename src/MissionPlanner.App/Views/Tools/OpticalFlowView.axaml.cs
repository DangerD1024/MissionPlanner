using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MissionPlanner.App.Core.ViewModels.Tools;

namespace MissionPlanner.App.Views.Tools;

/// <summary>Draws the sensor's grayscale picture, pixel for pixel (no smoothing, to judge focus).</summary>
public sealed partial class OpticalFlowView : UserControl
{
    private OpticalFlowViewModel? _vm;
    private WriteableBitmap? _bitmap;

    public OpticalFlowView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as OpticalFlowViewModel);
        DetachedFromVisualTree += (_, _) => Attach(null);
    }

    private void Attach(OpticalFlowViewModel? vm)
    {
        if (_vm == vm)
            return;
        if (_vm != null)
            _vm.ImageChanged -= OnImage;
        _vm = vm;
        if (vm != null)
        {
            vm.ImageChanged += OnImage;
            OnImage(vm, EventArgs.Empty);
        }
    }

    private void OnImage(object? sender, EventArgs e)
    {
        if (_vm?.Image is not { } image)
            return;
        if (_bitmap == null || _bitmap.PixelSize.Width != image.Width || _bitmap.PixelSize.Height != image.Height)
            _bitmap = new WriteableBitmap(new PixelSize(image.Width, image.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        var bgra = image.ToBgra();
        using (var fb = _bitmap.Lock())
        {
            var row = image.Width * 4;
            for (var y = 0; y < image.Height; y++)
                Marshal.Copy(bgra, y * row, fb.Address + y * fb.RowBytes, row);
        }

        Picture.Source = null;
        Picture.Source = _bitmap;
    }
}
