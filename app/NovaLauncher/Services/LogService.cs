using System;
using System.Collections.ObjectModel;

namespace NovaLauncher.Services;

/// <summary>启动器与游戏日志总线（控制台页 + 插件事件）。</summary>
public static class LogService
{
    public static ObservableCollection<string> Lines { get; } = new();

    public static event Action<string>? LineAppended;

    // 重入保护：插件若在 ConsoleLine/LineAppended 处理器里再次调用 Append（例如把含 ERROR
    // 的行重新打日志），会形成 Append -> RaiseConsoleLine -> 插件 -> Append -> ... 的无限递归，
    // 直接导致栈溢出崩溃（启动游戏时游戏输出 ERROR 行即触发）。用该标志禁止嵌套重放事件。
    private static bool _raisingConsoleLine;

    public static void Info(string msg) => Append("[信息] " + msg);
    public static void Warn(string msg) => Append("[警告] " + msg);
    public static void Error(string msg) => Append("[错误] " + msg);

    public static void Append(string line)
    {
        if (Lines.Count > 800) Lines.RemoveAt(0);
        Lines.Add(line);
        LineAppended?.Invoke(line);
        if (!_raisingConsoleLine)
        {
            _raisingConsoleLine = true;
            try { PluginApi.LauncherEvents.RaiseConsoleLine(line); }
            finally { _raisingConsoleLine = false; }
        }
        Console.WriteLine(line);
    }
}
