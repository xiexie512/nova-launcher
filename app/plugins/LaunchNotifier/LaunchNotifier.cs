using NovaLauncher.PluginApi;

namespace LaunchNotifier;

/// <summary>示例插件：启动前后处理（提示内存建议、下载完成通知）。</summary>
public sealed class LaunchNotifier : ILauncherPlugin
{
    private ILauncherApi? _api;

    public string Name => "启动小助手";
    public string Version => "1.0.0";
    public string Author => "NovaLauncher";
    public string Description => "启动前提示内存建议，下载完成时通知";

    public void OnLoad(ILauncherApi api)
    {
        _api = api;
        LauncherEvents.GameLaunching += info =>
        {
            api.Log($"提示：当前分配 {info.MemoryMb}MB。若安装光影/大型整合包，建议 4GB 以上。");
        };
        LauncherEvents.DownloadProgress += d =>
        {
            if (d.Done) api.Log($"[下载完成] {d.Key} ({d.Downloaded / 1024} KB)");
        };
    }

    public void OnUnload() { }
}
