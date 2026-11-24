using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using MissionPlanner.App.Controls;
using MissionPlanner.App.Core.ViewModels.FlightData;
using MissionPlanner.App.Core.ViewModels.Tools;
using MissionPlanner.App.Core.ViewModels.Video;

namespace MissionPlanner.App.Views.Tools;

/// <summary>
/// Draws the app's HUD over a video frame off screen, as the HUD stream does (HudStreamPump): the
/// frame goes in as the HUD's video, the HUD is rendered at the frame's size and read back.
/// </summary>
public sealed class OsdVideoCompositor : IHudCompositor
{
    private readonly Hud _hud = new() { VideoAspect = VideoAspect.Fill, ShowOverlay = true };
    private RenderTargetBitmap? _target;
    private WriteableBitmap? _frame;

    public Task ComposeAsync(byte[] bgra, int width, int height, HudModel hud, CancellationToken cancel) =>
        Dispatcher.UIThread.InvokeAsync(() => Compose(bgra, width, height, hud), DispatcherPriority.Background, cancel).GetTask();

    private void Compose(byte[] bgra, int width, int height, HudModel hud)
    {
        if (_target == null || _frame == null || _target.PixelSize.Width != width || _target.PixelSize.Height != height)
        {
            _target?.Dispose();
            _frame?.Dispose();
            _target = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
            _frame = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        }

        var row = width * 4;
        using (var fb = _frame.Lock())
        {
            for (var y = 0; y < height; y++)
                Marshal.Copy(bgra, y * row, fb.Address + y * fb.RowBytes, row);
        }

        // set again each frame: the bitmap is the same object with new pixels
        _hud.VideoFrame = null;
        _hud.VideoFrame = _frame;
        _hud.Model = hud;
        _hud.Measure(new Size(width, height));
        _hud.Arrange(new Rect(0, 0, width, height));
        _target.Render(_hud);

        var pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            _target.CopyPixels(new PixelRect(0, 0, width, height), pin.AddrOfPinnedObject(), bgra.Length, row);
        }
        finally
        {
            pin.Free();
        }

        if (_target.Format is { } format && format == PixelFormat.Rgba8888)
        {
            for (var i = 0; i < bgra.Length; i += 4)
                (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
        }
    }
}
