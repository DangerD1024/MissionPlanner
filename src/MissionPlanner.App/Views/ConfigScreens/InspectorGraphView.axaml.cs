using Avalonia.Controls;
using MissionPlanner.App.Core.ViewModels.ConfigScreens;
using ScottPlot;

namespace MissionPlanner.App.Views.ConfigScreens;

public sealed partial class InspectorGraphView : UserControl
{
    private static readonly Color[] Palette =
    [
        Color.FromHex("#8cc63f"), Color.FromHex("#4fa3e3"), Color.FromHex("#f0b429"), Color.FromHex("#e5534b"),
        Color.FromHex("#b67fe0"), Color.FromHex("#3cc6b0"), Color.FromHex("#f08c4a"), Color.FromHex("#d6d9de"),
    ];

    private InspectorGraphViewModel? _vm;

    public InspectorGraphView()
    {
        InitializeComponent();
        StylePlot();

        DataContextChanged += (_, _) =>
        {
            if (_vm != null)
                _vm.DataChanged -= OnDataChanged;
            _vm = DataContext as InspectorGraphViewModel;
            if (_vm != null)
            {
                _vm.DataChanged += OnDataChanged;
                Plot.Plot.YLabel(_vm.YLabel);
                Redraw();
            }
        };
    }

    private void StylePlot()
    {
        var plot = Plot.Plot;
        plot.FigureBackground.Color = Color.FromHex("#15191e");
        plot.DataBackground.Color = Color.FromHex("#101317");
        plot.Axes.Color(Color.FromHex("#9aa4b0"));
        plot.Grid.MajorLineColor = Color.FromHex("#2c333c");
        plot.Legend.BackgroundColor = Color.FromHex("#1c2127");
        plot.Legend.FontColor = Color.FromHex("#e6e9ed");
        plot.Legend.OutlineColor = Color.FromHex("#2c333c");
        plot.XLabel("time (s)");
    }

    private void OnDataChanged(object? sender, EventArgs e) => Redraw();

    private void Redraw()
    {
        if (_vm == null)
            return;
        var plot = Plot.Plot;
        plot.Clear();
        var series = _vm.Snapshot();
        for (var i = 0; i < series.Count; i++)
        {
            var s = series[i];
            if (s.X.Length == 0)
                continue;
            var line = plot.Add.Scatter(s.X, s.Y);
            line.LegendText = s.Name;
            line.MarkerSize = 0;
            line.LineWidth = 1.6f;
            line.Color = Palette[i % Palette.Length];
        }

        if (series.Count > 1)
            plot.ShowLegend(Alignment.UpperLeft);
        else
            plot.HideLegend();
        plot.Axes.AutoScale();
        Plot.Refresh();
    }
}
