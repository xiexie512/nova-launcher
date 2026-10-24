using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using NovaLauncher.PluginApi;

/// <summary>
/// NovaModsLoader 端到端验证（无 GUI）：
/// 1) 模拟 PluginService 的加载路径（LoadFromAssemblyPath + 反射 ILauncherPlugin）；
/// 2) 验证插件注册页面/卡片；
/// 3) 触发 OnAction 走真实逻辑：Fabric 安装、Modrinth 搜索、反编译启动器；
/// 4) 反编译前先载入 NovaLauncher.dll，使反射分析能读到启动器结构。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        // bin/Release/net8.0 → 上溯 6 层到 nova-launcher 根
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.."));
        var pluginDll = Path.Combine(root, "app/plugins/NovaModsLoader/bin/Release/net8.0/NovaModsLoader.dll");
        var novaDll = Path.Combine(root, "app/NovaLauncher/bin/Release/net8.0/NovaLauncher.dll");

        var tempHome = Path.Combine(Path.GetTempPath(), "nova-mods-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempHome);
        Console.WriteLine($"[TEST] 临时目录: {tempHome}");

        var api = new MockApi(tempHome);
        var failures = 0;

        // 0) 载入 NovaLauncher.dll，供反编译分析
        if (File.Exists(novaDll)) Assembly.LoadFrom(novaDll);
        else Console.WriteLine("[TEST] 未找到 NovaLauncher.dll（跳过反编译程序集注入，用已加载程序集兜底）");

        // 1) 模拟插件加载
        Console.WriteLine($"[TEST] 加载插件: {pluginDll}");
        ILauncherPlugin plugin;
        if (File.Exists(pluginDll))
        {
            var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(pluginDll);
            var impl = asm.GetTypes().FirstOrDefault(t => !t.IsAbstract && !t.IsInterface
                && typeof(ILauncherPlugin).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) != null);
            if (impl == null) { Console.WriteLine("[FAIL] 插件未找到 ILauncherPlugin 实现"); return 1; }
            plugin = (ILauncherPlugin)Activator.CreateInstance(impl)!;
        }
        else
        {
            plugin = new NovaModsLoader.NovaModsLoader(); // 编译期引用兜底
        }

        plugin.OnLoad(api);
        Console.WriteLine($"[OK] 插件载入: {plugin.Name} v{plugin.Version} by {plugin.Author}");

        // 页面/卡片
        if (api.Pages.Count == 0) { Console.WriteLine("[FAIL] 未注册插件页面"); failures++; }
        if (api.Cards.Count == 0) { Console.WriteLine("[FAIL] 未注册主页卡片"); failures++; }
        Console.WriteLine($"[OK] 注册页面 {api.Pages.Count} 个，卡片 {api.Cards.Count} 个，按钮 {api.Pages.Sum(p => p.Actions.Count)} 个");

        // 2) Fabric 安装（真实网络 + 写版本 JSON）
        Console.WriteLine("\n=== 测试: 安装 Fabric ===");
        if (RunAction(plugin, "install_fabric", api, "fabric-loader-1.21.1", 120))
            Console.WriteLine("[OK] Fabric 安装成功");
        else { Console.WriteLine("[FAIL] Fabric 安装失败/超时"); failures++; }

        // 校验产出的版本 JSON
        var fabricJson = Path.Combine(api.GameRoot, "versions", "fabric-loader-1.21.1", "fabric-loader-1.21.1.json");
        if (File.Exists(fabricJson))
        {
            var node = System.Text.Json.JsonDocument.Parse(File.ReadAllText(fabricJson)).RootElement;
            var okMain = node.GetProperty("mainClass").GetString()?.Contains("fabricmc") == true;
            var okLibs = node.GetProperty("libraries").GetArrayLength() > 0;
            Console.WriteLine($"[{(okMain && okLibs ? "OK" : "FAIL")}] 版本JSON: mainClass含fabricmc={okMain}, libraries={node.GetProperty("libraries").GetArrayLength()}");
            if (!okMain || !okLibs) failures++;
        }
        else { Console.WriteLine("[FAIL] 未生成 fabric 版本 JSON"); failures++; }

        // 3) Quilt 安装（同 Fabric 机制）
        Console.WriteLine("\n=== 测试: 安装 Quilt ===");
        if (RunAction(plugin, "install_quilt", api, "quilt-loader-1.21.1", 120))
        {
            var quiltJson = Path.Combine(api.GameRoot, "versions", "quilt-loader-1.21.1", "quilt-loader-1.21.1.json");
            if (File.Exists(quiltJson))
            {
                var r = System.Text.Json.JsonDocument.Parse(File.ReadAllText(quiltJson)).RootElement;
                var okMain = r.GetProperty("mainClass").GetString()?.Contains("quiltmc") == true;
                var okLibs = r.GetProperty("libraries").GetArrayLength() > 0;
                Console.WriteLine($"[{(okMain && okLibs ? "OK" : "FAIL")}] Quilt 版本JSON: mainClass含quiltmc={okMain}, libraries={r.GetProperty("libraries").GetArrayLength()}");
                if (!okMain || !okLibs) failures++;
            }
            else { Console.WriteLine("[FAIL] 未生成 quilt 版本 JSON"); failures++; }
        }
        else { Console.WriteLine("[FAIL] Quilt 安装失败/超时"); failures++; }

        // 4) Modrinth 搜索
        Console.WriteLine("\n=== 测试: Modrinth 搜索 ===");
        if (RunAction(plugin, "modrinth_search", api, "搜索完成", 60))
            Console.WriteLine("[OK] Modrinth 搜索成功");
        else { Console.WriteLine("[FAIL] Modrinth 搜索失败"); failures++; }

        // 5) 反编译
        Console.WriteLine("\n=== 测试: 反编译启动器 ===");
        if (RunAction(plugin, "decompile", api, "导出反编译报告", 30))
        {
            var report = Directory.GetFiles(Path.Combine(tempHome, "exports"), "*.md").FirstOrDefault();
            if (report != null)
            {
                var head = File.ReadAllText(report).Split('\n').Take(6);
                Console.WriteLine("[OK] 报告: " + report);
                Console.WriteLine("     " + string.Join(" | ", head));
            }
            else { Console.WriteLine("[FAIL] 未生成报告文件"); failures++; }
        }
        else { Console.WriteLine("[FAIL] 反编译失败"); failures++; }

        Console.WriteLine($"\n========== 结果: {(failures == 0 ? "全部通过 ✔" : failures + " 项失败 ✘")} ==========");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>触发插件动作，轮询页面正文直到出现关键词或超时。</summary>
    private static bool RunAction(ILauncherPlugin plugin, string actionId, MockApi api, string keyword, int timeoutSec)
    {
        var page = api.Pages.FirstOrDefault(p => p.Key == "nova-mods");
        api.ClearLog();
        plugin.OnAction(actionId);
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        while (DateTime.UtcNow < deadline)
        {
            if (page != null && (page.Body ?? "").Contains(keyword)) return true;
            if (api.LastError != null) { Console.WriteLine("[!] 错误: " + api.LastError); return false; }
            Thread.Sleep(1500);
        }
        Console.WriteLine("[!] 超时，页面正文: " + (page?.Body ?? "").Split('\n').TakeLast(4).Aggregate("", (a, b) => a + b + "\n"));
        return false;
    }
}

