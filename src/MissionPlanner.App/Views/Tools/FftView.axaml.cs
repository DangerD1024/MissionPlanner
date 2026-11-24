using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using MissionPlanner.App.Core.Localization;
using MissionPlanner.App.Core.ViewModels.Tools;
using MissionPlanner.App.Localization;
using ScottPlot;
using ScottPlot.Avalonia;

namespace MissionPlanner.App.Views.Tools;

/// <summary>The FFT graphs: one ScottPlot per sensor, built when the view-model's plots change.</summary>
public sealed partial class FftView : UserControl
{
    private static readonly Color[] Axes = [Color.FromHex("#e5534b"), Color.FromHex("#8cc63f"), Color.FromHex("#4fa3e3")];

    private FftViewModel? _vm;

    public FftView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as FftViewModel);
        DetachedFromVisualTree += (_, _) => Attach(null);
        SizeChanged += (_, e) => Graphs.Columns = Math.Clamp((int)(e.NewSize.Width / 460), 1, 3);
    }

    private void Attach(FftViewModel? vm)
    {
        if (_vm == vm)
            return;
        if (_vm != null)
            _vm.PropertyChanged -= OnVmChanged;
        _vm = vm;
        if (vm != null)
        {
            vm.PropertyChanged += OnVmChanged;
            Build();
        }
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FftViewModel.Plots))
            Build();
    }

    private void Build()
    {
        Graphs.Children.Clear();
        if (_vm == null)
            return;
        var yLabel = L.Tr(_vm.YLabel);
        var magnitude = _vm.Magnitude;
        foreach (var p in _vm.Plots)
        {
            var avalonia = new AvaPlot { Height = 300, Margin = new Avalonia.Thickness(0, 0, 8, 8) };
            var plot = avalonia.Plot;
            Style(plot);
            plot.Title(p.Title, size: 12);
            plot.XLabel(L.Tr("Frequency (Hz)"));
            plot.YLabel(yLabel);
            for (var i = 0; i < p.Curves.Count; i++)
            {
                var c = p.Curves[i];
                var line = plot.Add.ScatterLine(p.Frequencies, c.Values);
                line.LegendText = c.Name;
                line.LineWidth = 1.3f;
                line.Color = Axes[i % Axes.Length];
            }

            plot.ShowLegend(Alignment.UpperRight);
            plot.Axes.SetLimitsX(0, p.SampleRate / 2);
            plot.Axes.SetLimitsY(p.YMin, p.YMax);
            avalonia.PointerMoved += (_, e) => ShowPoint(avalonia, p, e, magnitude);
            avalonia.PointerExited += (_, _) => TrBinding.Bind(Cursor, TextBlock.TextProperty, "Point at a graph to read the frequency and rpm.");
            avalonia.Refresh();
            Graphs.Children.Add(avalonia);
        }
    }

    private void ShowPoint(AvaPlot avalonia, FftPlot p, PointerEventArgs e, bool magnitude)
    {
        if (p.Frequencies.Length == 0)
            return;
        var pos = e.GetPosition(avalonia);
        var scale = avalonia.DisplayScale;
        var coords = avalonia.Plot.GetCoordinates(new Pixel((float)(pos.X * scale), (float)(pos.Y * scale)));
        var step = p.Frequencies.Length > 1 ? p.Frequencies[1] - p.Frequencies[0] : 1;
        var i = Math.Clamp((int)Math.Round(coords.X / step), 0, p.Frequencies.Length - 1);
        var best = p.Curves.Select(c => c.Values[i]).Max();
        Cursor.Text = $"{p.Sensor}: " + FftViewModel.PointText(p.Frequencies[i], best, magnitude);
    }

    private static void Style(Plot plot)
    {
        plot.FigureBackground.Color = Color.FromHex("#15191e");
        plot.DataBackground.Color = Color.FromHex("#101317");
        plot.Axes.Color(Color.FromHex("#9aa4b0"));
        plot.Grid.MajorLineColor = Color.FromHex("#2c333c");
        plot.Legend.BackgroundColor = Color.FromHex("#1c2127");
        plot.Legend.FontColor = Color.FromHex("#e6e9ed");
        plot.Legend.OutlineColor = Color.FromHex("#2c333c");
    }
}
