using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using MissionPlanner.App.Core.Video;
using MissionPlanner.App.Core.ViewModels.FlightData;
using MissionPlanner.App.Core.ViewModels.Video;

namespace MissionPlanner.App.Controls.Video;

/// <summary>
/// Puts the video behind a <see cref="Hud"/>: copies the newest decoded frame into one reused
/// <see cref="WriteableBitmap"/> at most once per rendered frame, mirrors the status line and
/// aspect, and gives the HUD's host its right-click Video menu. The stream itself belongs to
/// <see cref="HudVideoViewModel"/> (one per app) and keeps running while views come and go.
/// </summary>
public sealed class HudVideoPresenter
{
    private static bool _shutdownHooked;

    private readonly Control _host;
    private readonly Hud _hud;
    private readonly FrameReader _reader;
    private readonly Action<TimeSpan> _onAnimationFrame;
    private readonly Action _requestFrame;
    private HudVideoViewModel? _video;
    private bool _attached;
    private WriteableBitmap? _bitmap;
    private long _sequence;
    private int _pending;
    private long _pendingSince;

    private HudVideoPresenter(Control host, Hud hud, Action<ItemCollection>? extraMenu)
    {
        _host = host;
        _hud = hud;
        _reader = CopyFrame;
        _onAnimationFrame = OnAnimationFrame;
        _requestFrame = RequestFrame;
        host.ContextMenu = HudVideoMenu.Create(() => _video, extraMenu);

        host.DataContextChanged += (_, _) => Bind();
        hud.AttachedToVisualTree += (_, _) =>
        {
            _attached = true;
            Bind();
        };
        hud.DetachedFromVisualTree += (_, _) =>
        {
            _attached = false;
            Bind();
        };
    }

    /// <summary>
    /// Adds video to the HUD in <paramref name="host"/>; the host's DataContext is the
    /// <see cref="FlightDataViewModel"/> (it supplies the app's services).
    /// </summary>
    /// <param name="extraMenu">appends the host's own entries to the right-click menu</param>
    public static HudVideoPresenter Install(Control host, Hud hud, Action<ItemCollection>? extraMenu = null) =>
        new(host, hud, extraMenu);

    private void Bind()
    {
        var services = (_host.DataContext as FlightDataViewModel)?.Services;
        var video = _attached && services != null ? HudVideoViewModel.For(services) : null;
        if (video == _video)
            return;

        if (_video != null)
        {
            _video.PropertyChanged -= OnVideoChanged;
            _video.Stream.FrameArrived -= OnFrameArrived;
        }

        _video = video;
        if (video == null)
        {
            // off screen: drop the bitmap, keep the stream
            _hud.VideoFrame = null;
            _hud.VideoStatus = null;
            _bitmap?.Dispose();
            _bitmap = null;
            return;
        }

        HookShutdown(video);
        video.PropertyChanged += OnVideoChanged;
        video.Stream.FrameArrived += OnFrameArrived;
        _sequence = 0;
        SyncFromVideo();
        Volatile.Write(ref _pending, 0);
        if (video.ShowsFrame)
            OnFrameArrived(null, EventArgs.Empty);
    }

    private static void HookShutdown(HudVideoViewModel video)
    {
        if (_shutdownHooked || Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;
        _shutdownHooked = true;
        // stop cleanly (RTSP teardown, sockets closed) before the process goes
        desktop.ShutdownRequested += (_, _) => video.Stop();
        desktop.Exit += (_, _) => video.Stop();
    }

    private void OnVideoChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HudVideoViewModel.State) or nameof(HudVideoViewModel.StatusText) or nameof(HudVideoViewModel.Aspect))
            SyncFromVideo();
    }

    private void SyncFromVideo()
    {
        if (_video == null)
            return;
        _hud.VideoStatus = _video.StatusText;
        _hud.VideoAspect = _video.Aspect;
        if (!_video.ShowsFrame)
            _hud.VideoFrame = null;
    }

    /// <summary>Decoder thread: ask for one UI update, however many frames arrive before it runs.</summary>
    private void OnFrameArrived(object? sender, EventArgs e)
    {
        var now = Environment.TickCount64;
        // a request that never ran (window hidden mid-way) must not block video for good
        if (Interlocked.Exchange(ref _pending, 1) == 0 || now - Interlocked.Read(ref _pendingSince) > 1000)
        {
            Interlocked.Exchange(ref _pendingSince, now);
            Dispatcher.UIThread.Post(_requestFrame, DispatcherPriority.Render);
        }
    }

    private void RequestFrame()
    {
        var top = TopLevel.GetTopLevel(_hud);
        if (top == null || _video == null)
        {
            Volatile.Write(ref _pending, 0);
            return;
        }

        top.RequestAnimationFrame(_onAnimationFrame);
    }

    /// <summary>UI thread, just before the next render: show the newest frame.</summary>
    private void OnAnimationFrame(TimeSpan _)
    {
        Volatile.Write(ref _pending, 0);
        var video = _video;
        if (video == null || !video.ShowsFrame)
            return;
        if (!video.Stream.Frames.TryRead(ref _sequence, _reader) || _bitmap == null)
            return;

        if (!ReferenceEquals(_hud.VideoFrame, _bitmap))
            _hud.VideoFrame = _bitmap;
        else
            _hud.InvalidateVisual();
    }

    /// <summary>Copies a frame into the reused bitmap (a new one only when the size changes).</summary>
    private void CopyFrame(byte[] pixels, int width, int height, int stride)
    {
        if (_bitmap == null || _bitmap.PixelSize.Width != width || _bitmap.PixelSize.Height != height)
        {
            var old = _bitmap;
            _bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            if (ReferenceEquals(_hud.VideoFrame, old))
                _hud.VideoFrame = null;
            old?.Dispose();
        }

        using var fb = _bitmap.Lock();
        if (fb.RowBytes == stride)
        {
            Marshal.Copy(pixels, 0, fb.Address, stride * height);
            return;
        }

        var row = Math.Min(stride, fb.RowBytes);
        for (var y = 0; y < height; y++)
            Marshal.Copy(pixels, y * stride, fb.Address + y * fb.RowBytes, row);
    }
}
