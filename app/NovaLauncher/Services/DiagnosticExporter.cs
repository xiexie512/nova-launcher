using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NovaLauncher.Services;

/// <summary>
/// 一键导出诊断信息包：把系统信息、启动器日志、游戏日志、崩溃报告、版本清单打包成 zip，
/// 方便发到群里/Issues 排查问题。借鉴 HMCL「导出诊断信息」。
/// </summary>
public static class DiagnosticExporter
{
    /// <summary>导出诊断包到 Exports 目录，返回 zip 完整路径；失败返回 null。</summary>
    public static string? Export()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Exports);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var zipPath = Path.Combine(AppPaths.Exports, $"NovaLauncher-diagnostic-{stamp}.zip");

            using var fs = File.Create(zipPath);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

            AddEntry(zip, "system.txt", BuildSystemInfo());
            AddEntry(zip, "settings.txt", BuildSettingsText());
            AddEntry(zip, "launcher-log.txt", string.Join(Environment.NewLine, LogService.Lines));
            AddEntry(zip, "versions.txt", BuildVersionsText());
            AddEntry(zip, "plugins.txt", BuildPluginsText());

            // 每个已装版本的游戏日志 + 崩溃报告
            if (Directory.Exists(AppPaths.Versions))
            {
                foreach (var dir in Directory.GetDirectories(AppPaths.Versions))
                {
                    var id = Path.GetFileName(dir);
                    var logsDir = Path.Combine(dir, "logs");
                    if (Directory.Exists(logsDir))
                    {
                        foreach (var f in Directory.GetFiles(logsDir, "*.log"))
                        {
                            AddFileEntry(zip, $"games/{id}/logs/{Path.GetFileName(f)}", f);
                        }
                    }
                    var crashDir = Path.Combine(dir, "crash-reports");
                    if (Directory.Exists(crashDir))
                    {
                        foreach (var f in Directory.GetFiles(crashDir))
                        {
                            AddFileEntry(zip, $"games/{id}/crash-reports/{Path.GetFileName(f)}", f);
                        }
                    }
                }
            }

            return zipPath;
        }
        catch (Exception e)
        {
            LogService.Error("导出诊断信息失败: " + e.Message);
            return null;
        }
    }

    // ===== 内容构建 =====

    private static string BuildSystemInfo()
    {
        SystemInfo.TryGetTotalMemoryMb(out var totalMb);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("NovaLauncher 诊断信息");
        sb.AppendLine("生成时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("操作系统: " + RuntimeInformation.OSDescription);
        sb.AppendLine("架构: " + RuntimeInformation.ProcessArchitecture);
        sb.AppendLine(".NET 运行时: " + RuntimeInformation.FrameworkDescription);
        sb.AppendLine("物理内存: " + (totalMb > 0 ? totalMb + " MB" : "未知"));
        sb.AppendLine("启动器内核: v" + AppServices.KernelVersion);
        sb.AppendLine("数据目录: " + AppPaths.Home);
        return sb.ToString();
    }

    private static string BuildSettingsText()
    {
        try
        {
            var json = JsonNode.Parse(File.ReadAllText(AppPaths.SettingsFile));
            if (json != null) MaskSecrets(json);
            return json?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "(空)";
        }
        catch
        {
            return "(设置文件读取失败)";
        }
    }

    /// <summary>把敏感字段打码后再输出。</summary>
    private static void MaskSecrets(JsonNode node)
    {
        foreach (var key in new[] { "AiKey", "CurseForgeKey", "MicrosoftClientId", "NovaNetLastRoomPassword", "NovaNetLastRoomCode" })
            if (node[key] is JsonValue v) node[key] = "***";
        if (node["Accounts"] is JsonArray accounts)
        {
            foreach (var acc in accounts)
            {
                if (acc?["AccessToken"] is JsonValue t) acc["AccessToken"] = "***";
                if (acc?["ClientToken"] is JsonValue c) acc["ClientToken"] = "***";
            }
        }
    }

    private static string BuildVersionsText()
    {
        var sb = new System.Text.StringBuilder();
        if (!Directory.Exists(AppPaths.Versions))
        {
            sb.AppendLine("（无已安装版本）");
            return sb.ToString();
        }
        foreach (var dir in Directory.GetDirectories(AppPaths.Versions))
        {
            var id = Path.GetFileName(dir);
            var hasCrash = Directory.Exists(Path.Combine(dir, "crash-reports"))
                           && Directory.GetFiles(Path.Combine(dir, "crash-reports")).Length > 0;
            sb.AppendLine($"{id}\t{(hasCrash ? "有崩溃报告" : "")}");
        }
        return sb.ToString();
    }

    private static string BuildPluginsText()
    {
        if (!Directory.Exists(AppPaths.Plugins)) return "（无插件）";
        var files = Directory.GetFiles(AppPaths.Plugins)
            .Select(f => $"{Path.GetFileName(f)}\t{new FileInfo(f).Length} bytes");
        return string.Join(Environment.NewLine, files);
    }

    private static void AddEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var w = new StreamWriter(entry.Open());
        w.Write(content);
    }

    private static void AddFileEntry(ZipArchive zip, string name, string filePath)
    {
        try
        {
            zip.CreateEntryFromFile(filePath, name);
        }
        catch { /* 单个文件失败不影响整体 */ }
    }
}

/// <summary>跨平台系统信息（内存探测：macOS 用 sysctl，Windows 用 GlobalMemoryStatusEx）。</summary>
public static class SystemInfo
{
    public static bool TryGetTotalMemoryMb(out long totalMb)
    {
        totalMb = 0;
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var psi = new System.Diagnostics.ProcessStartInfo("/usr/sbin/sysctl", "-n hw.memsize")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) return false;
                var line = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(3000);
                if (long.TryParse(line, out var bytes) && bytes > 0) totalMb = bytes / 1024 / 1024;
                return totalMb > 0;
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
                if (GlobalMemoryStatusEx(ref status))
                {
                    totalMb = (long)(status.ullTotalPhys / 1024 / 1024);
                    return totalMb > 0;
                }
            }
        }
        catch { }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);
}