internal sealed class MockApi : ILauncherApi
{
    private readonly Dictionary<string, string> _settings = new();
    public List<PluginPageInfo> Pages { get; } = new();
    public List<HomeCardInfo> Cards { get; } = new();
    public string? LastError;
    private readonly List<string> _log = new();
    public string GameRoot { get; }
    public string LauncherHome { get; }

    public MockApi(string home)
    {
        LauncherHome = home;
        GameRoot = Path.Combine(home, "games");
        Directory.CreateDirectory(GameRoot);
    }

    public void Log(string message) => Console.WriteLine("  [插件日志] " + message);
    public void Notify(string title, string message) => Console.WriteLine($"  [通知] {title}: {message}");
    public string GetSetting(string key, string defaultValue = "") => _settings.TryGetValue(key, out var v) ? v : defaultValue;
    public void SetSetting(string key, string value) => _settings[key] = value;
    public void RegisterHomeCard(HomeCardInfo card) => Cards.Add(card);
    public void RegisterPage(PluginPageInfo page) => Pages.Add(page);
    public void UpdatePage(string key, string body)
    {
        var p = Pages.FirstOrDefault(x => x.Key == key);
        if (p != null) p.SetBody(body);
    }
    public void UpdateHomeCard(string cardId, string body)
    {
        var c = Cards.FirstOrDefault(x => x.Id == cardId);
        if (c != null) c.Body = body;
    }
    public void ClearLog() { LastError = null; _log.Clear(); }
}
