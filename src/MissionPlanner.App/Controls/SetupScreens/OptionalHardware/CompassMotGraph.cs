using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MissionPlanner.App.Core.ViewModels.SetupScreens.OptionalHardware;

namespace MissionPlanner.App.Controls.SetupScreens.OptionalHardware;

/// <summary>
/// Compass/motor calibration graph (Mission Planner's ZedGraph): interference % (left axis) and
/// current in A (right axis) against throttle %, from samples ordered by throttle.
/// </summary>
public sealed class CompassMotGraph : Control
{
    public static readonly StyledProperty<IReadOnlyList<CompassMotSample>?> PointsProperty =
        AvaloniaProperty.Register<CompassMotGraph, IReadOnlyList<CompassMotSample>?>(nameof(Points));

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<CompassMotGraph, IBrush?>(nameof(Background));

    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<CompassMotGraph, IBrush?>(nameof(GridBrush));

    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        AvaloniaProperty.Register<CompassMotGraph, IBrush?>(nameof(TextBrush));

    public static readonly StyledProperty<IBrush?> InterferenceBrushProperty =
        AvaloniaProperty.Register<CompassMotGraph, IBrush?>(nameof(InterferenceBrush));

    public static readonly StyledProperty<IBrush?> CurrentBrushProperty =
        AvaloniaProperty.Register<CompassMotGraph, IBrush?>(nameof(CurrentBrush));

    static CompassMotGraph()
    {
        AffectsRender<CompassMotGraph>(PointsProperty, BackgroundProperty, GridBrushProperty, TextBrushProperty,
            InterferenceBrushProperty, CurrentBrushProperty);
    }

    public IReadOnlyList<CompassMotSample>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public IBrush? GridBrush
    {
        get => GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public IBrush? TextBrush
    {
        get => GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    public IBrush? InterferenceBrush
    {
        get => GetValue(InterferenceBrushProperty);
        set => SetValue(InterferenceBrushProperty, value);
    }

    public IBrush? CurrentBrush
    {
        get => GetValue(CurrentBrushProperty);
        set => SetValue(CurrentBrushProperty, value);
    }

    /// <summary>A round axis maximum at or above <paramref name="value"/> (10, 20, 50, 100...).</summary>
    internal static double NiceMax(double value, double floor)
    {
        var v = Math.Max(value, floor);
        var mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (var m in new[] { 1, 2, 2.5, 5, 10 })
        {
            if (m * mag >= v)
                return m * mag;
        }

        return 10 * mag;
    }

    public override void Render(DrawingContext ctx)
    {
        var b = new Rect(Bounds.Size);
        if (Background is { } bg)
            ctx.FillRectangle(bg, b, 6);

        var text = TextBrush ?? Brushes.Gray;
        var gridPen = new Pen(GridBrush ?? Brushes.DimGray, 1);
        var interferenceBrush = InterferenceBrush ?? Brushes.IndianRed;
        var currentBrush = CurrentBrush ?? Brushes.LimeGreen;

        const double left = 52, right = 52, top = 30, bottom = 40;
        var plot = new Rect(left, top, Math.Max(10, b.Width - left - right), Math.Max(10, b.Height - top - bottom));

        var points = Points ?? [];
        var maxInterference = NiceMax(points.Count == 0 ? 0 : points.Max(p => p.InterferencePercent), 100);
        var maxCurrent = NiceMax(points.Count == 0 ? 0 : points.Max(p => p.CurrentAmps), 10);

        // grid and axes: throttle 0-100 % every 10, five rows
        for (var i = 0; i <= 10; i++)
        {
            var x = Math.Round(plot.Left + plot.Width * i / 10) + 0.5;
            ctx.DrawLine(gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var t = Label((i * 10).ToString(CultureInfo.InvariantCulture), text);
            ctx.DrawText(t, new Point(x - t.Width / 2, plot.Bottom + 4));
        }

        for (var i = 0; i <= 5; i++)
        {
            var y = Math.Round(plot.Bottom - plot.Height * i / 5) + 0.5;
            ctx.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var li = Label((maxInterference * i / 5).ToString("0", CultureInfo.InvariantCulture), interferenceBrush);
            ctx.DrawText(li, new Point(plot.Left - li.Width - 6, y - li.Height / 2));
            var lc = Label((maxCurrent * i / 5).ToString("0.#", CultureInfo.InvariantCulture), currentBrush);
            ctx.DrawText(lc, new Point(plot.Right + 6, y - lc.Height / 2));
        }

        var xTitle = Label("Throttle %", text);
        ctx.DrawText(xTitle, new Point(plot.Center.X - xTitle.Width / 2, plot.Bottom + 20));
        var yLeft = Label("Interference %", interferenceBrush);
        ctx.DrawText(yLeft, new Point(plot.Left - 40, 8));
        var yRight = Label("Amps", currentBrush);
        ctx.DrawText(yRight, new Point(plot.Right - yRight.Width + 40, 8));

        if (points.Count == 0)
        {
            var empty = Label("Press Start and raise the throttle: interference and current appear here.", text);
            ctx.DrawText(empty, new Point(plot.Center.X - empty.Width / 2, plot.Center.Y - empty.Height / 2));
            return;
        }

        using (ctx.PushClip(plot.Inflate(1)))
        {
            Line(ctx, points, plot, p => p.InterferencePercent / maxInterference, interferenceBrush);
            Line(ctx, points, plot, p => p.CurrentAmps / maxCurrent, currentBrush);
        }
    }

    private static void Line(DrawingContext ctx, IReadOnlyList<CompassMotSample> points, Rect plot, Func<CompassMotSample, double> y, IBrush brush)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            for (var i = 0; i < points.Count; i++)
            {
                var p = new Point(
                    plot.Left + plot.Width * Math.Clamp(points[i].ThrottlePercent / 100, 0, 1),
                    plot.Bottom - plot.Height * Math.Clamp(y(points[i]), 0, 1));
                if (i == 0)
                    g.BeginFigure(p, false);
                else
                    g.LineTo(p);
            }

            g.EndFigure(false);
        }

        ctx.DrawGeometry(null, new Pen(brush, 1.8), geo);
    }

    private static FormattedText Label(string s, IBrush brush) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default), 11, brush);
}
