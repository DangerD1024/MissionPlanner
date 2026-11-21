using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
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
/// Feeds the stream: while <see cref="StreamingViewModel.IsStreaming"/>, renders a HUD of its own
/// (not on screen) at the stream size and frame rate — the newest video or 3D frame behind it, the
/// OSD drawn or not as chosen — and pushes the pixels to the encoder. Works whichever screen is
/// shown and whether the HUD is attached, detached or hidden.
/// </summary>
public sealed class HudStreamPump
{
    private const string BackgroundUser = "stream";
    private static HudStreamPump? _instance;

    private readonly FlightDataViewModel _flight;
    private readonly StreamingViewModel _streaming;
    private readonly HudVideoViewModel _video;
    private readonly Hud _hud;
    private readonly FrameReader _reader;
    private DispatcherTimer? _timer;
    private RenderTargetBitmap? _target;
    private WriteableBitmap? _frame;
    private SyntheticView? _view;
    private byte[] _pixels = [];
    private long _sequence;

    private HudStreamPump(FlightDataViewModel flight)
    {
        _flight = flight;
        _streaming = StreamingViewModel.For(flight.Services);
        _video = HudVideoViewModel.For(flight.Services);
        _hud = new Hud { Model = flight.Hud };
        _reader = CopyFrame;
        _streaming.PropertyChanged += OnStreamingChanged;
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownRequested += (_, _) =>
            {
                _streaming.StopRecording();
                _streaming.Stop();
            };
            desktop.Exit += (_, _) => _streaming.Stop();
        }
    }

    /// <summary>Starts following streaming for the app (once).</summary>
    public static void Track(FlightDataViewModel flight) => _instance ??= new HudStreamPump(flight);

    private void OnStreamingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(StreamingViewModel.IsProducing))
            return;
        if (_streaming.IsProducing && _streaming.Active is { } s)
        {
            _flight.UseInBackground(BackgroundUser, true);
            _timer?.Stop();
            _timer = new DispatcherTimer(TimeSpan.FromSeconds(1.0 / s.Fps), DispatcherPriority.Render, (_, _) => Pump());
            _timer.Start();
        }
        else
        {
            _timer?.Stop();
            _timer = null;
            _flight.UseInBackground(BackgroundUser, false);
            _target?.Dispose();
            _target = null;
            _view?.Dispose();
            _view = null;
        }
    }

    private void Pump()
    {
        if (_streaming.Active is not { } s)
            return;
        var w = s.Width;
        var h = s.Height;
        if (_target == null || _target.PixelSize.Width != w || _target.PixelSize.Height != h)
        {
            _target?.Dispose();
            _target = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
            _pixels = new byte[w * h * 4];
        }

        // the newest video frame, or the 3D view rendered at the stream's own size
        IFrameSource? source = null;
        if (_video.UsesSynthetic)
        {
            if (_view == null)
            {
                _view = _flight.Scene.Acquire(SyntheticViewKind.FirstPerson);
                _sequence = 0;
            }

            _view.Width = w;
            _view.Height = h;
            _view.CentreDownFraction = HudGeometry.HorizonDownFraction(w, h);
            _hud.Camera = HudGeometry.Camera(_video, _view, w, h);
            source = _view;
            _hud.VideoAspect = VideoAspect.Fill;
        }
        else
        {
            if (_view != null)
            {
                _view.Dispose();
                _view = null;
                _sequence = 0;
            }

            _hud.Camera = null;
            if (_video.ShowsFrame)
                source = _video.Stream;
            _hud.VideoAspect = _video.Aspect;
        }

        if (source != null)
        {
            source.Frames.TryRead(ref _sequence, _reader);
            _hud.VideoFrame = _frame;
            _hud.AttitudeOverride = (source as SyntheticView)?.FrameAttitude;
        }
        else
        {
            _hud.VideoFrame = null;
            _hud.AttitudeOverride = null;
        }

        _hud.ShowOverlay = s.ShowOsd;
        _hud.Measure(new Size(w, h));
        _hud.Arrange(new Rect(0, 0, w, h));
        _target.Render(_hud);

        var pin = GCHandle.Alloc(_pixels, GCHandleType.Pinned);
        try
        {
            _target.CopyPixels(new PixelRect(0, 0, w, h), pin.AddrOfPinnedObject(), _pixels.Length, w * 4);
        }
        finally
        {
            pin.Free();
        }

        if (_target.Format is { } format && format == PixelFormat.Rgba8888)
            SwapRedBlue(_pixels);
        _streaming.Push(_pixels);
    }

    private static void SwapRedBlue(byte[] p)
    {
        for (var i = 0; i < p.Length; i += 4)
            (p[i], p[i + 2]) = (p[i + 2], p[i]);
    }

    private void CopyFrame(byte[] pixels, int width, int height, int stride)
    {
        if (_frame == null || _frame.PixelSize.Width != width || _frame.PixelSize.Height != height)
        {
            _frame?.Dispose();
            _frame = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        }

        using var fb = _frame.Lock();
        var row = Math.Min(stride, fb.RowBytes);
        for (var y = 0; y < height; y++)
            Marshal.Copy(pixels, y * stride, fb.Address + y * fb.RowBytes, row);
    }
}
