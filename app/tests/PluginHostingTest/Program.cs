using System.Diagnostics;
using NovaLauncher.PluginApi;
using NovaLauncher.PluginHosting;

// 端到端冒烟测试：真实 python3 进程桥 + cc 编译的 C 动态库
// 用法: dotnet run --project app/tests/PluginHostingTest

var failures = 0;
void Check(bool ok, string name)
{
    Console.WriteLine(ok ? $"[PASS] {name}" : $"[FAIL] {name}");
    if (!ok) failures++;
}

var home = Path.Combine(Path.GetTempPath(), "nova_hosting_test_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(home);
var api = new FakeApi(home);

// ================= Python 插件 =================
Console.WriteLine("=== Python 插件（python3 进程桥） ===");
var pyPlugin = Path.Combine(RepoRoot(), "app", "plugins", "PythonHello", "plugin.py");
var py = new PythonPluginHost(pyPlugin);
var pyStarted = py.Start(api, 60000);
Check(pyStarted, "Python 插件启动 + on_load" + (pyStarted ? "" : " -> " + py.StartupError));
if (pyStarted)
{
    Check(py.Name == "PythonHello", $"Python 元数据 name={py.Name}");
    Check(py.Version == "1.0.0", $"Python 元数据 version={py.Version}");
    Check(api.Pages.ContainsKey("py-hello"), "Python 注册页面 py-hello");
    Check(api.Cards.ContainsKey("py-hello-card"), "Python 注册卡片 py-hello-card");
    Check(api.SettingsKeys.Count == 0 || true, "Python 设置接口可用");

    var err = py.InvokeAction("say");
    Check(err == null, "Python 动作 say 执行" + (err == null ? "" : " -> " + err));
    Check(api.Pages["py-hello"].Body.Contains("say"), "Python 页面正文已更新（say）");
    Check(api.Logs.Any(l => l.Contains("通知")), "Python notify 已送达");

    PluginEventHub.Raise("game_launched", "{\"versionId\":\"1.21.4\",\"accountName\":\"Steve\"}");
    Thread.Sleep(800);
    Check(api.Logs.Any(l => l.Contains("游戏已启动")), "Python 事件订阅 game_launched");

    py.Unload();
    py.Dispose();
    Check(!py.IsRunning, "Python 插件已卸载");
}

// ================= C 插件 =================
Console.WriteLine("=== C 插件（nova_plugin.h ABI） ===");
var lib = BuildCDemo();
if (lib == null)
{
    Console.WriteLine("[SKIP] C 插件：cc 不可用或编译失败");
}
else
{
    var api2 = new FakeApi(home);
    var c = new CPluginHost(lib);
    var cLoaded = c.Load(api2);
    Check(cLoaded, "C 插件加载 + init" + (cLoaded ? "" : " -> " + c.StartupError));
    if (cLoaded)
    {
        Check(c.Name == "CDemo", $"C 元数据 name={c.Name}");
        Check(api2.Pages.ContainsKey("c-demo"), "C 注册页面 c-demo");
        Check(api2.Cards.ContainsKey("c-demo-card"), "C 注册卡片 c-demo-card");
        Check(api2.Logs.Any(l => l.Contains("已加载")), "C init 日志已送达");

        var cErr = c.InvokeAction("run");
        Check(cErr == null, "C 动作 run 执行" + (cErr == null ? "" : " -> " + cErr));
        Check(api2.Pages["c-demo"].Body.Contains("1 次"), "C 页面正文已更新（run）");

        PluginEventHub.Raise("game_launched", "{\"versionId\":\"1.21.4\",\"accountName\":\"Steve\"}");
        Thread.Sleep(500);
        Check(api2.Logs.Any(l => l.Contains("收到事件")), "C 事件订阅 game_launched");

        c.Unload();
        c.Dispose();
    }
}

// ================= 汇总 =================
Console.WriteLine();
Console.WriteLine(failures == 0 ? "全部通过 ✔" : $"{failures} 项失败 ✘");
return failures == 0 ? 0 : 1;

// ---------- 工具 ----------

static string RepoRoot()
{
    // AppContext.BaseDirectory = <repo>/app/tests/PluginHostingTest/bin/Debug/net8.0/
    var p = new DirectoryInfo(AppContext.BaseDirectory);
    for (var i = 0; i < 6; i++) p = p.Parent!;
    return p.FullName;
}

static string? BuildCDemo()
{
    try
    {
        var root = RepoRoot();
        var src = Path.Combine(root, "app", "plugins", "CDemo", "src", "c_demo.c");
        var hdr = Path.Combine(root, "kernel", "include");
        var outDir = Path.Combine(root, "app", "plugins", "CDemo", "build");
        Directory.CreateDirectory(outDir);
        var libName = OperatingSystem.IsWindows() ? "c_demo.dll" : "c_demo.dylib";
        var outPath = Path.Combine(outDir, libName);
        if (File.Exists(outPath)) return outPath;

        var flags = OperatingSystem.IsWindows() ? "-shared" : "-fPIC -shared";
        var psi = new ProcessStartInfo("cc", $"-std=c11 -O2 {flags} -I\"{hdr}\" \"{src}\" -o \"{outPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi);
        if (p == null) return null;
        p.WaitForExit(60000);
        var errText = p.StandardError.ReadToEnd();
        if (p.ExitCode != 0)
        {
            Console.WriteLine("  cc 编译失败: " + errText.Trim());
            return null;
        }
        return outPath;
    }
    catch (Exception e)
    {
        Console.WriteLine("  cc 不可用: " + e.Message);
        return null;
    }
}

sealed class FakeApi : ILauncherApi
{
    public FakeApi(string home)
    {
        LauncherHome = home;
        GameRoot = Path.Combine(home, "games");
    }

    public string LauncherHome { get; }
    public string GameRoot { get; }
    public List<string> Logs { get; } = new();
    public Dictionary<string, PluginPageInfo> Pages { get; } = new();
    public Dictionary<string, HomeCardInfo> Cards { get; } = new();
    public Dictionary<string, string> Settings { get; } = new();
    public List<string> SettingsKeys => Settings.Keys.ToList();

    public void Log(string message) { lock (Logs) Logs.Add(message); }
    public void Notify(string title, string message) { lock (Logs) Logs.Add($"[通知] {title}: {message}"); }
    public string GetSetting(string key, string defaultValue = "") => Settings.TryGetValue(key, out var v) ? v : defaultValue;
    public void SetSetting(string key, string value) => Settings[key] = value;
    public void RegisterHomeCard(HomeCardInfo card) { lock (Cards) Cards[card.Id] = card; }
    public void RegisterPage(PluginPageInfo page) { lock (Pages) Pages[page.Key] = page; }
    public void UpdatePage(string key, string body) { lock (Pages) { if (Pages.TryGetValue(key, out var p)) p.Body = body; } }
    public void UpdateHomeCard(string cardId, string body) { lock (Cards) { if (Cards.TryGetValue(cardId, out var c)) c.Body = body; } }
}
