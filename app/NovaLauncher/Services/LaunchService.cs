using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using NovaLauncher.Kernel;
using NovaLauncher.Models;
using NovaLauncher.PluginApi;

namespace NovaLauncher.Services;

/// <summary>游戏启动编排：确保文件 → 找 Java → 内核构建命令 → 拉起进程。</summary>
public sealed class LaunchService
{
    private readonly VersionService _versions;
    public Process? Proc { get; private set; }
    public bool IsRunning => Proc is { HasExited: false };

    /// <summary>最近一次成功启动的版本（供崩溃诊断/控制台定位日志）。</summary>
    public string? LastVersionId { get; private set; }

    public event Action? Stopped;

    public LaunchService(VersionService versions) => _versions = versions;

    public async Task<(bool ok, string? error)> LaunchAsync(VersionEntry version, GameAccount account)
    {
        if (IsRunning) return (false, "游戏已在运行");
        var id = version.Id;

        try
        {
            LogService.Info($"准备启动 {id}，账户 {account.Name}（{account.TypeLabel}）");

            // 1. 确保文件就绪
            if (!VersionService.IsInstalled(id))
            {
                LogService.Info("游戏文件未安装，开始补全…");
                await _versions.InstallVersionAsync(version);
            }

            // 2. Java
            var java = SettingsService.S.JavaPath;
            if (string.IsNullOrWhiteSpace(java) || !NovaKernel.JavaCheck(java))
                java = JavaService.FindJava();
            if (string.IsNullOrEmpty(java))
                return (false, "未找到 Java 运行时，请到设置中指定 Java 路径，或使用「自动下载 Java」");

            // 内存设置合理性校验（借鉴 HMCL 的内存建议：超过物理内存的 90% 提醒）
            if (SystemInfo.TryGetTotalMemoryMb(out var totalMb) && totalMb > 0
                && SettingsService.S.MemoryMb > (long)(totalMb * 0.9))
            {
                LogService.Warn($"内存分配 {SettingsService.S.MemoryMb} MB 超过物理内存 {totalMb} MB 的 90%，可能导致系统卡顿或启动失败。建议在设置页点击「按推荐值设置」降到推荐内存。");
            }

            // 3. 读取版本 JSON
            var vjText = File.ReadAllText(AppPaths.VersionJson(id));
            var vjNode = JsonNode.Parse(vjText) ?? throw new Exception("版本 JSON 损坏");

            // 4. 构建启动参数（C 内核）
            var req = new JsonObject
            {
                ["versionJson"] = vjNode,
                ["gameDir"] = AppPaths.GameDir(id),
                ["librariesDir"] = AppPaths.Libraries,
                ["assetsDir"] = AppPaths.Assets,
                ["nativesDir"] = AppPaths.NativesDir(id),
                ["clientJar"] = AppPaths.VersionJar(id),
                ["java"] = java,
                ["username"] = account.Name,
                ["uuid"] = account.Uuid,
                ["accessToken"] = account.AccessToken,
                ["userType"] = account.UserType,
                ["versionType"] = "release",
                ["versionName"] = id,
                ["memoryMb"] = SettingsService.S.MemoryMb,
                ["width"] = SettingsService.S.Width,
                ["height"] = SettingsService.S.Height,
            };
            var logsDir = Path.Combine(AppPaths.VersionDir(id), "logs");
            if (Directory.Exists(logsDir))
            {
                var logFile = Directory.GetFiles(logsDir).FirstOrDefault();
                if (logFile != null) req["logConfigPath"] = logFile;
            }
            var extra = new JsonArray();
            foreach (var a in SettingsService.S.ExtraJvmArgs) extra.Add(a);
            req["extraJvmArgs"] = extra;

            var argvJson = NovaKernel.BuildLaunch(req.ToJsonString());
            using var argvDoc = JsonDocument.Parse(argvJson);
            var argv = new List<string>();
            foreach (var a in argvDoc.RootElement.EnumerateArray()) argv.Add(a.GetString() ?? "");

            // 个性化：启动后自动进默认服务器
            if (SettingsService.S.AutoJoinServer && !string.IsNullOrWhiteSpace(SettingsService.S.DefaultServer))
            {
                var (host, port) = SplitServer(SettingsService.S.DefaultServer);
                argv.Add("--server");
                argv.Add(host);
                argv.Add("--port");
                argv.Add(port);
            }

            // 5. 插件事件
            LauncherEvents.RaiseGameLaunching(new LaunchInfo
            {
                VersionId = id,
                AccountName = account.Name,
                MemoryMb = SettingsService.S.MemoryMb,
                GameDir = AppPaths.GameDir(id),
            });

            // 6. 启动进程
            Directory.CreateDirectory(AppPaths.GameDir(id));
            var psi = new ProcessStartInfo
            {
                FileName = java,
                WorkingDirectory = AppPaths.GameDir(id),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = false,
            };
            for (int i = 1; i < argv.Count; i++) psi.ArgumentList.Add(argv[i]);

            LogService.Info("启动命令: " + java + " " + string.Join(" ", argv.GetRange(1, argv.Count - 1)));
            var p = Process.Start(psi);
            if (p == null) return (false, "进程启动失败");
            Proc = p;
            p.OutputDataReceived += (_, e) => { if (e.Data != null) LogService.Append("[游戏] " + e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) LogService.Append("[游戏] " + e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) =>
            {
                LogService.Info($"游戏进程退出，代码 {p.ExitCode}");
                MonitorService.StopIfRunning(p.Id);
                Proc = null;
                // 记录最近游玩时间与当日游玩局数，供主页「游玩于 / 日历」展示
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    SettingsService.S.LastPlayedTimes[id] = now.ToUnixTimeSeconds();
                    var key = now.LocalDateTime.ToString("yyyy-MM-dd");
                    SettingsService.S.PlayLog[key] = SettingsService.S.PlayLog.TryGetValue(key, out var c) ? c + 1 : 1;
                    SettingsService.Save();
                }
                catch { }
                LauncherEvents.RaiseGameLaunched(new LaunchInfo { VersionId = id, AccountName = account.Name, MemoryMb = SettingsService.S.MemoryMb, GameDir = AppPaths.GameDir(id) });
                // 游戏退出后：智能诊断最近日志/崩溃报告，把结论打到控制台（借鉴 PCL2 错误分析）
                try
                {
                    var findings = GameDiagnostics.Analyze(id);
                    if (findings.Count > 0)
                    {
                        LogService.Warn("—— 游戏退出诊断（共 " + findings.Count + " 条，详见控制台顶部面板）——");
                        foreach (var f in findings.Take(3))
                            LogService.Warn(f.Title + "：" + f.Suggestion);
                    }
                    else if (p.ExitCode != 0)
                    {
                        LogService.Info("游戏进程退出，未在日志中发现常见崩溃原因（可在控制台点「分析崩溃日志」手动排查）。");
                    }
                }
                catch { /* 诊断失败不影响主流程 */ }
                Stopped?.Invoke();
            };
            MonitorService.Start(p);
            LastVersionId = id;
            LogService.Info("游戏已启动 ✓");
            return (true, null);
        }
        catch (Exception e)
        {
            LogService.Error("启动失败: " + e.Message);
            return (false, e.Message);
        }
    }

    public void Stop()
    {
        if (Proc is { HasExited: false })
        {
            try
            {
                Proc.Kill(true);
                LogService.Info("已强制结束游戏进程");
            }
            catch (Exception e)
            {
                LogService.Warn("结束进程失败: " + e.Message);
            }
        }
    }

    /// <summary>解析 "host:port" 为 (host, port)，缺省端口按 25565。</summary>
    private static (string host, string port) SplitServer(string address)
    {
        var a = address.Trim();
        var idx = a.LastIndexOf(':');
        if (idx > 0 && int.TryParse(a[(idx + 1)..], out var p) && p > 0 && p <= 65535)
            return (a[..idx], p.ToString());
        return (a, "25565");
    }
}
