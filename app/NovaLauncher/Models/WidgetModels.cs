using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NovaLauncher.Models;

/// <summary>网格尺寸常量（单元格像素与列数）。</summary>
public static class WidgetGrid
{
    public const int Columns = 6;          // 网格列数
    public const double CellW = 150;       // 单格宽（px）
    public const double CellH = 128;       // 单格高（px）
    public const double Gap = 10;          // 格间距（px）

    /// <summary>整个网格可用宽度。</summary>
    public const double CanvasWidth = Columns * CellW + (Columns - 1) * Gap;
}

/// <summary>侧边栏可添加的插件 / 小组件条目（含现有首页模块、内置小插件、插件卡片）。</summary>
public sealed class WidgetCatalogItem
{
    public string TypeKey { get; init; } = "";
    public string Title { get; init; } = "";
    public string Icon { get; init; } = "🧩";
    public string Description { get; init; } = "";
    public int DefaultCols { get; init; } = 1;
    public int DefaultRows { get; init; } = 1;
    /// <summary>是否为插件注册的主页卡片。</summary>
    public bool IsPluginCard { get; init; }
    public string? PluginCardId { get; init; }
}

/// <summary>已放置到网格的插件 / 小组件实例。1×2 / 2×1 / 2×2 / 1×1 由 Cols×Rows 表达。</summary>
public sealed class PlacedWidget : ObservableObject
{
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");
    public string TypeKey { get; init; } = "";
    public string Title { get; init; } = "";
    public string Icon { get; init; } = "🧩";
    public bool IsPluginCard { get; init; }
    public string? PluginCardId { get; init; }

    private int _row;
    private int _col;
    private int _cols = 1;
    private int _rows = 1;

    public int Row { get => _row; set { if (SetProperty(ref _row, value)) RaiseGeo(); } }
    public int Col { get => _col; set { if (SetProperty(ref _col, value)) RaiseGeo(); } }
    public int Cols { get => _cols; set { if (SetProperty(ref _cols, value)) RaiseGeo(); } }
    public int Rows { get => _rows; set { if (SetProperty(ref _rows, value)) RaiseGeo(); } }

    // 网格像素几何（由 行/列/尺寸 计算）
    public double X => Col * (WidgetGrid.CellW + WidgetGrid.Gap);
    public double Y => Row * (WidgetGrid.CellH + WidgetGrid.Gap);
    public double W => Cols * WidgetGrid.CellW + (Cols - 1) * WidgetGrid.Gap;
    public double H => Rows * WidgetGrid.CellH + (Rows - 1) * WidgetGrid.Gap;

    /// <summary>尺寸文案，如 1×2。</summary>
    public string SizeLabel => $"{Cols}×{Rows}";

    private void RaiseGeo()
    {
        OnPropertyChanged(nameof(X));
        OnPropertyChanged(nameof(Y));
        OnPropertyChanged(nameof(W));
        OnPropertyChanged(nameof(H));
        OnPropertyChanged(nameof(SizeLabel));
    }
}

/// <summary>网格布局持久化条目。</summary>
public sealed class WidgetLayoutEntry
{
    public string TypeKey { get; set; } = "";
    public string? PluginCardId { get; set; }
    public int Row { get; set; }
    public int Col { get; set; }
    public int Cols { get; set; } = 1;
    public int Rows { get; set; } = 1;
}
