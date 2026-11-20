using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using MissionPlanner.App.Core.Localization;
using MissionPlanner.App.Core.Synthetic;
using MissionPlanner.App.Core.Video;

namespace MissionPlanner.App.Controls.Synthetic;

/// <summary>
/// The map's 3D panel: the vehicle model over terrain, seen from behind and above (third person).
/// The horizon stays level and the camera follows the vehicle; drag to orbit, wheel to zoom,
/// double-click to go back behind it. Shows frames of a chase <see cref="SyntheticView"/>, which
/// exists only while this control is on screen.
/// </summary>
public sealed class ChaseView3D : Control
{
    public static readonly StyledProperty<SyntheticScene?> SceneProperty =
        AvaloniaProperty.Register<ChaseView3D, SyntheticScene?>(nameof(Scene));

    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#0b0d10"));
    private static readonly IBrush HintBg = new SolidColorBrush(Color.FromArgb(150, 10, 14, 18));
    private static readonly Typeface Face = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    private readonly FrameReader _reader;
    private SyntheticView? _view;
    private WriteableBitmap? _bitmap;
    private long _sequence;
    private int _pending;
    private Point? _drag;
    private bool _onScreen;

    public ChaseView3D()
    {
        _reader = CopyFrame;
        ClipToBounds = true;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.SizeAll);
    }

    public SyntheticScene? Scene
    {
        get => GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _onScreen = true;
        Sync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _onScreen = false;
        Sync();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SceneProperty || change.Property == IsVisibleProperty || change.Property == BoundsProperty)
            Sync();
    }

    /// <summary>A chase view while visible, at this control's pixel size; none otherwise.</summary>
    private void Sync()
    {
        var want = _onScreen && IsEffectivelyVisible && Scene != null && Bounds.Width > 16 && Bounds.Height > 16;
        if (!want)
        {
            if (_view != null)
            {
                _view.FrameArrived -= OnFrame;
                _view.Dispose();
                _view = null;
            }

            return;
        }

        if (_view == null)
        {
            _view = Scene!.Acquire(SyntheticViewKind.Chase);
            _view.FrameArrived += OnFrame;
            _sequence = 0;
        }

        var px = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        _view.Width = (int)Math.Round(Bounds.Width * px);
        _view.Height = (int)Math.Round(Bounds.Height * px);
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _pending, 1) == 0)
            Dispatcher.UIThread.Post(RequestFrame, DispatcherPriority.Render);
    }

    /// <summary>Shows the newest frame on the display's next refresh (vsync), not whenever it arrives.</summary>
    private void RequestFrame()
    {
        if (TopLevel.GetTopLevel(this) is { } top)
            top.RequestAnimationFrame(_ => ShowFrame());
        else
            Volatile.Write(ref _pending, 0);
    }

    private void ShowFrame()
    {
        Volatile.Write(ref _pending, 0);
        if (_view != null && _view.Frames.TryRead(ref _sequence, _reader))
            InvalidateVisual();
    }

    private void CopyFrame(byte[] pixels, int width, int height, int stride)
    {
        if (_bitmap == null || _bitmap.PixelSize.Width != width || _bitmap.PixelSize.Height != height)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        }

        using var fb = _bitmap.Lock();
        var row = Math.Min(stride, fb.RowBytes);
        for (var y = 0; y < height; y++)
            Marshal.Copy(pixels, y * stride, fb.Address + y * fb.RowBytes, row);
    }

    public override void Render(DrawingContext ctx)
    {
        var r = new Rect(Bounds.Size);
        ctx.FillRectangle(Background, r);
        if (_bitmap != null)
            ctx.DrawImage(_bitmap, new Rect(_bitmap.Size), r);

        string? message = Scene?.Error ?? (_bitmap == null ? L.Tr("Starting the 3D view…") : null);
        if (message != null)
            DrawLabel(ctx, message, new Point(14, 14), 13);
        DrawLabel(ctx, L.Tr("Drag to orbit · wheel to zoom · double-click: follow behind the vehicle"), new Point(10, Bounds.Height - 30), 11);
    }

    private static void DrawLabel(DrawingContext ctx, string text, Point at, double size)
    {
        var t = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, size, Brushes.White);
        ctx.FillRectangle(HintBg, new Rect(at.X - 6, at.Y - 3, t.Width + 12, t.Height + 6), 4);
        ctx.DrawText(t, at);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_view == null)
            return;
        if (e.ClickCount == 2)
        {
            _view.ResetOrbit();
            e.Handled = true;
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _drag = e.GetPosition(this);
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag is not { } from || _view == null)
            return;
        var at = e.GetPosition(this);
        var d = at - from;
        _drag = at;
        // dragging takes the camera off "follow": it starts from where it is now
        if (double.IsNaN(_view.OrbitHeading))
        {
            if (double.IsNaN(_view.ShownHeading))
                return;
            _view.OrbitHeading = _view.ShownHeading;
        }

        _view.OrbitHeading = PoseTracker.Norm360(_view.OrbitHeading - d.X * 0.4);
        _view.OrbitElevation = Math.Clamp(_view.OrbitElevation + d.Y * 0.3, 2, 85);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _drag = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_view == null)
            return;
        _view.Distance = Math.Clamp(_view.Distance * Math.Pow(0.85, e.Delta.Y), 3, 600);
        e.Handled = true;
    }
}
