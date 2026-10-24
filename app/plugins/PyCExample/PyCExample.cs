using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using NovaLauncher.PluginApi;

namespace PyCExample;

/// <summary>
/// PyCExample：Python + C 实例插件。
/// 演示一个 Nova 插件如何同时集成两种底层语言：
/// · C —— P/Invoke 直接调用启动器自研 C 内核 nova_kernel（版本 / 离线UUID / HTTP / Java 校验）
/// · Python —— 调用本机 python3 执行脚本（参数传递 / 中文输出 / 标准库计算），脚本可热编辑
/// 同一个「离线 UUID」功能分别用 C 内核与 Python 各实现一遍，一键对照，直观展示两语言接入方式。
/// </summary>
public sealed class PyCExamplePlugin : ILauncherPlugin
{
    public string Name => "PyCExample";
    public string Version => "1.0.0";
    public string Author => "Nova";
    public string Description => "Python + C 实例插件：同一插件内演示调用 C 内核(nova_kernel)与本机 Python3";

    private const string PageKey = "pyc-demo";
    private const string CardId = "pyc-card";
    private const string ManifestUrl = "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";

    private ILauncherApi _api = null!;
    private readonly BlockingCollection<Func<string>> _jobs = new();
    private Thread? _worker;

    public void OnLoad(ILauncherApi api)
    {
        _api = api;
        EnsureScripts();

        api.RegisterPage(new PluginPageInfo
        {
            Key = PageKey,
            Title = "Python+C 实例",
            Icon = "🐍",
            Body = StatusText(),
            Actions = new List<PluginActionInfo>
            {
                new() { Id = "refresh", Label = "刷新状态", Icon = "🔄" },
                new() { Id = "c_uuid", Label = "C内核·离线UUID", Icon = "🧬" },
                new() { Id = "c_http", Label = "C内核·HTTP请求", Icon = "🌐" },
                new() { Id = "py_hello", Label = "Python·hello.py", Icon = "💬" },
                new() { Id = "py_uuid", Label = "Python·离线UUID", Icon = "🐍" },
                new() { Id = "run_all", Label = "一键全跑", Icon = "⚡" },
            },
        });

        api.RegisterHomeCard(new HomeCardInfo
        {
            Id = CardId,
            Title = "Python + C 实例插件",
            Icon = "🐍",
            Body = "同一插件内调用 C 内核(nova_kernel)与本机 Python3，侧栏「Python+C 实例」页一键对照。",
        });

        // 订阅启动事件：游戏启动后用 Python 生成祝贺语（演示事件订阅 + 脚本调用，后台线程执行）
        LauncherEvents.GameLaunched += info => Enqueue(() =>
        {
            var msg = PyRunner.RunScript(Script("hello.py"),
                $"\"{DemoName}\" \"{info.VersionId} 已启动，Python 来祝贺啦！\"");
            _api.Notify("Python 启动祝贺", msg);
            return "已用 Python 生成启动祝贺";
        });

        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "PyCExample" };
        _worker.Start();
        Refresh();
        _api.Log($"PyCExample 已加载：C 内核 v{PycKernel.Version}；Python {PyRunner.PythonVersion}（{PyRunner.PythonPath}）");
    }

    public void OnUnload()
    {
        try { _jobs.CompleteAdding(); } catch { }
    }

    // ---------- 路径与脚本 ----------

    /// <summary>演示玩家名，可通过启动器设置项 PyCExample.demoName 修改。</summary>
    private string DemoName => _api.GetSetting("demoName", "Steve");

    private string DataDir => Path.Combine(_api.LauncherHome, "PyCExample");
    private string ScriptsDir => Path.Combine(DataDir, "scripts");
    private string Script(string name) => Path.Combine(ScriptsDir, name);

    /// <summary>把内嵌的 .py 脚本首次写出到数据目录（用户可直接编辑，已存在不覆盖）。</summary>
    private void EnsureScripts()
    {
        try
        {
            Directory.CreateDirectory(ScriptsDir);
            WriteEmbedded(".scripts.hello.py", "hello.py");
            WriteEmbedded(".scripts.py_uuid.py", "py_uuid.py");
        }
        catch (Exception e)
        {
            _api.Log("脚本写出失败: " + e.Message);
        }
    }

    private void WriteEmbedded(string endsWith, string fileName)
    {
        var asm = typeof(PyCExamplePlugin).Assembly;
        var res = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(endsWith, StringComparison.Ordinal));
        if (res == null) return;
        var target = Path.Combine(ScriptsDir, fileName);
        if (File.Exists(target)) return;
        using var s = asm.GetManifestResourceStream(res);
        if (s == null) return;
        using var fs = File.Create(target);
        s.CopyTo(fs);
    }

    // ---------- 状态与队列 ----------

    private string StatusText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("C 内核（nova_kernel）");
        sb.AppendLine("  版本：" + PycKernel.Version);
        sb.AppendLine("  库文件：" + (PycKernel.KernelLibPath == "" ? "未定位到 dylib/dll" : PycKernel.KernelLibPath));
        sb.AppendLine();
        sb.AppendLine("Python3");
        sb.AppendLine("  解释器：" + PyRunner.PythonPath);
        sb.AppendLine("  版本：" + PyRunner.PythonVersion);
        sb.AppendLine();
        sb.AppendLine("脚本目录：" + ScriptsDir);
        sb.AppendLine("  演示玩家名：" + DemoName + "（设置项 PyCExample.demoName）");
        return sb.ToString();
    }

    private void SetStatus(string text)
    {
        try
        {
            _api.UpdatePage(PageKey, text);
            _api.UpdateHomeCard(CardId, "最近：\n" + LastLines(text, 3));
        }
        catch { }
    }

    private static string LastLines(string text, int n)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join("\n", lines.TakeLast(n));
    }

    private void Refresh() => Enqueue(() =>
    {
        return StatusText();
    });

    private void Enqueue(Func<string> job)
    {
        try { _jobs.Add(job); } catch { }
    }

    private void WorkerLoop()
    {
        foreach (var job in _jobs.GetConsumingEnumerable())
        {
            try
            {
                var msg = job();
                SetStatus(StatusText() + "\n--- 最近操作 ---\n" + msg);
                _api.Log(msg);
            }
            catch (Exception e)
            {
                SetStatus(StatusText() + "\n--- 最近操作 ---\n[失败] " + e.Message);
                _api.Log("操作失败: " + e.Message);
            }
        }
    }

    // ---------- 按钮分发 ----------

    public void OnAction(string actionId)
    {
        switch (actionId)
        {
            case "refresh": Refresh(); break;
            case "c_uuid": Enqueue(TestCUuid); break;
            case "c_http": Enqueue(TestCHttp); break;
            case "py_hello": Enqueue(TestPyHello); break;
            case "py_uuid": Enqueue(TestPyUuid); break;
            case "run_all": Enqueue(RunAll); break;
        }
    }

    // ---------- C 内核演示 ----------

    private string TestCUuid()
    {
        var name = DemoName;
        var uuid = PycKernel.OfflineUuid(name);
        var msg = $"C 内核 nova_offline_uuid(\"{name}\") = {uuid}";
        return msg;
    }

    private string TestCHttp()
    {
        try
        {
            var body = PycKernel.HttpGet(ManifestUrl, 15000);
            using var doc = JsonDocument.Parse(body);
            var latest = doc.RootElement.GetProperty("latest");
            var release = latest.GetProperty("release").GetString();
            var snapshot = latest.GetProperty("snapshot").GetString();
            var head = body.Length > 200 ? body[..200] + "…" : body;
            return $"C 内核 nova_http_get 成功（Mojang 版本清单）：\n最新正式版 {release} / 快照版 {snapshot}\n响应 {body.Length} 字节，开头：{head}";
        }
        catch (Exception e)
        {
            return "C 内核 HTTP 请求失败：" + e.Message;
        }
    }

    // ---------- Python 演示 ----------

    private string TestPyHello()
    {
        var msg = PyRunner.RunScript(Script("hello.py"), $"\"{DemoName}\" \"你好，这是 Python 脚本的输出\"");
        return "Python · hello.py：\n" + msg;
    }

    private string TestPyUuid()
    {
        var name = DemoName;
        var py = PyRunner.RunScript(Script("py_uuid.py"), $"\"{name}\"").Trim();
        string c;
        try { c = PycKernel.OfflineUuid(name).Trim(); }
        catch (Exception e) { c = "（C 内核不可用: " + e.Message + "）"; }
        var same = string.Equals(py, c, StringComparison.Ordinal) ? "✔ 两语言结果一致" : "✘ 两语言结果不一致";
        return $"同一功能，两种语言实现对比（离线 UUID）：\nPython 计算：{py}\nC 内核计算：{c}\n{same}";
    }

    // ---------- 一键全跑 ----------

    private string RunAll()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== C 内核（nova_kernel，P/Invoke） ==");
        sb.AppendLine("版本：" + PycKernel.Version);
        try { sb.AppendLine($"离线UUID({DemoName})：" + PycKernel.OfflineUuid(DemoName)); }
        catch (Exception e) { sb.AppendLine("离线UUID 失败：" + e.Message); }
        try
        {
            var body = PycKernel.HttpGet(ManifestUrl, 15000);
            using var doc = JsonDocument.Parse(body);
            sb.AppendLine("HTTP Mojang 清单：最新正式版 " + doc.RootElement.GetProperty("latest").GetProperty("release").GetString());
        }
        catch (Exception e) { sb.AppendLine("HTTP 请求失败：" + e.Message); }
        sb.AppendLine();
        sb.AppendLine("== Python3（python3 script.py） ==");
        sb.AppendLine("解释器：" + PyRunner.PythonPath + " / " + PyRunner.PythonVersion);
        sb.AppendLine("hello.py：" + PyRunner.RunScript(Script("hello.py"), $"\"{DemoName}\" \"Python 和 C 联动成功！\"").Replace("\n", " ⏎ "));
        sb.AppendLine("py_uuid.py：" + PyRunner.RunScript(Script("py_uuid.py"), $"\"{DemoName}\"").Trim());
        return sb.ToString();
    }
}
