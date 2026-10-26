using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaLauncher.Models;
using NovaLauncher.Services;

namespace NovaLauncher.ViewModels;

/// <summary>
/// 游戏版本：展示所有已下载版本（启动 / 打开目录 / 删除），
/// 并通过「下载版本」对话框（分类 + 加载器 + 光影）下载新版本。
/// </summary>
public sealed partial class VersionsViewModel : ObservableObject
{
    private string _status = "";
    private bool _busy;

    public ObservableCollection<InstalledVersionInfo> Downloaded { get; } = new();

    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public bool Busy { get => _busy; set => SetProperty(ref _busy, value); }
    public bool HasDownloaded => Downloaded.Count > 0;

    public VersionsViewModel(VersionService vs)
    {
        RefreshDownloaded();
    }

    /// <summary>扫描所有已下载版本（含整合包与加载器版本）。</summary>
    [RelayCommand]
    public void RefreshDownloaded()
    {
        Downloaded.Clear();
        foreach (var info in LoaderService.ScanInstalled())
            Downloaded.Add(info);
        OnPropertyChanged(nameof(HasDownloaded));
        Status = Downloaded.Count > 0 ? $"共 {Downloaded.Count} 个已下载版本" : "还没有下载任何版本";
    }

    [RelayCommand]
    private async Task LaunchAsync(string? id)
    {
        if (string.IsNullOrEmpty(id) || Busy) return;
        var acc = SettingsService.S.SelectedAccount;
        if (acc == null) { Status = "请先在账户页选择账户"; return; }
        Busy = true;
        Status = $"启动 {id}…";
        try
        {
            var (ok, err) = await AppServices.Launcher.LaunchAsync(new VersionEntry { Id = id }, acc);
            Status = ok ? "游戏已启动" : "启动失败: " + err;
        }
        catch (Exception e)
        {
            Status = "启动失败: " + e.Message;
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    private void OpenDir(string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        var dir = AppPaths.GameDir(id);
        if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true, WorkingDirectory = dir });
    }

    /// <summary>打开该版本的 mods 目录（方便手动装 mod / 排查冲突）。</summary>
    [RelayCommand]
    private void OpenModsDir(string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        var dir = Path.Combine(AppPaths.GameDir(id), "mods");
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }

    /// <summary>把该版本的完整游戏目录（mods/config/saves 等，不含日志）打包为 zip，便于分享/备份。</summary>
    [RelayCommand]
    private void ExportInstance(string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        try
        {
            var instanceDir = AppPaths.GameDir(id);
            if (!Directory.Exists(instanceDir))
            {
                Status = "该版本还没有游戏目录（尚未启动过）";
                return;
            }
            Directory.CreateDirectory(AppPaths.Exports);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var zipPath = Path.Combine(AppPaths.Exports, $"{id}-instance-{stamp}.zip");
            using var fs = File.Create(zipPath);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
            AddDir(zip, instanceDir, "", exclude: new[] { "logs", "crash-reports" });
            Status = "实例包已导出：" + zipPath;
            LogService.Info("实例包已导出: " + zipPath);
        }
        catch (Exception e)
        {
            Status = "导出失败: " + e.Message;
        }
    }

    private static void AddDir(ZipArchive zip, string dir, string prefix, string[] exclude)
    {
        foreach (var sub in Directory.GetDirectories(dir))
        {
            var name = Path.GetFileName(sub);
            if (exclude.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            AddDir(zip, sub, prefix + name + "/", exclude);
        }
        foreach (var f in Directory.GetFiles(dir))
        {
            try { zip.CreateEntryFromFile(f, prefix + Path.GetFileName(f)); }
            catch { /* 单个文件失败跳过 */ }
        }
    }

    [RelayCommand]
    private void Delete(string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        try
        {
            System.IO.Directory.Delete(AppPaths.VersionDir(id), true);
            Status = $"已删除 {id}";
            RefreshDownloaded();
        }
        catch (Exception e)
        {
            Status = "删除失败: " + e.Message;
        }
    }
}
