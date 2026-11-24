using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using MissionPlanner.App.Core.Synthetic;
using MissionPlanner.App.Core.Video;
using MissionPlanner.App.Core.ViewModels.FlightData;
using MissionPlanner.App.Core.ViewModels.Video;

namespace MissionPlanner.App.Controls.Video;

/// <summary>
/// Puts a picture behind a <see cref="Hud"/>: the camera video, or the 3D view (first person, sized
/// to this HUD so its horizon lines up with the pitch ladder). Copies the newest frame into one
/// reused <see cref="WriteableBitmap"/> at most once per rendered frame, mirrors the status line and
/// aspect, and gives the HUD's host its right-click menu. The video stream belongs to
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
    private SyntheticScene? _scene;
    private SyntheticView? _view;
    private IFrameSource? _source;
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
        hud.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.BoundsProperty || e.Property == Visual.IsVisibleProperty)
                UpdateSource();
        };
    }

    /// <summary>
    /// Adds video / 3D to the HUD in <paramref name="host"/>; the host's DataContext is the
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
        {
            UpdateSource();
            return;
        }

        if (_video != null)
            _video.PropertyChanged -= OnVideoChanged;
        if (_scene != null)
            _scene.StateChanged -= OnSceneChanged;

        _video = video;
        _scene = services != null && video != null ? SyntheticScene.For(services) : null;
        if (video == null)
        {
            // off screen: drop the bitmap and the 3D view, keep the video stream
            UseSource(null);
            _hud.VideoFrame = null;
            _hud.VideoStatus = null;
            _bitmap?.Dispose();
            _bitmap = null;
            return;
        }

        HookShutdown(video);
        video.PropertyChanged += OnVideoChanged;
        _scene!.StateChanged += OnSceneChanged;
        UpdateSource();
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
        if (e.PropertyName is nameof(HudVideoViewModel.State) or nameof(HudVideoViewModel.StatusText) or nameof(HudVideoViewModel.Aspect)
            or nameof(HudVideoViewModel.UsesSynthetic) or nameof(HudVideoViewModel.FpvFov) or nameof(HudVideoViewModel.TiltServo))
            UpdateSource();
        // a fixed tilt changes from the settings dialog; a servo-driven one is followed per frame (and may be
        // raised from the stream's thread), so only the fixed one redraws from here
        else if (e.PropertyName == nameof(HudVideoViewModel.CameraTilt) && sender is HudVideoViewModel { TiltServo: 0 })
            UpdateSource();
    }

    private void OnSceneChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(UpdateSource);

    /// <summary>Video or 3D, the 3D view's size, the status line and aspect.</summary>
    private void UpdateSource()
    {
        var video = _video;
        if (video == null)
            return;

        var synthetic = video.UsesSynthetic && _hud.IsVisible && _scene != null;
        if (synthetic)
        {
            _view ??= _scene!.Acquire(SyntheticViewKind.FirstPerson);
            SizeView(_view);
            _hud.Camera = HudGeometry.Camera(video, _view, _hud.Bounds.Width, _hud.Bounds.Height);
            UseSource(_view);
            _hud.VideoStatus = _scene!.Error is { } error ? "3D: " + error : null;
            // the 3D picture is rendered at this HUD's shape: fill it exactly
            _hud.VideoAspect = VideoAspect.Fill;
        }
        else
        {
            UseSource(video.Stream);
            _hud.Camera = null;
            _hud.VideoStatus = video.StatusText;
            _hud.VideoAspect = video.Aspect;
            if (!video.ShowsFrame)
                _hud.VideoFrame = null;
        }
    }

    /// <summary>Renders at the HUD's pixel size, with the horizon where the HUD draws it (8·scale below the middle).</summary>
    private void SizeView(SyntheticView view)
    {
        var w = _hud.Bounds.Width;
        var h = _hud.Bounds.Height;
        var px = TopLevel.GetTopLevel(_hud)?.RenderScaling ?? 1;
        view.Width = (int)Math.Round(w * px);
        view.Height = (int)Math.Round(h * px);
        view.CentreDownFraction = HudGeometry.HorizonDownFraction(w, h);
    }

    private void UseSource(IFrameSource? source)
    {
        if (ReferenceEquals(source, _source))
            return;
        if (_source != null)
            _source.FrameArrived -= OnFrameArrived;
        if (_view != null && !ReferenceEquals(source, _view))
        {
            _view.Dispose();
            _view = null;
        }

        _source = source;
        _sequence = 0;
        _hud.AttitudeOverride = null;
        Volatile.Write(ref _pending, 0);
        if (source == null)
            return;
        source.FrameArrived += OnFrameArrived;
        OnFrameArrived(null, EventArgs.Empty);
    }

    private bool ShowsFrame => _source is SyntheticView || _video?.ShowsFrame == true;

    /// <summary>Decoder / render thread: ask for one UI update, however many frames arrive before it runs.</summary>
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
        if (top == null || _source == null)
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
        var source = _source;
        if (source == null || !ShowsFrame)
            return;
        if (!source.Frames.TryRead(ref _sequence, _reader) || _bitmap == null)
            return;
        _hud.AttitudeOverride = (source as SyntheticView)?.FrameAttitude;
        // a servo-driven camera tilt moves with the vehicle's servo output: follow it every frame
        if (source is SyntheticView view && _video is { TiltServo: > 0 } video)
            _hud.Camera = HudGeometry.Camera(video, view, _hud.Bounds.Width, _hud.Bounds.Height);

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

/// <summary>Where the HUD puts things, shared with the 3D view so their horizons agree.</summary>
public static class HudGeometry
{
    /// <summary>The HUD's scale for its size (as in <see cref="Hud.Render"/>).</summary>
    public static double Scale(double w, double h) => Math.Clamp(Math.Min(w / 480, h / 360), 0.6, 2.2);

    /// <summary>The HUD's level horizon sits 8·scale below the middle: that as a fraction of the height.</summary>
    public static float HorizonDownFraction(double w, double h) => h <= 0 ? 0 : (float)(8 * Scale(w, h) / h);

    /// <summary>
    /// Points a first-person 3D view's camera as the settings say and returns the camera model the HUD
    /// must draw its ladder with (null when it is the HUD's own: default field of view, no tilt).
    /// </summary>
    public static (double VerticalFov, double Tilt)? Camera(HudVideoViewModel video, SyntheticView view, double w, double h)
    {
        video.UpdateServoTilt();
        var tilt = video.CameraTilt;
        view.HorizontalFov = video.FpvFov;
        view.CameraTilt = tilt;
        if (video.FpvFov == null && tilt == 0)
            return null;
        var vfov = video.FpvFov is { } hf && h > 0
            ? SceneCamera.VerticalFromHorizontal(hf, w / h)
            : SceneCamera.HudVerticalFov;
        return (vfov * 180 / Math.PI, tilt);
    }
}
