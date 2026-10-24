using System;
using System.Collections.Generic;

namespace NovaLauncher.PluginApi;

/// <summary>插件可用的启动器能力：日志、通知、设置、目录、UI 扩展（主页卡片 / 新页面 / 页面操作按钮）。</summary>
public interface ILauncherApi
{
    void Log(string message);
    void Notify(string title, string message);
    string GetSetting(string key, string defaultValue = "");
    void SetSetting(string key, string value);
    string LauncherHome { get; }
    string GameRoot { get; }

    /// <summary>在主页注册一张卡片（显示在可自由拖动的主页卡片区）。</summary>
    void RegisterHomeCard(HomeCardInfo card);

    /// <summary>在侧栏注册一个新页面（插件页，可承载说明、状态与操作入口）。</summary>
    void RegisterPage(PluginPageInfo page);

    /// <summary>更新已注册插件页面的正文文本（用于展示实时进度/状态）。</summary>
    void UpdatePage(string key, string body);

    /// <summary>更新主页卡片正文文本。</summary>
    void UpdateHomeCard(string cardId, string body);

    /// <summary>
    /// 供 Python/C 插件宿主在元数据就绪后更新插件名（用于设置项前缀与日志标签）。
    /// 默认无操作，兼容旧实现。
    /// </summary>
    void SetPluginName(string name) { }
}

/// <summary>主页卡片信息（插件注册）。</summary>
public sealed class HomeCardInfo
{
    private string _body = "";

    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Body { get => _body; set { if (_body == value) return; _body = value; BodyChanged?.Invoke(); } }
    public string Icon { get; init; } = "🧩";

    /// <summary>正文被插件更新时触发（供首页小组件实时刷新）。</summary>
    public event Action? BodyChanged;
}

/// <summary>插件页面上的一个操作按钮。</summary>
public sealed class PluginActionInfo
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string Icon { get; init; } = "";
}

/// <summary>插件页面信息（插件注册，出现在侧栏）。</summary>
public sealed class PluginPageInfo
{
    private string _body = "";

    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    public string Body { get => _body; set => SetBody(value); }
    public string Icon { get; init; } = "🧩";
    public List<PluginActionInfo> Actions { get; init; } = new();

    /// <summary>正文被插件更新时触发（供 UI 实时刷新）。</summary>
    public event Action? BodyChanged;

    public void SetBody(string value)
    {
        if (_body == value) return;
        _body = value;
        BodyChanged?.Invoke();
    }
}

/// <summary>启动器事件总线：插件在 OnLoad 中订阅。</summary>
public static class LauncherEvents
{
    public static event Action<LaunchInfo>? GameLaunching;
    public static event Action<LaunchInfo>? GameLaunched;
    public static event Action<string>? ConsoleLine;
    public static event Action<DownloadInfo>? DownloadProgress;

    public static void RaiseGameLaunching(LaunchInfo i) => GameLaunching?.Invoke(i);
    public static void RaiseGameLaunched(LaunchInfo i) => GameLaunched?.Invoke(i);
    public static void RaiseConsoleLine(string line) => ConsoleLine?.Invoke(line);
    public static void RaiseDownload(DownloadInfo d) => DownloadProgress?.Invoke(d);
}

public sealed class LaunchInfo
{
    public string VersionId { get; init; } = "";
    public string AccountName { get; init; } = "";
    public int MemoryMb { get; init; }
    public string GameDir { get; init; } = "";
}

public sealed class DownloadInfo
{
    public string Key { get; init; } = "";
    public long Downloaded { get; init; }
    public long Total { get; init; }
    public bool Done { get; init; }
}
