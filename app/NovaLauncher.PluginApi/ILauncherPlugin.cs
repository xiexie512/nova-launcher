namespace NovaLauncher.PluginApi;

/// <summary>启动器插件接口：把插件 dll 放入 plugins 目录即可被加载。</summary>
public interface ILauncherPlugin
{
    string Name { get; }
    string Version { get; }
    string Author { get; }
    string Description { get; }
    void OnLoad(ILauncherApi api);
    void OnUnload();

    /// <summary>
    /// 插件页面上的操作按钮被点击时回调（默认接口方法，旧插件无需实现）。
    /// actionId 来自 RegisterPage 时页面上注册的 PluginActionInfo.Id。
    /// </summary>
    void OnAction(string actionId)
    {
        // 默认不做任何事，兼容旧插件。
    }
}
