using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MissionPlanner.App.Core.ViewModels.SetupScreens.OptionalHardware;

namespace MissionPlanner.App.Controls.SetupScreens.OptionalHardware;

/// <summary>
/// The OSD layout editor: the screen's character grid with each switched-on element drawn at its
/// cell. Click an element to select it, drag it to move it (it snaps to the grid), or nudge the
/// selected one with the arrow keys. Element positions are written to the <see cref="OsdItem"/>s.
/// </summary>
public sealed class OsdCanvas : Control
{
    public static readonly StyledProperty<IReadOnlyList<OsdItem>?> ItemsProperty =
        AvaloniaProperty.Register<OsdCanvas, IReadOnlyList<OsdItem>?>(nameof(Items));

    public static readonly StyledProperty<OsdGrid> GridProperty =
        AvaloniaProperty.Register<OsdCanvas, OsdGrid>(nameof(Grid), OsdGrid.Sd);

    /// <summary>Changes whenever an element moves or is switched (the view-model's LayoutVersion).</summary>
    public static readonly StyledProperty<int> VersionProperty =
        AvaloniaProperty.Register<OsdCanvas, int>(nameof(Version));

    public static readonly StyledProperty<OsdItem?> SelectedItemProperty =
        AvaloniaProperty.Register<OsdCanvas, OsdItem?>(nameof(SelectedItem), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<bool> ShowNamesProperty =
        AvaloniaProperty.Register<OsdCanvas, bool>(nameof(ShowNames));

    public static readonly StyledProperty<IBrush?> ScreenBrushProperty =
        AvaloniaProperty.Register<OsdCanvas, IBrush?>(nameof(ScreenBrush));

    public static readonly StyledProperty<IBrush?> GridLineBrushProperty =
        AvaloniaProperty.Register<OsdCanvas, IBrush?>(nameof(GridLineBrush));

    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        AvaloniaProperty.Register<OsdCanvas, IBrush?>(nameof(TextBrush));

    public static readonly StyledProperty<IBrush?> MutedBrushProperty =
        AvaloniaProperty.Register<OsdCanvas, IBrush?>(nameof(MutedBrush));

    public static readonly StyledProperty<IBrush?> SelectionBrushProperty =
        AvaloniaProperty.Register<OsdCanvas, IBrush?>(nameof(SelectionBrush));

    public static readonly StyledProperty<IBrush?> ChangedBrushProperty =
        AvaloniaProperty.Register<OsdCanvas, IBrush?>(nameof(ChangedBrush));

    private static readonly FontFamily Mono = new("Cascadia Mono,Consolas,DejaVu Sans Mono,Liberation Mono,Menlo,monospace");

    private readonly Dictionary<char, FormattedText> _glyphs = new();
    private double _glyphCell;
    private IBrush? _glyphBrush;

    private OsdItem? _dragging;
    private int _grabDx;
    private int _grabDy;

    static OsdCanvas()
    {
        AffectsRender<OsdCanvas>(ItemsProperty, GridProperty, VersionProperty, SelectedItemProperty, ShowNamesProperty,
            ScreenBrushProperty, GridLineBrushProperty, TextBrushProperty, MutedBrushProperty, SelectionBrushProperty,
            ChangedBrushProperty);
        FocusableProperty.OverrideDefaultValue<OsdCanvas>(true);
    }

    public OsdCanvas()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Arrow);
        // taking focus for the arrow keys must not scroll the page under a drag
        AddHandler(RequestBringIntoViewEvent, (_, e) => e.Handled = true);
    }

    public IReadOnlyList<OsdItem>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public OsdGrid Grid
    {
        get => GetValue(GridProperty);
        set => SetValue(GridProperty, value);
    }

    public int Version
    {
        get => GetValue(VersionProperty);
        set => SetValue(VersionProperty, value);
    }

    public OsdItem? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public bool ShowNames
    {
        get => GetValue(ShowNamesProperty);
        set => SetValue(ShowNamesProperty, value);
    }

    public IBrush? ScreenBrush
    {
        get => GetValue(ScreenBrushProperty);
        set => SetValue(ScreenBrushProperty, value);
    }

    public IBrush? GridLineBrush
    {
        get => GetValue(GridLineBrushProperty);
        set => SetValue(GridLineBrushProperty, value);
    }

    public IBrush? TextBrush
    {
        get => GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    public IBrush? MutedBrush
    {
        get => GetValue(MutedBrushProperty);
        set => SetValue(MutedBrushProperty, value);
    }

    public IBrush? SelectionBrush
    {
        get => GetValue(SelectionBrushProperty);
        set => SetValue(SelectionBrushProperty, value);
    }

    public IBrush? ChangedBrush
    {
        get => GetValue(ChangedBrushProperty);
        set => SetValue(ChangedBrushProperty, value);
    }

    /// <summary>Cell size and top-left of the grid for the current bounds (cells are 2:3, like the OSD font).</summary>
    private (double Cw, double Ch, Point Origin) Layout()
    {
        var g = Grid;
        var b = Bounds;
        var cw = Math.Max(2, Math.Min(b.Width / g.Columns, b.Height / g.Rows / 1.5));
        var ch = cw * 1.5;
        var origin = new Point((b.Width - cw * g.Columns) / 2, (b.Height - ch * g.Rows) / 2);
        return (cw, ch, origin);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // as wide as offered, high enough for 2:3 cells
        var g = Grid;
        var w = double.IsInfinity(availableSize.Width) ? g.Columns * 16 : availableSize.Width;
        var h = w / g.Columns * 1.5 * g.Rows;
        if (!double.IsInfinity(availableSize.Height))
            h = Math.Min(h, availableSize.Height);
        return new Size(w, h);
    }

    public override void Render(DrawingContext ctx)
    {
        var g = Grid;
        var (cw, ch, o) = Layout();
        var screen = new Rect(o, new Size(cw * g.Columns, ch * g.Rows));

        ctx.FillRectangle(ScreenBrush ?? Brushes.Black, screen, 6);

        if (GridLineBrush is { } lineBrush)
        {
            var thin = new Pen(lineBrush, 1);
            for (var c = 1; c < g.Columns; c++)
            {
                var x = Math.Round(o.X + c * cw) + 0.5;
                ctx.DrawLine(thin, new Point(x, screen.Top), new Point(x, screen.Bottom));
            }

            for (var r = 1; r < g.Rows; r++)
            {
                var y = Math.Round(o.Y + r * ch) + 0.5;
                ctx.DrawLine(thin, new Point(screen.Left, y), new Point(screen.Right, y));
            }
        }

        // MAX7456: NTSC shows 13 of PAL's 16 rows
        if (g.NtscRows is { } ntsc && MutedBrush is { } muted)
        {
            var y = Math.Round(o.Y + ntsc * ch) + 0.5;
            var dashed = new Pen(muted, 1.5) { DashStyle = new DashStyle([4, 4], 0) };
            ctx.DrawLine(dashed, new Point(screen.Left, y), new Point(screen.Right, y));
            var size = Math.Max(9, ch * 0.32);
            ctx.DrawText(Text("NTSC", size, muted), new Point(screen.Left + 6, y - size * 1.4));
            ctx.DrawText(Text("PAL", size, muted), new Point(screen.Left + 6, y + 3));
        }

        var items = Items;
        if (items == null)
            return;

        var text = TextBrush ?? Brushes.White;
        EnsureGlyphs(cw, ch, text);
        var selected = SelectedItem;
        var selection = SelectionBrush ?? Brushes.Orange;
        var changed = ChangedBrush;

        using (ctx.PushClip(screen))
        {
            foreach (var item in items)
            {
                if (!item.Enabled || ReferenceEquals(item, selected))
                    continue;
                DrawItem(ctx, item, cw, ch, o, text, null, item.IsChanged ? changed : null);
            }

            if (selected is { Enabled: true })
                DrawItem(ctx, selected, cw, ch, o, text, selection, null);
        }
    }

    private void DrawItem(DrawingContext ctx, OsdItem item, double cw, double ch, Point o, IBrush text, IBrush? selection, IBrush? changed)
    {
        var caption = Caption(item);
        var rect = ItemRect(item, caption.Length, cw, ch, o);
        if (selection != null)
        {
            ctx.FillRectangle(new SolidColorBrush(ColorOf(selection), 0.28), rect, 3);
            ctx.DrawRectangle(new Pen(selection, 1.5), rect.Deflate(0.75), 3);
        }
        else
        {
            ctx.FillRectangle(new SolidColorBrush(ColorOf(text), 0.07), rect, 3);
            if (changed != null)
                ctx.DrawRectangle(new Pen(changed, 1) { DashStyle = new DashStyle([3, 2], 0) }, rect.Deflate(0.5), 3);
        }

        for (var i = 0; i < caption.Length; i++)
        {
            if (caption[i] == ' ')
                continue;
            if (!_glyphs.TryGetValue(caption[i], out var glyph))
            {
                glyph = Text(caption[i].ToString(), _glyphCell, text, mono: true);
                _glyphs[caption[i]] = glyph;
            }

            var cx = rect.X + i * cw + (cw - glyph.Width) / 2;
            var cy = rect.Y + (ch - glyph.Height) / 2;
            ctx.DrawText(glyph, new Point(cx, cy));
        }
    }

    private string Caption(OsdItem item) => OsdCaptions.For(item.Name, ShowNames);

    private static Rect ItemRect(OsdItem item, int length, double cw, double ch, Point o) =>
        new(o.X + item.X * cw, o.Y + item.Y * ch, Math.Max(1, length) * cw, ch);

    private void EnsureGlyphs(double cw, double ch, IBrush brush)
    {
        var size = Math.Min(ch * 0.78, cw * 1.55);
        if (Math.Abs(size - _glyphCell) < 0.01 && ReferenceEquals(brush, _glyphBrush))
            return;
        _glyphs.Clear();
        _glyphCell = size;
        _glyphBrush = brush;
    }

    private static FormattedText Text(string s, double size, IBrush brush, bool mono = false) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(mono ? Mono : FontFamily.Default, FontStyle.Normal, mono ? FontWeight.Bold : FontWeight.Normal),
            size, brush);

    private static Color ColorOf(IBrush b) => b is ISolidColorBrush s ? s.Color : Colors.Gray;

    /// <summary>The element under a point: the selected one first, then the last drawn.</summary>
    private OsdItem? HitTest(Point p)
    {
        var items = Items;
        if (items == null)
            return null;
        var (cw, ch, o) = Layout();
        if (SelectedItem is { Enabled: true } sel && ItemRect(sel, Caption(sel).Length, cw, ch, o).Contains(p))
            return sel;
        for (var i = items.Count - 1; i >= 0; i--)
        {
            var item = items[i];
            if (item.Enabled && ItemRect(item, Caption(item).Length, cw, ch, o).Contains(p))
                return item;
        }

        return null;
    }

    private (int X, int Y) Cell(Point p)
    {
        var (cw, ch, o) = Layout();
        return ((int)Math.Floor((p.X - o.X) / cw), (int)Math.Floor((p.Y - o.Y) / ch));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        Focus();
        var p = e.GetPosition(this);
        var hit = HitTest(p);
        SetCurrentValue(SelectedItemProperty, hit);
        if (hit == null)
            return;
        var (cx, cy) = Cell(p);
        _grabDx = cx - hit.X;
        _grabDy = cy - hit.Y;
        _dragging = hit;
        e.Pointer.Capture(this);
        Cursor = new Cursor(StandardCursorType.SizeAll);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_dragging is not { } item)
        {
            Cursor = new Cursor(HitTest(p) != null ? StandardCursorType.Hand : StandardCursorType.Arrow);
            return;
        }

        var (cx, cy) = Cell(p);
        var (x, y) = OsdLayout.Clamp(cx - _grabDx, cy - _grabDy, Grid);
        if (x != item.X)
            item.X = x;
        if (y != item.Y)
            item.Y = y;
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging == null)
            return;
        _dragging = null;
        e.Pointer.Capture(null);
        Cursor = new Cursor(StandardCursorType.Hand);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = null;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (SelectedItem is not { } item)
            return;
        var (dx, dy) = e.Key switch
        {
            Key.Left => (-1, 0),
            Key.Right => (1, 0),
            Key.Up => (0, -1),
            Key.Down => (0, 1),
            _ => (0, 0),
        };
        if (dx == 0 && dy == 0)
            return;
        var (x, y) = OsdLayout.Clamp(item.X + dx, item.Y + dy, Grid);
        item.X = x;
        item.Y = y;
        e.Handled = true;
    }
}
