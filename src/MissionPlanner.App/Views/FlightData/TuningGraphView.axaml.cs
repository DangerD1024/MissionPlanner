using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MissionPlanner.App.Core.ViewModels.FlightData;
using ScottPlot;
using ScottPlot.DataSources;
using ScottPlot.Plottables;

namespace MissionPlanner.App.Views.FlightData;

/// <summary>
/// Draws <see cref="TuningGraphViewModel"/>'s ring buffer with ScottPlot. Each sample, the buffer
/// is copied in time order into arrays allocated once (under the plot's lock, as ScottPlot may
/// render on another thread), the lines' render range is set to the valid part, and the axes
/// follow the last 30 s. Nothing is allocated per sample here.
/// </summary>
public sealed partial class TuningGraphView : UserControl
{
    private static readonly Color FigureColor = Color.FromHex("#15191e");
    private static readonly Color DataColor = Color.FromHex("#101317");
    private static readonly Color AxisColor = Color.FromHex("#9aa4b0");
    private static readonly Color GridColor = Color.FromHex("#2c333c");
    private static readonly Color TextColor = Color.FromHex("#e6e9ed");
    private static readonly Color LegendColor = Color.FromHex("#1c2127");

    private readonly List<Line> _lines = new();
    private TuningGraphViewModel? _vm;
    private IReadOnlyList<TuningSeries>? _builtFor;
    private double[] _xs = [];
    private bool _attached;

    public TuningGraphView()
    {
        InitializeComponent();
        StyleChart(Chart.Plot);
        Chart.UserInputProcessor.DoubleLeftClickBenchmark(false);

        DataContextChanged += (_, _) => Bind();
        AttachedToVisualTree += (_, _) =>
        {
            _attached = true;
            Bind();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _attached = false;
            Bind();
        };

        FieldSearch.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _vm != null)
            {
                _vm.ClosePickerCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

    private void Bind()
    {
        var next = _attached ? DataContext as TuningGraphViewModel : null;
        if (ReferenceEquals(next, _vm))
            return;

        if (_vm != null)
        {
            _vm.Sampled -= OnSampled;
            _vm.Reset -= OnReset;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }

        _vm = next;
        if (_vm == null)
            return;

        _vm.Sampled += OnSampled;
        _vm.Reset += OnReset;
        _vm.PropertyChanged += OnVmPropertyChanged;
        Rebuild();
    }

    private void OnSampled(object? sender, EventArgs e)
    {
        if (_vm is { IsPaused: false })
            Redraw();
    }

    private void OnReset(object? sender, EventArgs e)
    {
        if (_vm == null)
            return;
        if (!ReferenceEquals(_builtFor, _vm.Series))
            Rebuild();
        else
            Redraw();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TuningGraphViewModel.IsPaused) when _vm is { IsPaused: false }:
                Redraw();
                break;
            case nameof(TuningGraphViewModel.IsPickerOpen) when _vm is { IsPickerOpen: true }:
                Dispatcher.UIThread.Post(() => FieldSearch.Focus(), DispatcherPriority.Background);
                break;
        }
    }

    /// <summary>One line per series, over arrays sized to the buffer's capacity.</summary>
    private void Rebuild()
    {
        var vm = _vm;
        if (vm == null)
            return;

        var plot = Chart.Plot;
        lock (plot.Sync)
        {
            foreach (var line in _lines)
                plot.Remove(line.Scatter);
            _lines.Clear();

            var capacity = vm.Buffer.Capacity;
            if (_xs.Length != capacity)
                _xs = new double[capacity];

            foreach (var series in vm.Series)
            {
                var ys = new double[capacity];
                var source = new ScatterSourceDoubleArray(_xs, ys);
                var scatter = plot.Add.ScatterLine(source, Color.FromHex(series.Color));
                scatter.LegendText = series.LegendText;
                scatter.LineWidth = 1.6f;
                scatter.MarkerSize = 0;
                scatter.IsVisible = false;
                _lines.Add(new Line(scatter, source, ys));
            }

            _builtFor = vm.Series;
        }

        Redraw();
    }

    /// <summary>Copies the buffer into the plotted arrays and moves the axes to the last 30 s.</summary>
    private void Redraw()
    {
        var vm = _vm;
        if (vm == null)
            return;

        var buffer = vm.Buffer;
        var plot = Chart.Plot;
        lock (plot.Sync)
        {
            var n = buffer.CopyTimes(_xs);
            double min = double.PositiveInfinity, max = double.NegativeInfinity;
            var count = Math.Min(_lines.Count, buffer.SeriesCount);
            for (var s = 0; s < count; s++)
            {
                var line = _lines[s];
                buffer.CopyValues(s, line.Ys);
                var valid = Math.Min(buffer.ValidCount(s), n);
                if (valid < 1)
                {
                    line.Scatter.IsVisible = false;
                    continue;
                }

                line.Source.MinRenderIndex = n - valid;
                line.Source.MaxRenderIndex = n - 1;
                line.Scatter.IsVisible = true;
                var ys = line.Ys;
                for (var i = n - valid; i < n; i++)
                {
                    var v = ys[i];
                    if (v < min)
                        min = v;
                    if (v > max)
                        max = v;
                }
            }

            // the trace fills the first 30 s from the left, then scrolls
            var window = TuningGraphViewModel.WindowSeconds;
            var right = Math.Max(n > 0 ? _xs[n - 1] : 0, window);
            var left = right - window;
            if (double.IsInfinity(min))
            {
                min = -1;
                max = 1;
            }

            // more room on top, where the legend sits
            var span = max - min;
            var pad = span > 1e-9 ? span * 0.08 : Math.Max(1, Math.Abs(max) * 0.1);
            plot.Axes.SetLimits(left, right, min - pad, max + pad * 3);
        }

        Chart.Refresh();
    }

    private static void StyleChart(Plot plot)
    {
        plot.FigureBackground.Color = FigureColor;
        plot.DataBackground.Color = DataColor;
        plot.Axes.Color(AxisColor);
        plot.Axes.FrameColor(GridColor);
        plot.Grid.MajorLineColor = GridColor;

        plot.Legend.BackgroundColor = LegendColor.WithAlpha(0.85);
        plot.Legend.FontColor = TextColor;
        plot.Legend.OutlineColor = GridColor;
        plot.Legend.FontSize = 11;
        plot.Legend.ShowItemsFromHiddenPlottables = true;
        plot.ShowLegend(Alignment.UpperLeft, Orientation.Horizontal);

        foreach (var axis in plot.Axes.GetAxes())
        {
            axis.TickLabelStyle.FontSize = 11;
            axis.TickLabelStyle.ForeColor = AxisColor;
        }

        plot.Layout.Fixed(new PixelPadding(44, 10, 22, 6));
    }

    private sealed record Line(Scatter Scatter, ScatterSourceDoubleArray Source, double[] Ys);
}
