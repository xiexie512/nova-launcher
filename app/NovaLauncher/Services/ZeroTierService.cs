using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace NovaLauncher.Services;

/// <summary>单个 ZeroTier 网络（虚拟局域网）。</summary>
public sealed class ZtNetworkInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public string Type { get; set; } = "";
    public string Device { get; set; } = "";
    public List<string> Addresses { get; set; } = new();
    public string AddressText => Addresses.Count > 0 ? string.Join("、", Addresses.Select(a => a.Replace("/24", ""))) : "未分配";
}

/// <summary>
/// 虚拟局域网集成：驱动现成虚拟局域网软件 ZeroTier（zerotier-cli）。
/// 提供引擎检测、状态读取、加入/离开网络、虚拟 IP 获取与安装包下载引导。
/// </summary>
public static class ZeroTierService
{
    private static string? _cli;

    public static string? CliPath => _cli;
    public static bool IsInstalled => _cli != null;
    public static string? LastError { get; private set; }

    // ---- 按「电脑版本」= 操作系统 + CPU 架构 解析 ZeroTier 安装包 ----
    // ZeroTier 官方只发布两个安装包：
    //   · ZeroTier One.msi —— Windows x64（arm64 Windows 也能以 x64 模式运行）
    //   · ZeroTier One.pkg —— macOS 通用包（Apple Silicon 与 Intel 通吃）
    // 这里把 OS 与架构显式解析出来，用于下载选包、文件名区分与 UI 展示。

    public enum ZtOs { Windows, MacOS, Linux, Other }

    public static ZtOs Os => OperatingSystem.IsWindows() ? ZtOs.Windows
        : OperatingSystem.IsMacOS() ? ZtOs.MacOS
        : OperatingSystem.IsLinux() ? ZtOs.Linux
        : ZtOs.Other;

    public static string OsLabel => Os switch
    {
        ZtOs.Windows => "Windows",
        ZtOs.MacOS => "macOS",
        ZtOs.Linux => "Linux",
        _ => "未知系统",
    };

