using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaLauncher.Models;
using NovaLauncher.Services;

namespace NovaLauncher.ViewModels;

public sealed partial class ModpacksViewModel : ObservableObject
{
    private string _status = "";
    private bool _busy;

    public ObservableCollection<ModpackInfo> Instances => AppServices.Modpacks.Instances;

    /// <summary>可安装模组的目标版本（全部已下载版本，含整合包与加载器版本）。</summary>
    public ObservableCollection<InstalledVersionInfo> InstalledTargets { get; } = new();

    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public bool Busy { get => _busy; set => SetProperty(ref _busy, value); }

    public ModpacksViewModel()
    {
        AppServices.Modpacks.Refresh();
        RefreshTargets();
    }

    /// <summary>刷新可安装模组的目标版本列表。</summary>
    public void RefreshTargets()
    {
        InstalledTargets.Clear();
        foreach (var t in LoaderService.ScanInstalled()) InstalledTargets.Add(t);
        OnPropertyChanged(nameof(InstalledTargets));
    }

    [RelayCommand]
    private async Task ImportAsync(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath)) { Status = "未选择文件"; return; }
        Busy = true;
        Status = "导入中…";
        try
        {
            var (ok, msg) = await AppServices.Modpacks.ImportAsync(filePath,
                new Progress<string>(s => Status = s));
            Status = msg;
            RefreshTargets();
        }
        catch (Exception e)
        {
            Status = "导入失败: " + e.Message;
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    private void Export(ModpackInfo? m)
    {
        if (m == null) return;
        var outFile = ModpackService.Export(m.VersionId, AppPaths.Exports);
        Status = outFile != null ? $"已导出：{outFile}" : "导出失败（实例目录不存在）";
    }

    [RelayCommand]
    private void OpenDir(ModpackInfo? m)
    {
        if (m == null) return;
        var dir = AppPaths.GameDir(m.VersionId);
        if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }

    [RelayCommand]
    private async Task LaunchAsync(ModpackInfo? m)
    {
        if (m == null) return;
        var acc = SettingsService.S.SelectedAccount;
        if (acc == null) { Status = "请先在账户页选择账户"; return; }
        Busy = true;
        try
        {
            var (ok, err) = await AppServices.Launcher.LaunchAsync(new VersionEntry { Id = m.VersionId }, acc);
            Status = ok ? "整合包已启动" : "启动失败: " + err;
        }
        catch (Exception e)
        {
            Status = "启动失败: " + e.Message;
        }
        finally { Busy = false; }
    }
}
