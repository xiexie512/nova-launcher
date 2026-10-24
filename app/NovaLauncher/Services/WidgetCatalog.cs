using System.Collections.Generic;
using NovaLauncher.Models;

namespace NovaLauncher.Services;

/// <summary>
/// 侧边栏可添加的插件 / 小组件目录：现有首页模块 + 内置小插件。
/// 插件注册的主页卡片由 PluginService 动态追加（见 HomeViewModel.BuildAvailableWidgets）。
/// </summary>
public static class WidgetCatalog
{
    /// <summary>内置小组件（现有首页模块 + 新增小插件）。</summary>
    public static readonly IReadOnlyList<WidgetCatalogItem> BuiltIn = new List<WidgetCatalogItem>
    {
        new() { TypeKey = "greeting",        Title = "问候与日期",   Icon = "👋", Description = "今日问候与日期", DefaultCols = 1, DefaultRows = 1 },
        new() { TypeKey = "clock",           Title = "时钟",         Icon = "🕐", Description = "实时时钟与日期",   DefaultCols = 1, DefaultRows = 1 },
        new() { TypeKey = "quick_launch",    Title = "快捷启动",     Icon = "🚀", Description = "一键启动所选实例", DefaultCols = 1, DefaultRows = 1 },
        new() { TypeKey = "calendar",        Title = "日历",         Icon = "📅", Description = "本月日历与游玩记录", DefaultCols = 2, DefaultRows = 2 },
        new() { TypeKey = "pinned_instances",Title = "固定的实例",   Icon = "⭐", Description = "收藏的实例快速启动", DefaultCols = 2, DefaultRows = 2 },
        new() { TypeKey = "pinned_worlds",   Title = "固定的世界",   Icon = "🗺", Description = "收藏的世界快捷打开", DefaultCols = 1, DefaultRows = 2 },
        new() { TypeKey = "servers",         Title = "固定的服务器", Icon = "🖥", Description = "在线人数与连接",     DefaultCols = 1, DefaultRows = 2 },
        new() { TypeKey = "recent",          Title = "最近项目",     Icon = "🕘", Description = "最近游玩与服务器",   DefaultCols = 2, DefaultRows = 1 },
        new() { TypeKey = "versions",        Title = "版本下载",     Icon = "⬇", Description = "下载并启动游戏版本", DefaultCols = 2, DefaultRows = 2 },
        new() { TypeKey = "lan",             Title = "虚拟局域网",   Icon = "🛰", Description = "ZeroTier 虚拟内网",  DefaultCols = 1, DefaultRows = 1 },
        new() { TypeKey = "saves",           Title = "我的存档",     Icon = "🗃", Description = "浏览与管理存档",     DefaultCols = 1, DefaultRows = 2 },
        new() { TypeKey = "monitor",         Title = "性能监控",     Icon = "📊", Description = "游戏 CPU / 内存实时监控", DefaultCols = 2, DefaultRows = 1 },
    };
}
