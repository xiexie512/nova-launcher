using System;

namespace NovaLauncher.PluginHosting;

/// <summary>
/// 多语言插件事件中枢：主程序把 LauncherEvents 事件转发进来，
/// Python 插件（进程桥）与 C 插件（原生 ABI）各自订阅并分发到插件侧。
/// 订阅方必须在 Dispose 时取消订阅。
/// </summary>
public static class PluginEventHub
{
    /// <summary>事件名 + JSON 数据。</summary>
    public static event Action<string, string>? Raised;

    public static void Raise(string eventName, string dataJson)
    {
        try { Raised?.Invoke(eventName, dataJson); }
        catch { /* 单个订阅方异常不影响其他插件 */ }
    }
}
