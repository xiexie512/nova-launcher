using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaLauncher.Models;
using NovaLauncher.Services;

namespace NovaLauncher.ViewModels;

/// <summary>
/// 模组搜索与安装：搜索 Modrinth、按版本/加载器筛选、一键安装到选定版本的 mods 目录。
/// </summary>
public sealed partial class ModSearchViewModel : ObservableObject
{
    private string _query = "";
    private string _selectedMcVersion = "";
    private string _selectedLoader = "";
    private string _selectedVersionId = "";
    private string _status = "";
    private bool _busy;

    public ObservableCollection<ModrinthItem> Results { get; } = new();
    public ObservableCollection<string> InstalledVersions { get; } = new();
    public ObservableCollection<string> McVersions { get; } = new();
    public ObservableCollection<string> Loaders { get; } = new() { "", "fabric", "quilt", "forge", "neoforge" };

    public string Query { get => _query; set => SetProperty(ref _query, value); }
    public string SelectedMcVersion { get => _selectedMcVersion; set => SetProperty(ref _selectedMcVersion, value); }
    public string SelectedLoader { get => _selectedLoader; set => SetProperty(ref _selectedLoader, value); }
    public string SelectedVersionId { get => _selectedVersionId; set => SetProperty(ref _selectedVersionId, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public bool Busy { get => _busy; set => SetProperty(ref _busy, value); }

    public ModSearchViewModel()
    {
        RefreshInstalledVersions();
        LoadKnownMcVersions();
    }

    /// <summary>刷新已安装版本列表（作为安装目标）。</summary>
    [RelayCommand]
    public void RefreshInstalledVersions()
    {
        InstalledVersions.Clear();
        foreach (var info in LoaderService.ScanInstalled())
            InstalledVersions.Add(info.Id);
        if (InstalledVersions.Count > 0 && string.IsNullOrEmpty(SelectedVersionId))
            SelectedVersionId = InstalledVersions[0];
    }

    /// <summary>加载已知的 MC 版本（从清单获取，做筛选下拉用）。</summary>
    private async void LoadKnownMcVersions()
    {
        try
        {
            var list = await VersionService.S.FetchManifestAsync();
            var versions = list
                .Where(v => v.Type == "release")
                .Select(v => v.Id)
                .Distinct()
                .OrderByDescending(v => v, new VersionComparer())
                .ToList();
            McVersions.Clear();
            McVersions.Add(""); // 不限制
            foreach (var v in versions) McVersions.Add(v);
        }
        catch { /* 忽略，下拉留空即可 */ }
    }

    /// <summary>搜索模组。</summary>
    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(Query)) { Status = "请输入搜索关键词"; return; }
        if (string.IsNullOrEmpty(SelectedVersionId)) { Status = "请先选择一个已安装版本作为安装目标"; return; }

        Busy = true;
        Status = "搜索中…";
        Results.Clear();

        try
        {
            var list = await ModrinthClient.SearchModsAsync(Query, SelectedMcVersion, SelectedLoader, 30);
            foreach (var m in list) Results.Add(m);
            Status = $"找到 {list.Count} 个模组";
        }
        catch (Exception e)
        {
            Status = "搜索失败: " + e.Message;
        }
        finally { Busy = false; }
    }

    /// <summary>安装选中的模组到目标版本的 mods 目录。</summary>
    [RelayCommand]
    private async Task InstallAsync(ModrinthItem? item)
    {
        if (item == null) return;
        if (string.IsNullOrEmpty(SelectedVersionId)) { Status = "请选择安装目标版本"; return; }

        var modsDir = Path.Combine(AppPaths.GameDir(SelectedVersionId), "mods");
        Directory.CreateDirectory(modsDir);
        var dest = Path.Combine(modsDir, item.Slug + ".jar");

        // 若已存在同名文件，跳过下载
        if (File.Exists(dest))
        {
            Status = $"「{item.Title}」已存在，跳过下载";
            return;
        }

        Busy = true;
        Status = $"正在获取「{item.Title}」下载链接…";

        try
        {
            var file = await ModrinthClient.GetLatestFileAsync(item.ProjectId, SelectedMcVersion, SelectedLoader, ".jar");
            Status = $"正在下载「{file.FileName}」…";
            await ModrinthClient.DownloadAsync(file.Url, dest, p => Status = p);
            Status = $"✅ 「{item.Title}」安装完成 → {SelectedVersionId}/mods/";
        }
        catch (Exception e)
        {
            Status = "安装失败: " + e.Message;
        }
        finally { Busy = false; }
    }

    /// <summary>打开目标版本的 mods 目录。</summary>
    [RelayCommand]
    private void OpenModsDir()
    {
        if (string.IsNullOrEmpty(SelectedVersionId)) return;
        var dir = Path.Combine(AppPaths.GameDir(SelectedVersionId), "mods");
        Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }

    /// <summary>按 MC 版本号排序用的比较器（粗略）。</summary>
    private sealed class VersionComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            if (x == y) return 0;
            if (x == null) return 1;
            if (y == null) return -1;
            var px = x.Split('.').Select(int.Parse).ToArray();
            var py = y.Split('.').Select(int.Parse).ToArray();
            for (int i = 0; i < Math.Max(px.Length, py.Length); i++)
            {
                int vx = i < px.Length ? px[i] : 0;
                int vy = i < py.Length ? py[i] : 0;
                if (vx != vy) return vy.CompareTo(vx); // 降序
            }
            return 0;
        }
    }
}