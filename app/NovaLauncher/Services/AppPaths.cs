using System;
using System.IO;

namespace NovaLauncher.Services;

/// <summary>启动器数据目录（跨平台）。</summary>
public static class AppPaths
{
    public static string Home { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NovaLauncher");

    public static string Games { get; } = Path.Combine(Home, "games");
    public static string Versions => Path.Combine(Games, "versions");
    public static string Libraries => Path.Combine(Games, "libraries");
    public static string Assets => Path.Combine(Games, "assets");
    /// <summary>插件目录。默认数据目录 plugins/；可用环境变量 NOVA_PLUGINS_DIR 覆盖（如指向仓库内 plugins 文件夹，便于开发调试）。</summary>
    public static string Plugins =>
        Environment.GetEnvironmentVariable("NOVA_PLUGINS_DIR") is { Length: > 0 } env
            ? env
            : Path.Combine(Home, "plugins");
    public static string Modpacks => Path.Combine(Home, "modpacks");
    public static string Cores => Path.Combine(Home, "cores");
    public static string Exports => Path.Combine(Home, "exports");
    public static string Logs => Path.Combine(Home, "logs");
    public static string SettingsFile => Path.Combine(Home, "settings.json");

    public static string VersionDir(string id) => Path.Combine(Versions, id);
    public static string VersionJson(string id) => Path.Combine(VersionDir(id), id + ".json");
    public static string VersionJar(string id) => Path.Combine(VersionDir(id), id + ".jar");
    public static string NativesDir(string id) => Path.Combine(VersionDir(id), "natives");
    public static string GameDir(string id) => Path.Combine(VersionDir(id), "instance");

    public static void EnsureAll()
    {
        foreach (var d in new[] { Home, Games, Versions, Libraries, Assets, Plugins, Modpacks, Cores, Exports, Logs })
            Directory.CreateDirectory(d);
    }
}
