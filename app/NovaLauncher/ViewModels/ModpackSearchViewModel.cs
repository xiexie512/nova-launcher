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
/// 整合包搜索与导入：搜索 Modrinth 整合包、按版本/加载器筛选、一键导入为可启动实例。
/// </summary>
public sealed partial class ModpackSearchViewModel : ObservableObject
{
    private string _query = "";
    private string _selectedMcVersion = "";
    private string _selectedLoader = "";
    private string _status = "";
    private bool _busy;

    public ObservableCollection<ModrinthItem> Results { get; } = new();
    public ObservableCollection<string> McVersions { get; } = new();
    public ObservableCollection<string> Loaders { get; } = new() { "", "fabric", "quilt", "forge", "neoforge" };

    public string Query { get => _query; set => SetProperty(ref _query, value); }
    public string SelectedMcVersion { get => _selectedMcVersion; set => SetProperty(ref _selectedMcVersion, value); }
    public string SelectedLoader { get => _selectedLoader; set => SetProperty(ref _selectedLoader, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public bool Busy { get => _busy; set => SetProperty(ref _busy, value); }

    public ModpackSearchViewModel()
    {
        LoadKnownMcVersions();
    }

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
            McVersions.Add("");
            foreach (var v in versions) McVersions.Add(v);
        }
        catch { }
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(Query)) { Status = "请输入搜索关键词"; return; }

        Busy = true;
        Status = "搜索中…";
        Results.Clear();

        try
        {
            var list = await ModrinthClient.SearchModpacksAsync(Query, SelectedMcVersion, SelectedLoader, 20);
            foreach (var m in list) Results.Add(m);
            Status = $"找到 {list.Count} 个整合包";
        }
        catch (Exception e)
        {
            Status = "搜索失败: " + e.Message;
        }
        finally { Busy = false; }
    }

    /// <summary>导入整合包（下载 .mrpack 并调用 ModpackService.ImportAsync）。</summary>
    [RelayCommand]
    private async Task ImportAsync(ModrinthItem? item)
    {
        if (item == null) return;

        Busy = true;
        Status = $"正在获取「{item.Title}」下载链接…";

        try
        {
            var file = await ModrinthClient.GetLatestFileAsync(item.ProjectId, SelectedMcVersion, SelectedLoader, ".mrpack");
            Status = $"正在下载「{file.FileName}」…";

            var tempDir = Path.Combine(Path.GetTempPath(), "NovaLauncher_ModpackImport");
            Directory.CreateDirectory(tempDir);
            var destMrpack = Path.Combine(tempDir, file.FileName);

            await ModrinthClient.DownloadAsync(file.Url, destMrpack, p => Status = p);

            Status = "正在导入整合包（解压、下载模组、配置加载器）…";
            var (ok, msg) = await ModpackService.S.ImportAsync(destMrpack, new Progress<string>(p => Status = p));

            Status = ok ? $"✅ {msg}" : "导入失败: " + msg;
            if (ok)
            {
                // 导入成功后可选：清理临时文件
                try { File.Delete(destMrpack); } catch { }
            }
        }
        catch (Exception e)
        {
            Status = "导入失败: " + e.Message;
        }
        finally { Busy = false; }
    }

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
                if (vx != vy) return vy.CompareTo(vx);
            }
            return 0;
        }
    }
}