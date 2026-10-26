using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaLauncher.Models;
using NovaLauncher.Services;

namespace NovaLauncher.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private string _status = "";
    private bool _busy;
    private string _javaStatus = "";

    public int MemoryMb { get => SettingsService.S.MemoryMb; set { SettingsService.S.MemoryMb = value; SettingsService.Save(); } }
    public int Workers { get => SettingsService.S.Workers; set { SettingsService.S.Workers = value; SettingsService.Save(); } }
    public int Width { get => SettingsService.S.Width; set { SettingsService.S.Width = value; SettingsService.Save(); } }
    public int Height { get => SettingsService.S.Height; set { SettingsService.S.Height = value; SettingsService.Save(); } }
    public string JavaPath { get => SettingsService.S.JavaPath; set { SettingsService.S.JavaPath = value; SettingsService.Save(); } }
    public string MicrosoftClientId { get => SettingsService.S.MicrosoftClientId; set { SettingsService.S.MicrosoftClientId = value; SettingsService.Save(); } }
    public string CurseForgeKey { get => SettingsService.S.CurseForgeKey; set { SettingsService.S.CurseForgeKey = value; SettingsService.Save(); } }
    public string AiEndpoint { get => SettingsService.S.AiEndpoint; set { SettingsService.S.AiEndpoint = value; SettingsService.Save(); } }
    public string AiKey { get => SettingsService.S.AiKey; set { SettingsService.S.AiKey = value; SettingsService.Save(); } }
    public string AiModel { get => SettingsService.S.AiModel; set { SettingsService.S.AiModel = value; SettingsService.Save(); } }
    public bool UseMojang { get => SettingsService.S.UseMojang; set { SettingsService.S.UseMojang = value; SettingsService.Save(); } }
    public bool UseBmclapi { get => SettingsService.S.UseBmclapi; set { SettingsService.S.UseBmclapi = value; SettingsService.Save(); } }

    // ===== 个性化 · 外观 =====
    /// <summary>主题：0=跟随系统，1=浅色，2=深色（ComboBox SelectedIndex 直接绑定）。</summary>
    public int Theme { get => SettingsService.S.Theme; set { SettingsService.S.Theme = value; SettingsService.Save(); App.ApplyTheme(); OnPropertyChanged(nameof(ThemeLabel)); } }
    public string ThemeLabel => SettingsService.S.Theme switch { 1 => "浅色", 2 => "深色", _ => "跟随系统" };
    public string AccentColor { get => SettingsService.S.AccentColor; set { SettingsService.S.AccentColor = value; SettingsService.Save(); App.ApplyTheme(); } }

    // ===== 个性化 · 账号与默认服务器 =====
    public string OfflineUsername { get => SettingsService.S.OfflineUsername; set { SettingsService.S.OfflineUsername = value; SettingsService.Save(); } }
    public string DefaultServer { get => SettingsService.S.DefaultServer; set { SettingsService.S.DefaultServer = value; SettingsService.Save(); } }
    public bool AutoJoinServer { get => SettingsService.S.AutoJoinServer; set { SettingsService.S.AutoJoinServer = value; SettingsService.Save(); } }

    // ===== 个性化 · 游戏与版本管理 =====
    public ObservableCollection<string> InstalledVersions { get; } = new();
    public string DefaultVersion { get => SettingsService.S.DefaultVersion; set { SettingsService.S.DefaultVersion = value; SettingsService.Save(); } }

    public ObservableCollection<string> ExtraArgs { get; } = new();

    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public bool Busy { get => _busy; set => SetProperty(ref _busy, value); }
    public string JavaStatus { get => _javaStatus; set => SetProperty(ref _javaStatus, value); }

    public string GameRoot => AppPaths.Games;
    public string KernelVersion => AppServices.KernelVersion;

    // ===== 内存自动推荐（借鉴 HMCL 自动内存分配）=====
    /// <summary>物理内存（MB），探测失败返回 0。</summary>
    public long TotalMemoryMb => SystemInfo.TryGetTotalMemoryMb(out var m) ? m : 0;

    /// <summary>按物理内存推导的推荐游戏内存（MB）。</summary>
    public int RecommendedMemoryMb
    {
        get
        {
            var total = TotalMemoryMb;
            if (total <= 0) return 2048;
            if (total <= 3072) return 1024;   // 3G 老机器
            if (total <= 4096) return 1536;   // 4G
            if (total <= 8192) return 2048;   // 8G（M 系列 MacBook Air 等）
            if (total <= 16384) return 4096;  // 16G
            if (total <= 32768) return 6144;  // 32G
            return 8192;                       // 64G+
        }
    }

    public string MemoryHint => TotalMemoryMb > 0
        ? $"本机物理内存 {TotalMemoryMb} MB，推荐分配 {RecommendedMemoryMb} MB"
        : "未能探测物理内存，推荐值按 8GB 内存估算";

    public bool HasTotalMemory => TotalMemoryMb > 0;

    public SettingsViewModel()
    {
        foreach (var a in SettingsService.S.ExtraJvmArgs) ExtraArgs.Add(a);
        // 已安装版本清单（供「默认版本」选择）
        if (Directory.Exists(AppPaths.Versions))
            foreach (var d in Directory.GetDirectories(AppPaths.Versions))
                InstalledVersions.Add(Path.GetFileName(d));
    }

    [RelayCommand]
    private void DetectJava()
    {
        var j = JavaService.FindJava();
        if (j != null)
        {
            JavaPath = j;
            JavaStatus = "已找到: " + j;
        }
        else
        {
            JavaStatus = "未找到 Java。可点击「下载 Java 21」自动安装。";
        }
    }

    [RelayCommand]
    private async Task DownloadJavaAsync()
    {
        Busy = true;
        JavaStatus = "准备下载 Temurin JDK 21…";
        try
        {
            var j = await JavaService.DownloadJavaAsync(21, new Progress<string>(s => JavaStatus = s));
            JavaStatus = j != null ? "安装完成: " + j : "安装失败";
        }
        catch (Exception e)
        {
            JavaStatus = "下载失败: " + e.Message;
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    private void AddArg(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        ExtraArgs.Add(text.Trim());
        SyncArgs();
    }

    [RelayCommand]
    private void RemoveArg(string? arg)
    {
        if (arg == null) return;
        ExtraArgs.Remove(arg);
        SyncArgs();
    }

    [RelayCommand]
    private void OpenGameRoot()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = AppPaths.Games,
            UseShellExecute = true,
        });
    }

    [RelayCommand]
    private void OpenHomeDir()
    {
        System.IO.Directory.CreateDirectory(AppPaths.Home);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = AppPaths.Home,
            UseShellExecute = true,
        });
    }

    /// <summary>一键把游戏内存设为按物理内存推荐的值。</summary>
    [RelayCommand]
    private void ApplyRecommendedMemory()
    {
        MemoryMb = RecommendedMemoryMb;
        Status = $"内存已设为推荐值 {RecommendedMemoryMb} MB";
        OnPropertyChanged(nameof(MemoryMb));
    }

    /// <summary>一键填入常用 JVM 参数模板（幂等，不会重复添加）。</summary>
    [RelayCommand]
    private void ApplyJvmTemplate()
    {
        var template = new[]
        {
            "-XX:+UseG1GC",
            "-XX:MaxGCPauseMillis=100",
            "-XX:+UseStringDeduplication",
            "-Dfile.encoding=UTF-8",
        };
        var added = 0;
        foreach (var arg in template)
        {
            if (ExtraArgs.Any(a => string.Equals(a, arg, StringComparison.OrdinalIgnoreCase))) continue;
            ExtraArgs.Add(arg);
            added++;
        }
        SyncArgs();
        Status = added > 0 ? $"已填入 {added} 条推荐 JVM 参数（G1GC 低延迟 GC + UTF-8 编码）" : "推荐参数已存在，无需重复添加";
    }

    /// <summary>一键导出诊断信息包（系统信息 + 日志 + 崩溃报告，脱敏打包）。</summary>
    [RelayCommand]
    private void ExportDiagnostics()
    {
        var path = DiagnosticExporter.Export();
        Status = path != null ? "诊断包已导出：" + path : "导出失败，详见控制台日志";
    }

    private void SyncArgs()
    {
        SettingsService.S.ExtraJvmArgs = ExtraArgs.ToList();
        SettingsService.Save();
    }

    [RelayCommand]
    private void Save()
    {
        SyncArgs();
        SettingsService.Save();
        Status = "设置已保存 ✓";
    }
}