    public static string ArchLabel => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.X64 => "x64",
        System.Runtime.InteropServices.Architecture.X86 => "x86",
        _ => "unknown",
    };

    public static bool IsArm64 =>
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
        System.Runtime.InteropServices.Architecture.Arm64;

    /// <summary>当前机器应使用的安装包描述。</summary>
    public static (string BundledName, string Url, string FileName) InstallerDescriptor => Os switch
    {
        ZtOs.Windows => ("ZeroTier-One.msi",
            "https://download.zerotier.com/dist/ZeroTier%20One.msi",
            "ZeroTier-One.msi"),
        ZtOs.MacOS => ("ZeroTier-One.pkg",
            "https://download.zerotier.com/dist/ZeroTier%20One.pkg",
            "ZeroTier-One.pkg"),
        ZtOs.Linux => ("",
            "https://www.zerotier.com/download/",
            ""),
        _ => ("", "", ""),
    };

    /// <summary>当前机器对应的官方下载地址（供 UI 展示）。</summary>
    public static string InstallerUrl => InstallerDescriptor.Url;

    /// <summary>当前机器对应的安装包文件名（含架构信息，供下载落地区分）。</summary>
    public static string DownloadFileName =>
        Os == ZtOs.Windows
            ? (IsArm64 ? "ZeroTier-One-arm64.msi" : "ZeroTier-One-x64.msi")
            : Os == ZtOs.MacOS
                ? (IsArm64 ? "ZeroTier-One-arm64.pkg" : "ZeroTier-One-x64.pkg")
                : "ZeroTier-One-linux";

    public static void Detect()
    {
        _cli = null;
        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            candidates.Add(Path.Combine(pf86, "ZeroTier", "One", "zerotier-cli.bat"));
            candidates.Add(Path.Combine(pf86, "ZeroTier", "One", "zerotier-cli.exe"));
            candidates.Add(Path.Combine(pf, "ZeroTier", "One", "zerotier-cli.bat"));
            candidates.Add(Path.Combine(pf, "ZeroTier", "One", "zerotier-cli.exe"));
        }
        else
        {
            candidates.Add("/usr/local/bin/zerotier-cli");
            candidates.Add("/opt/homebrew/bin/zerotier-cli");
            candidates.Add("/Applications/ZeroTier One.app/Contents/MacOS/zerotier-cli");
        }
        foreach (var c in candidates)
            if (File.Exists(c)) { _cli = c; return; }
    }

    /// <summary>执行 zerotier-cli 并返回输出。失败抛异常。</summary>
    public static string RunCli(string args)
    {
        Detect();
        if (_cli == null) throw new Exception("未检测到 ZeroTier，请先安装虚拟局域网软件");
        LastError = null;
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (OperatingSystem.IsWindows())
        {
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("\"" + _cli + "\" " + args);
        }
        else
        {
            psi.FileName = _cli;
            foreach (var a in args.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                psi.ArgumentList.Add(a);
        }
        using var p = Process.Start(psi) ?? throw new Exception("无法启动 zerotier-cli");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(20000)) { p.Kill(); throw new Exception("zerotier-cli 超时"); }
        if (p.ExitCode != 0 && stderr.Length > 0) LastError = stderr.Trim();
        return stdout;
    }

    /// <summary>引擎信息：{ Online, Version, NodeId, Raw }。</summary>
    public static (bool Online, string Version, string NodeId) GetInfo()
    {
        var raw = RunCli("info");
        // 输出形如: 200 info 3bc37a3264 1.16.2 ONLINE
        var parts = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 5 && parts[1] == "info")
        {
            var online = parts.Length > 4 && parts[4].Equals("ONLINE", StringComparison.OrdinalIgnoreCase);
            return (online, parts[3], parts[2]);
        }
        throw new Exception("zerotier-cli info 输出异常: " + raw.Trim());
    }

    /// <summary>已加入的虚拟局域网列表（含分配的虚拟 IP）。</summary>
    public static List<ZtNetworkInfo> GetNetworks()
    {
        var raw = RunCli("listnetworks -j");
        using var doc = JsonDocument.Parse(raw);
        var list = new List<ZtNetworkInfo>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var n = new ZtNetworkInfo
            {
                Id = Get(el, "id"),
                Name = Get(el, "name"),
                Status = Get(el, "status"),
                Type = Get(el, "type"),
                Device = Get(el, "dev"),
            };
            if (el.TryGetProperty("assignedAddresses", out var aa))
                foreach (var a in aa.EnumerateArray())
                    n.Addresses.Add(a.GetString() ?? "");
            list.Add(n);
        }
        return list;
    }

    /// <summary>第一个网络分配的虚拟 IPv4（去掉掩码），无则空。</summary>
    public static string? MyVirtualIp()
    {
        try
        {
            foreach (var n in GetNetworks())
                foreach (var a in n.Addresses)
                    if (a.Contains('.') && !a.StartsWith("169.254"))
                        return a.Split('/')[0];
        }
        catch { }
        return null;
    }

    public static void JoinNetwork(string id)
    {
        id = id.Trim();
        if (id.Length != 16) throw new Exception("网络 ID 应为 16 位十六进制，如 08752e18b1bf9ef8");
        RunCli("join " + id);
    }

    public static void LeaveNetwork(string id) => RunCli("leave " + id);

    /// <summary>
    /// 打开 ZeroTier 安装包（按当前电脑的操作系统 + CPU 架构选择）：
    /// 优先使用随启动器内置的安装包（installers/ 目录），未内置时才联网下载到「下载」目录。
    /// Windows / macOS 直接唤起系统安装向导；Linux 无官方图形安装包，返回官方仓库指引。
    /// </summary>
    public static async Task<string> DownloadAndOpenInstallerAsync()
    {
        var (bundledName, url, _) = InstallerDescriptor;

        // Linux：无单文件 GUI 安装包，给出官方下载页指引
        if (Os == ZtOs.Linux)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
            return url;
        }

        // 1) 内置包：与当前系统匹配的安装包（macOS 带 .pkg、Windows 带 .msi）
        if (bundledName.Length > 0)
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, "installers", bundledName);
            if (File.Exists(bundled))
            {
                LogService.Info($"ZeroTier：使用内置安装包 {bundledName}（{OsLabel} / {ArchLabel}）");
                Process.Start(new ProcessStartInfo { FileName = bundled, UseShellExecute = true });
                return bundled;
            }
        }

        // 2) 联网下载：按 OS+架构 命名落盘，避免跨系统/跨架构混用
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            OperatingSystem.IsWindows() ? "Downloads" : "下载");
        Directory.CreateDirectory(downloads);
        var dest = Path.Combine(downloads, DownloadFileName);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            await using (var fs = File.Create(dest))
                await resp.Content.CopyToAsync(fs);
        }
        LogService.Info($"ZeroTier：已下载 {DownloadFileName}（{OsLabel} / {ArchLabel}）→ {dest}");
        // 打开安装器（macOS 弹系统安装向导；Windows 弹安装程序）
        Process.Start(new ProcessStartInfo { FileName = dest, UseShellExecute = true });
        return dest;
    }

    private static string Get(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";
}
