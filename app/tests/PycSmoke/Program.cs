using System.Reflection;
using System.Runtime.Loader;
using NovaLauncher.PluginApi;

namespace PycSmoke;

/// <summary>
/// PyCExample 插件冒烟测试：
/// 按主程序 PluginService 的相同加载路径（AssemblyLoadContext.LoadFromAssemblyPath + 反射 ILauncherPlugin）
/// 真实加载插件 dll，触发各操作按钮，验证 C 内核 P/Invoke 调用与 Python3 脚本调用均可运行。
/// 用法：NOVA_KERNEL_LIB=<dylib绝对路径> PycSmoke <PyCExample.dll绝对路径>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var dll = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "PyCExample.dll");
        if (!File.Exists(dll))
        {
            Console.WriteLine("[FAIL] 插件 dll 不存在: " + dll);
            return 1;
        }

        var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(dll));
        var type = asm.GetTypes().First(t =>
            !t.IsAbstract && !t.IsInterface &&
            typeof(ILauncherPlugin).IsAssignableFrom(t) &&
            t.GetConstructor(Type.EmptyTypes) != null);
        var plugin = (ILauncherPlugin)Activator.CreateInstance(type)!;
        var stub = new StubApi();
        plugin.OnLoad(stub);

        Console.WriteLine("== OnLoad ==");
        Console.WriteLine("  Name=" + plugin.Name + " Ver=" + plugin.Version + " Author=" + plugin.Author);
        Console.WriteLine("  Pages=" + stub.Pages.Count + " Cards=" + stub.Cards.Count +
            " Actions=" + stub.Pages.FirstOrDefault()?.Actions.Count);
        Assert(stub.Pages.Count >= 1, "插件页未注册");
        Assert(stub.Cards.Count >= 1, "主页卡片未注册");

        var hello = Path.Combine(stub.LauncherHome, "PyCExample", "scripts", "hello.py");
        var pyUuid = Path.Combine(stub.LauncherHome, "PyCExample", "scripts", "py_uuid.py");
        Assert(File.Exists(hello) && File.Exists(pyUuid), "内嵌脚本未写出到数据目录");
        Console.WriteLine("  脚本已写出: " + Path.GetDirectoryName(hello));

        plugin.OnAction("c_uuid"); Wait(1500);
        Print("c_uuid", stub.LastBody);
        AssertContains(stub.LastBody, "C 内核 nova_offline_uuid", "c_uuid 输出异常");

        plugin.OnAction("py_hello"); Wait(1500);
        Print("py_hello", stub.LastBody);
        AssertContains(stub.LastBody, "Python 说：你好", "py_hello 未输出 Python 文本");

        plugin.OnAction("py_uuid"); Wait(1500);
        Print("py_uuid", stub.LastBody);
        AssertContains(stub.LastBody, "两语言结果一致", "Python 与 C 的离线 UUID 不一致");

        plugin.OnAction("c_http"); Wait(4000);
        Print("c_http", stub.LastBody);

        plugin.OnAction("run_all"); Wait(4000);
        Print("run_all", stub.LastBody);
        AssertContains(stub.LastBody, "== C 内核", "run_all 缺 C 内核段");
        AssertContains(stub.LastBody, "== Python3", "run_all 缺 Python 段");

        Console.WriteLine("SMOKE OK ✓");
        return 0;
    }

    private static void Wait(int ms) => Thread.Sleep(ms);

    private static void Print(string tag, string body)
    {
        Console.WriteLine("== " + tag + " ==");
        Console.WriteLine(string.IsNullOrWhiteSpace(body) ? "(空)" : body);
        Console.WriteLine();
    }

    private static void Assert(bool cond, string what)
    {
        if (!cond) { Console.WriteLine("[FAIL] " + what); Environment.Exit(1); }
    }

    private static void AssertContains(string text, string keyword, string what)
    {
        if (!text.Contains(keyword, StringComparison.Ordinal))
        {
            Console.WriteLine("[FAIL] " + what + "（缺少关键字: " + keyword + "）");
            Console.WriteLine(text);
            Environment.Exit(1);
        }
    }

    /// <summary>模拟 ILauncherApi：记录页面/卡片/正文，设置与目录落在临时路径。</summary>
    private sealed class StubApi : ILauncherApi
    {
        public List<string> Logs { get; } = new();
        public List<HomeCardInfo> Cards { get; } = new();
        public List<PluginPageInfo> Pages { get; } = new();
        public string LastBody { get; private set; } = "";
        public Dictionary<string, string> Settings { get; } = new();
        public string LauncherHome { get; } =
            Path.Combine(Path.GetTempPath(), "pyc-smoke-" + Guid.NewGuid().ToString("N")[..8]);
        public string GameRoot => Path.Combine(LauncherHome, "games");

        public void Log(string message) { Logs.Add(message); Console.WriteLine("  [log] " + message); }
        public void Notify(string title, string message) => Console.WriteLine("  [notify] " + title + " | " + message);
        public string GetSetting(string key, string defaultValue = "")
            => Settings.TryGetValue(key, out var v) ? v : defaultValue;
        public void SetSetting(string key, string value) => Settings[key] = value;
        public void RegisterHomeCard(HomeCardInfo card) => Cards.Add(card);
        public void RegisterPage(PluginPageInfo page) => Pages.Add(page);
        public void UpdatePage(string key, string body) => LastBody = body;
        public void UpdateHomeCard(string cardId, string body) { }
    }
}
