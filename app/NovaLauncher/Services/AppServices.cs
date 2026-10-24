using System;
using System.IO;
using System.Text.Json;
using NovaLauncher.PluginApi;
using NovaLauncher.PluginHosting;

namespace NovaLauncher.Services;

/// <summary>服务容器：App 启动时初始化一次。</summary>
public static class AppServices
{
    public static VersionService Versions { get; private set; } = null!;
    public static LaunchService Launcher { get; private set; } = null!;
    public static ModpackService Modpacks { get; private set; } = null!;

    public static string KernelVersion { get; private set; } = "?";

    public static void Init()
    {
        AppPaths.EnsureAll();
        SeedBundledPlugins();
        try { KernelVersion = NovaLauncher.Kernel.NovaKernel.Version; }
        catch (Exception e)
        {
            KernelVersion = "内核加载失败";
            LogService.Error("C 内核加载失败（libnova_kernel.dylib / nova_kernel.dll 缺失?）: " + e.Message);
        }
        Versions = new VersionService();
        Launcher = new LaunchService(Versions);
        Modpacks = new ModpackService(Versions);

        // 把启动器事件转发给 Python / C 插件
        LauncherEvents.GameLaunching += i => PluginEventHub.Raise("game_launching", JsonSerializer.Serialize(i));
        LauncherEvents.GameLaunched += i => PluginEventHub.Raise("game_launched", JsonSerializer.Serialize(i));
        LauncherEvents.ConsoleLine += line => PluginEventHub.Raise("console_line", JsonSerializer.Serialize(new { line }));
        LauncherEvents.DownloadProgress += d => PluginEventHub.Raise("download_progress", JsonSerializer.Serialize(d));

        PluginService.LoadPlugins();
        LogService.Info($"NovaLauncher 就绪 · 内核 v{KernelVersion}");
    }

    /// <summary>首次运行时把安装包自带的示例插件（dll / py / dylib）播种到数据目录。</summary>
    private static void SeedBundledPlugins()
    {
        try
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, "plugins");
            if (!Directory.Exists(bundled)) return;
            if (Directory.GetFiles(AppPaths.Plugins).Length > 0) return;
            Directory.CreateDirectory(AppPaths.Plugins);
            foreach (var f in Directory.GetFiles(bundled))
                File.Copy(f, Path.Combine(AppPaths.Plugins, Path.GetFileName(f)), true);
        }
        catch { /* 播种失败不影响主流程 */ }
    }
}
