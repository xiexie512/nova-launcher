using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace NovaLauncher.Services;

/// <summary>
/// 游戏崩溃智能诊断：扫描游戏日志与崩溃报告，按常见原因规则识别并给出可操作建议。
/// 借鉴 PCL2「错误分析」与 HMCL「诊断信息」的体验：崩溃后直接告诉用户原因和怎么修。
/// </summary>
public static class GameDiagnostics
{
    public sealed record Finding(string Level, string Title, string Detail, string Suggestion);

    private sealed record Rule(Regex Pattern, string Title, string Suggestion);

    private static readonly Rule[] Rules =
    {
        new(new Regex(@"OutOfMemoryError|Out of memory|GC overhead limit exceeded", RegexOptions.IgnoreCase),
            "内存不足（OutOfMemory）",
            "游戏申请的内存超过了给 Java 分配的上限，或系统内存不足。若已分配 6GB 以上仍报 OOM，通常是某个 mod 泄漏内存或 mods 过多。"),
        new(new Regex(@"UnsupportedClassVersionError|class file version|unsupported major\.minor", RegexOptions.IgnoreCase),
            "Java 版本过旧",
            "游戏（或其 mod）需要更高版本的 Java。Nova 默认使用 Java 21，请在设置页用「下载 Java 21」安装，或手动指定最新 Java 路径。"),
        new(new Regex(@"ClassNotFoundException|NoClassDefFoundError", RegexOptions.IgnoreCase),
            "缺失类 / mod 损坏",
            "某个 mod 缺失依赖或被删了关键文件。请到 mods 目录检查最近添加的 mod，或重装对应 mod（常见于前置库缺失）。"),
        new(new Regex(@"ModLoadingException|ModResolutionException|Mod .* requires|missing or unsupported mandatory dependencies|Could not find version|Unknown mod", RegexOptions.IgnoreCase),
            "mod 依赖或冲突",
            "有 mod 缺少前置或互相冲突（Fabric/Quilt 会明确写出缺失的 mod id）。按日志提示补装前置，或移除冲突 mod。"),
        new(new Regex(@"Couldn't create window|Pixel format|OpenGL|GLFW|LWJGLException|IllegalStateException: GLFW|Failed to load LWJGL", RegexOptions.IgnoreCase),
            "图形环境问题",
            "Java 无法创建图形窗口（OpenGL/LWJGL 初始化失败）。请更新显卡驱动；macOS 上确认系统允许「低功耗/集成显卡」正常初始化；若在虚拟机/远程桌面中运行，请改用有 GPU 直通的环境。"),
        new(new Regex(@"InvalidSignatureException|SignatureException|校验失败|hash.*mismatch|NoSuchAlgorithmException", RegexOptions.IgnoreCase),
            "游戏文件损坏",
            "游戏核心文件校验失败（下载中断或磁盘损坏）。请在版本页删除该版本后重新下载安装。"),
        new(new Regex(@"Connection refused|UnknownHostException|SocketException|ConnectException|Read timed out|timeout", RegexOptions.IgnoreCase),
            "网络连接失败",
            "游戏访问服务器/资源地址超时或拒绝连接。请检查网络；正版登录失败多为微软服务不可达，可稍后重试或切换网络。"),
        new(new Regex(@"Mixin apply failed|Mixin.*failed|TransformerException", RegexOptions.IgnoreCase),
            "mod 兼容问题（Mixin）",
            "某个 mod 注入失败，通常是 mod 与当前 Minecraft/加载器版本不兼容。请更新或移除日志中指出的 mod。"),
        new(new Regex(@"Access is denied|Permission denied|Read-only file system|No space left on device", RegexOptions.IgnoreCase),
            "文件权限或磁盘不足",
            "游戏目录不可写或磁盘已满。请检查数据目录权限，清理磁盘空间后重试。"),
        new(new Regex(@"Unresolved compilation problem|compilation problem|Syntax error in|Command parse error", RegexOptions.IgnoreCase),
            "脚本/数据包编译错误",
            "游戏内脚本或数据包存在编译错误，请按日志中的文件与行号修复后重进。"),
    };

    /// <summary>分析某个已安装版本：扫描 latest.log 与 crash-reports 下最新的崩溃报告。</summary>
    public static List<Finding> Analyze(string versionId)
    {
        var findings = new List<Finding>();
        var versionDir = AppPaths.VersionDir(versionId);

        // 1. 最新崩溃报告（crash-reports/*.txt，按时间倒序取最新）
        var crashDir = Path.Combine(versionDir, "crash-reports");
        if (Directory.Exists(crashDir))
        {
            var newest = Directory.GetFiles(crashDir, "*.txt")
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .FirstOrDefault();
            if (newest != null)
            {
                try
                {
                    var text = File.ReadAllText(newest);
                    var source = "crash-reports/" + Path.GetFileName(newest);
                    findings.AddRange(Match(text, source, "崩溃报告"));
                }
                catch { /* 读取失败跳过 */ }
            }
        }

        // 2. 游戏主日志 latest.log（避免与崩溃报告重复轰炸，日志类原因只取一条）
        var logDir = Path.Combine(versionDir, "logs");
        if (Directory.Exists(logDir))
        {
            var latest = Path.Combine(logDir, "latest.log");
            if (File.Exists(latest))
            {
                try
                {
                    var text = ReadTail(File.ReadAllText(latest), 4000);
                    var seen = new HashSet<string>(findings.Select(f => f.Title));
                    foreach (var f in Match(text, "logs/latest.log", "游戏日志"))
                    {
                        if (seen.Add(f.Title)) findings.Add(f);
                    }
                }
                catch { /* 读取失败跳过 */ }
            }
        }

        return findings;
    }

    /// <summary>对一段日志文本做规则匹配，输出诊断结论。</summary>
    public static List<Finding> Match(string text, string source, string sourceLabel)
    {
        var result = new List<Finding>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (var rule in Rules)
        {
            var m = rule.Pattern.Match(text);
            if (!m.Success) continue;
            // 截取命中行上下文，帮助定位
            var line = ExtractLine(text, m.Index);
            result.Add(new Finding("error", rule.Title,
                $"{sourceLabel}：{Truncate(line, 160)}",
                rule.Suggestion));
        }
        return result;
    }

    /// <summary>游戏最近一次是否正常退出（用于在控制台提示「上次崩溃」）。</summary>
    public static bool HasCrash(string versionId)
    {
        var crashDir = Path.Combine(AppPaths.VersionDir(versionId), "crash-reports");
        return Directory.Exists(crashDir) && Directory.GetFiles(crashDir, "*.txt").Length > 0;
    }

    private static string ExtractLine(string text, int index)
    {
        var start = text.LastIndexOf('\n', Math.Max(0, index - 1));
        var end = text.IndexOf('\n', index);
        if (start < 0) start = 0; else start += 1;
        if (end < 0) end = text.Length;
        return text[start..end].Trim();
    }

    private static string ReadTail(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        return text[^maxChars..];
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
