using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GMap.NET;
using MissionPlanner.App.Controls.Logs;
using MissionPlanner.App.Core.Maps;
using MissionPlanner.App.Core.ViewModels.Logs;
using ScottPlot;
using ScottPlot.Plottables;

namespace MissionPlanner.App.Views.Logs;

/// <summary>
/// Log browser: the ScottPlot graph and the track map follow the view-model's series, events and
/// track; plottables live here only.
/// </summary>
public sealed partial class LogBrowserView : UserControl
{
    /// <summary>Markers drawn at most; the events list still has them all.</summary>
    private const int MaxMarkers = 1500;

    /// <summary>Above this many markers of a kind, they are drawn without labels.</summary>
    private const int MaxLabels = 60;

    private readonly Dictionary<GraphSeries, SignalXY> _lines = new();
    private readonly List<IPlottable> _markers = new();
    private readonly TrackLayer _track = new();
    private LogBrowserViewModel? _vm;
    private VerticalLine? _focus;
    private bool _mapFitted;

    public LogBrowserView()
    {
        InitializeComponent();
        StylePlot();

        Map.Provider = MapTiles.CommonProviders.FirstOrDefault();
        Map.Layers.Add(_track);
        Map.SizeChanged += (_, _) => FitMap();

        Graph.PointerMoved += OnGraphPointerMoved;
        Graph.PointerExited += (_, _) => SetCursor(null);

        // Mission Planner's right click on a field: graph it against the right-hand Y axis, or
        // remove it when graphed (the tree takes the press to select the row: handled releases too)
        Fields.AddHandler(PointerReleasedEvent, OnFieldsPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);

        EventList.SelectionChanged += (_, _) =>
        {
            if (EventList.SelectedItem is LogEvent e)
                _vm?.FocusEventCommand.Execute(e);
        };

        DataContextChanged += (_, _) => Attach(DataContext as LogBrowserViewModel);
        DetachedFromVisualTree += (_, _) => Attach(null);
    }

    private void Attach(LogBrowserViewModel? vm)
    {
        if (_vm == vm)
            return;

        if (_vm != null)
        {
            _vm.PropertyChanged -= OnVmChanged;
            _vm.Series.CollectionChanged -= OnSeriesChanged;
            _vm.FocusTimeRequested -= OnFocusTime;
            foreach (var s in _lines.Keys)
                s.PropertyChanged -= OnSeriesPropertyChanged;
        }

        _vm = vm;
        ResetPlot();
        if (vm == null)
            return;

        vm.PropertyChanged += OnVmChanged;
        vm.Series.CollectionChanged += OnSeriesChanged;
        vm.FocusTimeRequested += OnFocusTime;
        foreach (var s in vm.Series)
            AddLine(s);
        DrawMarkers();
        UpdateTrack();
        UpdateSideLayout();
        Graph.Plot.Axes.Bottom.Label.Text = vm.XAxisLabel;
        Rescale(all: true);
    }

    private void StylePlot()
    {
        var p = Graph.Plot;
        p.FigureBackground.Color = Color.FromHex("#15191e");
        p.DataBackground.Color = Color.FromHex("#101317");
        p.Axes.Color(Color.FromHex("#9aa4b0"));
        p.Grid.MajorLineColor = Color.FromHex("#262c34");
        p.Axes.FrameColor(Color.FromHex("#2c333c"));
        p.Legend.BackgroundColor = Color.FromHex("#1c2127").WithAlpha(0.9);
        p.Legend.FontColor = Color.FromHex("#e6e9ed");
        p.Legend.OutlineColor = Color.FromHex("#2c333c");
        p.Legend.Alignment = Alignment.UpperLeft;
        p.Axes.Bottom.Label.Text = "Time (s)";
        p.Axes.Bottom.Label.FontSize = 13;
        // the Y axes are titled with their lines' names (Rescale)
        p.Axes.Left.Label.Text = "";
        p.Axes.Left.Label.FontSize = 13;
        p.Axes.Right.Label.Text = "";
        p.Axes.Right.Label.FontSize = 13;
        p.Axes.Margins(0.01, 0.08);
    }

