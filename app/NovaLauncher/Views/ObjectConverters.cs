using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace NovaLauncher.Views;

public static class ObjectConverters
{
    public static readonly IValueConverter BoolToBrush = new FuncValueConverter<bool, IBrush?>(
        b => b ? new SolidColorBrush(Color.Parse("#7FE08C")) : new SolidColorBrush(Color.Parse("#E07070")));

    public static readonly IValueConverter BoolToVisibility = new FuncValueConverter<bool, bool>(b => b);

    /// <summary>日历「今天」高亮背景；非今天但有游玩记录用弱高亮。</summary>
    public static readonly IValueConverter IsTodayToBrush = new FuncValueConverter<bool, IBrush?>(
        b => b ? new SolidColorBrush(Color.Parse("#2E6B3F")) : new SolidColorBrush(Color.Parse("Transparent")));

    /// <summary>count > 0 → true（用于列表非空显示）。</summary>
    public static readonly IValueConverter CountToNotEmpty = new FuncValueConverter<int, bool>(c => c > 0);

    /// <summary>count == 0 → true（用于空态提示显示）。</summary>
    public static readonly IValueConverter CountToEmpty = new FuncValueConverter<int, bool>(c => c <= 0);

    /// <summary>最近项目按钮文案：服务器 → 连接，实例 → 启动。</summary>
    public static readonly IValueConverter IsServerToAction = new FuncValueConverter<bool, string>(s => s ? "连接" : "启动");
}

public sealed class FuncValueConverter<TIn, TOut> : IValueConverter
{
    private readonly Func<TIn?, TOut> _f;
    public FuncValueConverter(Func<TIn?, TOut> f) => _f = f;
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => _f((TIn?)value);
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
