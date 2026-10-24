using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using NovaLauncher.ViewModels;

namespace NovaLauncher.Views;

/// <summary>轻量实时曲线图（无第三方依赖，纯 DrawingContext 绘制）。</summary>
public sealed class MonitorChart : Control
{
    public static readonly StyledProperty<MonitorViewModel?> ViewModelProperty =
        AvaloniaProperty.Register<MonitorChart, MonitorViewModel?>(nameof(ViewModel));

    public MonitorViewModel? ViewModel
    {
        get => GetValue(ViewModelProperty);
        set
        {
            if (ViewModel != null) ViewModel.ChartUpdated -= OnUpdated;
            SetValue(ViewModelProperty, value);
            if (value != null)
            {
                value.ChartUpdated += OnUpdated;
                InvalidateVisual();
            }
        }
    }

    private void OnUpdated() => InvalidateVisual();

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 10 || h < 10) return;

        // 背景网格
        var gridPen = new Pen(Brushes.Transparent, 0);
        _ = gridPen;

        // 图例
        ctx.DrawText(new FormattedText("CPU %", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, 11, Brushes.Gray), new Point(8, 6));
        ctx.DrawText(new FormattedText("内存 MB", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, 11, Brushes.Gray), new Point(w / 2 + 8, 6));

        if (ViewModel == null) return;
        DrawSeries(ctx, ViewModel.CpuSeries, new SolidColorBrush(Color.Parse("#4CBE59")), w, h, 400, 0, w / 2);
        DrawSeries(ctx, ViewModel.MemSeries, new SolidColorBrush(Color.Parse("#4A9BE8")), w, h, 8192, w / 2, w / 2);
    }

    private static void DrawSeries(DrawingContext ctx, System.Collections.Generic.IReadOnlyList<double> data,
        IBrush brush, double w, double h, double max, double xOff, double width)
    {
        if (data.Count < 2) return;
        var pen = new Pen(brush, 2);
        var step = width / (MonitorViewModelMaxPoints - 1);
        var prev = new Point();
        for (int i = 0; i < data.Count; i++)
        {
            var x = xOff + i * step;
            var y = h - 22 - (data[i] / max) * (h - 34);
            var pt = new Point(x, y);
            if (i == 0) prev = pt;
            else ctx.DrawLine(pen, prev, pt);
            prev = pt;
        }
    }

    private const int MonitorViewModelMaxPoints = 60;
}