    private void ResetPlot()
    {
        foreach (var s in _lines.Keys)
            s.PropertyChanged -= OnSeriesPropertyChanged;
        _lines.Clear();
        _markers.Clear();
        _focus = null;
        Graph.Plot.Clear();
        Graph.Plot.Legend.IsVisible = false;
        Graph.Refresh();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LogBrowserViewModel.VisibleEvents):
                DrawMarkers();
                Graph.Refresh();
                break;
            case nameof(LogBrowserViewModel.Track):
                _mapFitted = false;
                UpdateSideLayout();
                UpdateTrack();
                // fit once the map has its new size
                Dispatcher.UIThread.Post(FitMap, DispatcherPriority.Background);
                break;
            case nameof(LogBrowserViewModel.MapVisible):
                UpdateSideLayout();
                Dispatcher.UIThread.Post(FitMap, DispatcherPriority.Background);
                break;
            case nameof(LogBrowserViewModel.XAxisLabel):
                Graph.Plot.Axes.Bottom.Label.Text = _vm!.XAxisLabel;
                Graph.Refresh();
                break;
            case nameof(LogBrowserViewModel.Source):
                // a new log: start from its whole time range
                Rescale(all: true);
                break;
        }
    }

    private void OnSeriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var s in _lines.Keys.ToList())
                RemoveLine(s);
        }

        if (e.OldItems != null)
        {
            foreach (GraphSeries s in e.OldItems)
                RemoveLine(s);
        }

        var first = _lines.Count == 0;
        if (e.NewItems != null)
        {
            foreach (GraphSeries s in e.NewItems)
                AddLine(s);
        }

        Rescale(all: first);
    }

    private void AddLine(GraphSeries s)
    {
        if (_lines.ContainsKey(s))
            return;
        var line = Graph.Plot.Add.SignalXY(s.X, s.Y, Color.FromHex(s.Color));
        line.LegendText = LegendText(s);
        line.LineWidth = 1.5f;
        line.Axes.YAxis = s.RightAxis ? Graph.Plot.Axes.Right : Graph.Plot.Axes.Left;
        _lines[s] = line;
        s.PropertyChanged += OnSeriesPropertyChanged;
    }

    private void RemoveLine(GraphSeries s)
    {
        if (!_lines.Remove(s, out var line))
            return;
        s.PropertyChanged -= OnSeriesPropertyChanged;
        Graph.Plot.Remove(line);
    }

    private void OnSeriesPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GraphSeries.RightAxis) || sender is not GraphSeries s || !_lines.TryGetValue(s, out var line))
            return;
        line.Axes.YAxis = s.RightAxis ? Graph.Plot.Axes.Right : Graph.Plot.Axes.Left;
        line.LegendText = LegendText(s);
        Rescale(all: false);
    }

    /// <summary>Right-hand lines are marked in the legend, as Mission Planner marks them " R".</summary>
    private static string LegendText(GraphSeries s) => s.RightAxis ? s.Name + " (R)" : s.Name;

    private void OnFieldsPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // a right click does not tick the checkbox itself (it answers the left button only)
        if (_vm == null || e.InitialPressMouseButton != MouseButton.Right ||
            (e.Source as StyledElement)?.DataContext is not LogTreeNode { IsField: true } node)
            return;
        _vm.ToggleField(node, rightAxis: true);
        e.Handled = true;
    }

    /// <summary>An axis title: the names of its lines, the first two when there are more.</summary>
    private static string AxisTitle(IEnumerable<GraphSeries> series)
    {
        var names = series.Select(s => s.Name).ToList();
        return names.Count switch
        {
            0 => "",
            <= 2 => string.Join(", ", names),
            _ => string.Join(", ", names.Take(2)) + " …",
        };
    }

    /// <summary>Fits the Y axes to the lines; with <paramref name="all"/> the time axis too.</summary>
    private void Rescale(bool all)
    {
        var p = Graph.Plot;
        var hasLines = _lines.Count > 0;
        p.Legend.IsVisible = hasLines;
        p.Axes.Left.Label.Text = AxisTitle(_lines.Keys.Where(s => !s.RightAxis));
        p.Axes.Right.Label.Text = AxisTitle(_lines.Keys.Where(s => s.RightAxis));

        if (!hasLines)
        {
            // no lines: show the log's time range so the markers have a place
            var src = _vm?.Source;
            if (src != null && !double.IsNaN(src.FirstTime) && src.LastTime > src.FirstTime)
                p.Axes.SetLimits(src.FirstTime, src.LastTime, 0, 1);
            Graph.Refresh();
            return;
        }

        var x = p.Axes.GetLimits();
        p.Axes.AutoScale();
        if (!all)
            p.Axes.SetLimitsX(x.Left, x.Right);
        Graph.Refresh();
    }

    private void DrawMarkers()
    {
        var p = Graph.Plot;
        foreach (var m in _markers)
            p.Remove(m);
        _markers.Clear();

        var events = _vm?.VisibleEvents ?? [];
        if (events.Count == 0)
            return;

        var counts = events.GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Count());
        var top = false;
        foreach (var e in events.Take(MaxMarkers))
        {
            var color = Color.FromHex(LogConverters.EventColor(e.Kind));
            var line = p.Add.VerticalLine(e.Time, 1, color.WithAlpha(e.Kind == LogEventKind.Message ? 0.35 : 0.75),
                e.Kind == LogEventKind.Mode ? LinePattern.Solid : LinePattern.Dashed);
            line.ExcludeFromLegend = true;
            line.EnableAutoscale = false;

            // messages are many and long: lines only, their text is in the events list
            var labelled = e.Kind != LogEventKind.Message && counts[e.Kind] <= MaxLabels;
            if (labelled)
            {
                line.LabelText = e.Text.Length > 28 ? e.Text[..27] + "…" : e.Text;
                line.LabelFontSize = 11;
                line.LabelFontColor = Color.FromHex("#0f1216");
                line.LabelBackgroundColor = color;
                line.LabelRotation = 0;
                // modes along the top edge; errors and events in a row just below it, alternating
                // between two rows so neighbours do not overlap (the bottom edge has the time ticks)
                line.LabelOppositeAxis = true;
                if (e.Kind != LogEventKind.Mode)
                {
                    line.LabelOffsetY = top ? 40 : 20;
                    top = !top;
                }
            }

            _markers.Add(line);
        }
    }

    private void OnFocusTime(object? sender, double time)
    {
        var p = Graph.Plot;
        var lim = p.Axes.GetLimits();
        var half = Math.Max(1, (lim.Right - lim.Left) / 2);
        p.Axes.SetLimitsX(time - half, time + half);

        if (_focus != null)
            p.Remove(_focus);
        _focus = p.Add.VerticalLine(time, 2, Color.FromHex("#8cc63f"));
        _focus.ExcludeFromLegend = true;
        _focus.EnableAutoscale = false;
        Graph.Refresh();
        SetCursor(_vm?.TrackPointAt(time));
    }

    private void OnGraphPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_vm == null || !_vm.HasTrack)
            return;
        var pos = e.GetPosition(Graph);
        var scale = Graph.DisplayScale;
        var c = Graph.Plot.GetCoordinates(new Pixel((float)(pos.X * scale), (float)(pos.Y * scale)));
        SetCursor(_vm.TrackPointAt(c.X));
    }

    private void SetCursor(LogTrackPoint? point)
    {
        if (_track.Cursor == point)
            return;
        _track.Cursor = point;
        Map.InvalidateVisual();
    }

    private void UpdateTrack()
    {
        _track.Points = _vm?.Track ?? [];
        _track.Cursor = null;
        FitMap();
        Map.InvalidateVisual();
    }

    private void FitMap()
    {
        if (_mapFitted || _track.Points.Count < 2 || Map.Bounds.Width < 20 || Map.Bounds.Height < 20 ||
            !Map.IsEffectivelyVisible)
            return;
        Map.ZoomToFit(_track.Points.Select(p => new PointLatLng(p.Lat, p.Lng)).ToList(), maxZoom: 18);
        _mapFitted = true;
    }

    /// <summary>The side column: map over events when there is a track, events alone otherwise.</summary>
    private void UpdateSideLayout()
    {
        var map = _vm?.MapVisible == true;
        SidePanel.RowDefinitions[0].Height = map ? new GridLength(3, GridUnitType.Star) : new GridLength(0);
        SidePanel.RowDefinitions[1].Height = map ? new GridLength(4) : new GridLength(0);
        SidePanel.RowDefinitions[2].Height = new GridLength(2, GridUnitType.Star);
    }
}
