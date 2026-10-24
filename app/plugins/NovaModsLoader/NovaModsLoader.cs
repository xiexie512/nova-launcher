using System.Collections.Concurrent;
using System.Text;
using NovaLauncher.PluginApi;

namespace NovaModsLoader;

/// <summary>
/// NovaModsLoader：为 Nova 启动器提供「4 大模组加载器 + Modrinth 下载 + 启动器反编译分析」。
/// 通过插件页面的操作按钮交互；所有耗时任务在后台线程执行，页面正文实时刷新进度。
/// </summary>
public sealed class NovaModsLoader : ILauncherPlugin
{
    public string Name => "NovaModsLoader";
    public string Version => "1.0.0";
    public string Author => "Nova";
    public string Description => "4 大模组加载器（Fabric/Quilt/Forge/NeoForge）+ 原版 Modrinth 下载 + 启动器反编译分析";

    private ILauncherApi _api = null!;
    private readonly BlockingCollection<Func<string>> _jobs = new();
    private Thread? _worker;

    public void OnLoad(ILauncherApi api)
    {
        _api = api;
        var mc = CurrentMc();

        api.RegisterPage(new PluginPageInfo
        {
            Key = "nova-mods",
            Title = "模组加载器",
            Icon = "🧱",
            Body = StatusText(),
            Actions = new List<PluginActionInfo>
            {
                new() { Id = "install_fabric", Label = "安装 Fabric", Icon = "🧶" },
                new() { Id = "install_quilt", Label = "安装 Quilt", Icon = "🧵" },
                new() { Id = "install_forge", Label = "安装 Forge", Icon = "⚒️" },
                new() { Id = "install_neoforge", Label = "安装 NeoForge", Icon = "🔥" },
                new() { Id = "refresh", Label = "刷新状态", Icon = "🔄" },
                new() { Id = "modrinth_search", Label = "Modrinth 搜索模组", Icon = "🔍" },
                new() { Id = "modrinth_download", Label = "下载上次搜索的模组", Icon = "⬇️" },
                new() { Id = "decompile", Label = "反编译启动器", Icon = "🛠️" },
            },
        });

        api.RegisterHomeCard(new HomeCardInfo
        {
            Id = "nova-mods-card",
            Title = "模组加载器",
            Icon = "🧱",
            Body = "4 大加载器 + Modrinth 下载 + 启动器反编译。点击侧栏「模组加载器」页操作。",
        });

        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "NovaModsLoader" };
        _worker.Start();
        Refresh();
        api.Log($"模组加载器已载入，默认 MC 版本 {mc}（可在设置改插件键 mcVersion）");
    }

    public void OnUnload()
    {
        try { _jobs.CompleteAdding(); } catch { }
    }

    // ---------- 工具 ----------

    private string CurrentMc() => _api.GetSetting("mcVersion", "1.21.1");

    private string VersionsDir => Path.Combine(_api.GameRoot, "versions");

    private string ModsDirFor(string mc) => Path.Combine(_api.GameRoot, "versions", $"nova-{mc}", "instance", "mods");

    private void EnsureDirs()
    {
        var d = Path.Combine(_api.GameRoot, "versions", $"nova-{CurrentMc()}", "instance", "mods");
        Directory.CreateDirectory(d);
        Directory.CreateDirectory(Path.Combine(_api.LauncherHome, "exports"));
    }

    /// <summary>当前插件页/卡片正文：已安装的加载器 + 说明。</summary>
    private string StatusText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("MC 版本：" + CurrentMc() + "（设置项 NovaModsLoader.mcVersion）");
        sb.AppendLine();
        sb.AppendLine("已安装的加载器版本：");
        var any = false;
        foreach (var id in new[] { "fabric", "quilt", "forge", "neoforge" })
        {
            var dir = Path.Combine(VersionsDir, id);
            if (Directory.Exists(dir) && Directory.GetFiles(dir, "*.json").Length > 0)
            {
                sb.AppendLine($"  ✔ {id}  ✓");
                any = true;
            }
        }
        if (!any) sb.AppendLine("  暂无，请点击上方按钮安装。");
        sb.AppendLine();
        sb.AppendLine("Modrinth：使用官方 api.modrinth.com。");
        sb.AppendLine("反编译：反射分析 NovaLauncher 启动器并导出报告到 exports/。");
        return sb.ToString();
    }

    private void SetStatus(string text)
    {
        try
        {
            _api.UpdatePage("nova-mods", text);
            _api.UpdateHomeCard("nova-mods-card", "最近：\n" + LastLines(text, 3));
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
        SetStatus(StatusText());
        return "已刷新";
    });

    /// <summary>把任务压入后台队列（每次只跑一个，避免并发冲突）。</summary>
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
            case "install_fabric": InstallFabric(); break;
            case "install_quilt": InstallQuilt(); break;
            case "install_forge": InstallForge(); break;
            case "install_neoforge": InstallNeoForge(); break;
            case "refresh": Refresh(); break;
            case "modrinth_search": ModrinthSearch(); break;
            case "modrinth_download": ModrinthDownloadLast(); break;
            case "decompile": Decompile(); break;
        }
    }

    private void InstallFabric() => Enqueue(() => LoaderInstaller.InstallFabric(_api, SetStatus));
    private void InstallQuilt() => Enqueue(() => LoaderInstaller.InstallQuilt(_api, SetStatus));
    private void InstallForge() => Enqueue(() => LoaderInstaller.InstallForge(_api, SetStatus));
    private void InstallNeoForge() => Enqueue(() => LoaderInstaller.InstallNeoForge(_api, SetStatus));
    private void ModrinthSearch() => Enqueue(() => ModrinthApi.SearchAndRemember(_api, SetStatus));
    private void ModrinthDownloadLast() => Enqueue(() => ModrinthApi.DownloadLast(_api, SetStatus));
    private void Decompile() => Enqueue(() => LauncherAnalyzer.Run(_api, SetStatus));
}
