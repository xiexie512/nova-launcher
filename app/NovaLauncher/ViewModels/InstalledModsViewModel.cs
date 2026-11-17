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
/// 已安装模组项。
/// </summary>
public sealed partial class InstalledModItem : ObservableObject
{
    public string FileName { get; }
    public string FilePath { get; }
    public long FileSize { get; }
    public DateTime LastModified { get; }
    public string VersionId { get; } // 所属版本ID

    [ObservableProperty]
    private bool _isEnabled = true;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _updateStatus = ""; // "", "checking", "update_available", "latest", "error"

    public InstalledModItem(string fileName, string filePath, long fileSize, DateTime lastModified, string versionId)
    {
        FileName = fileName;
        FilePath = filePath;
        FileSize = fileSize;
        LastModified = lastModified;
        VersionId = versionId;
    }

    public string SizeText => FileSize >= 1_000_000 ? $"{FileSize / 1_000_000.0:F1} MB" : $"{FileSize / 1024.0:F0} KB";
    public string StatusIcon => UpdateStatus switch
    {
        "update_available" => "🔄",
        "latest" => "✅",
        "checking" => "⏳",
        "error" => "❌",
        _ => ""
    };
}

/// <summary>
/// 已安装模组管理：列出所有版本的 mods、启用/禁用、检查更新、删除。
/// </summary>
public sealed partial class InstalledModsViewModel : ObservableObject
{
    private string _selectedVersionId = "";
    private string _status = "";
    private bool _busy;
    private string _filterText = "";

    public ObservableCollection<string> VersionList { get; } = new();
    public ObservableCollection<InstalledModItem> Mods { get; } = new();

    public string SelectedVersionId
    {
        get => _selectedVersionId;
        set
        {
            if (SetProperty(ref _selectedVersionId, value))
                RefreshMods();
        }
    }

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value))
                OnPropertyChanged(nameof(FilteredMods));
        }
    }

    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public bool Busy { get => _busy; set => SetProperty(ref _busy, value); }

    /// <summary>按过滤文本筛选的模组列表。</summary>
    public IEnumerable<InstalledModItem> FilteredMods =>
        string.IsNullOrWhiteSpace(FilterText)
            ? Mods
            : Mods.Where(m => m.FileName.Contains(FilterText, StringComparison.OrdinalIgnoreCase));

    public int TotalCount => Mods.Count;
    public int EnabledCount => Mods.Count(m => m.IsEnabled);
    public int SelectedCount => Mods.Count(m => m.IsSelected);

    public InstalledModsViewModel()
    {
        RefreshVersionList();
        if (VersionList.Count > 0) SelectedVersionId = VersionList[0];
    }

    /// <summary>刷新版本列表。</summary>
    [RelayCommand]
    public void RefreshVersionList()
    {
        VersionList.Clear();
        foreach (var info in LoaderService.ScanInstalled())
            VersionList.Add(info.Id);
        OnPropertyChanged(nameof(VersionList));
    }

    /// <summary>刷新当前版本的模组列表。</summary>
    private void RefreshMods()
    {
        Mods.Clear();
        if (string.IsNullOrEmpty(SelectedVersionId)) return;

        var modsDir = Path.Combine(AppPaths.GameDir(SelectedVersionId), "mods");
        if (!Directory.Exists(modsDir)) return;

        foreach (var file in Directory.GetFiles(modsDir, "*.jar"))
        {
            var fi = new FileInfo(file);
            var disabledFile = file + ".disabled";
            var isEnabled = !File.Exists(disabledFile);

            Mods.Add(new InstalledModItem(
                Path.GetFileName(file),
                file,
                fi.Length,
                fi.LastWriteTime,
                SelectedVersionId
            ) { IsEnabled = isEnabled });
        }
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(EnabledCount));
    }

    /// <summary>启用/禁用模组（通过重命名 .disabled 后缀）。</summary>
    [RelayCommand]
    private void ToggleEnabled(InstalledModItem? item)
    {
        if (item == null) return;

        var disabledPath = item.FilePath + ".disabled";
        try
        {
            if (item.IsEnabled)
            {
                File.Move(item.FilePath, disabledPath);
                item.IsEnabled = false;
                Status = $"已禁用: {item.FileName}";
            }
            else
            {
                File.Move(disabledPath, item.FilePath);
                item.IsEnabled = true;
                Status = $"已启用: {item.FileName}";
            }
            OnPropertyChanged(nameof(EnabledCount));
        }
        catch (Exception e)
        {
            Status = "操作失败: " + e.Message;
        }
    }

    /// <summary>删除模组文件。</summary>
    [RelayCommand]
    private void DeleteMod(InstalledModItem? item)
    {
        if (item == null) return;

        try
        {
            var paths = new[] { item.FilePath, item.FilePath + ".disabled" };
            foreach (var p in paths)
                if (File.Exists(p)) File.Delete(p);

            Mods.Remove(item);
            Status = $"已删除: {item.FileName}";
            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(EnabledCount));
        }
        catch (Exception e)
        {
            Status = "删除失败: " + e.Message;
        }
    }

    /// <summary>批量删除选中的模组。</summary>
    [RelayCommand]
    private void BatchDelete()
    {
        var selected = Mods.Where(m => m.IsSelected).ToList();
        if (selected.Count == 0) { Status = "请先勾选要删除的模组"; return; }

        int ok = 0, fail = 0;
        foreach (var item in selected)
        {
            try
            {
                var paths = new[] { item.FilePath, item.FilePath + ".disabled" };
                foreach (var p in paths)
                    if (File.Exists(p)) File.Delete(p);
                Mods.Remove(item);
                ok++;
            }
            catch { fail++; }
        }
        Status = $"批量删除完成：成功 {ok}，失败 {fail}";
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(EnabledCount));
    }

    /// <summary>检查选中模组的更新（查询 Modrinth API）。</summary>
    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        var selected = Mods.Where(m => m.IsSelected && m.IsEnabled).ToList();
        if (selected.Count == 0) { Status = "请先勾选要检查更新的模组"; return; }

        Busy = true;
        int ok = 0, update = 0, fail = 0;

        foreach (var item in selected)
        {
            item.UpdateStatus = "checking";
            try
            {
                // 从文件名尝试解析 slug（去掉版本号后缀）
                var slug = ParseSlugFromFileName(item.FileName);
                if (string.IsNullOrEmpty(slug))
                {
                    item.UpdateStatus = "error";
                    fail++;
                    continue;
                }

                // 搜索项目
                var results = await ModrinthClient.SearchModsAsync(slug, "", "", 5);
                var project = results.FirstOrDefault(r => r.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase));
                if (project == null)
                {
                    item.UpdateStatus = "error";
                    fail++;
                    continue;
                }

                // 获取最新版本
                var latest = await ModrinthClient.GetLatestFileAsync(project.ProjectId, "", "", ".jar");
                // 简单比对：文件名是否不同
                if (!latest.FileName.Equals(item.FileName, StringComparison.OrdinalIgnoreCase))
                {
                    item.UpdateStatus = "update_available";
                    update++;
                }
                else
                {
                    item.UpdateStatus = "latest";
                }
                ok++;
            }
            catch
            {
                item.UpdateStatus = "error";
                fail++;
            }
        }

        Status = $"更新检查完成：最新 {ok - update}，有更新 {update}，失败 {fail}";
        Busy = false;
    }

    /// <summary>打开当前版本的 mods 目录。</summary>
    [RelayCommand]
    private void OpenModsDir()
    {
        if (string.IsNullOrEmpty(SelectedVersionId)) return;
        var dir = Path.Combine(AppPaths.GameDir(SelectedVersionId), "mods");
        Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }

    /// <summary>全选/取消全选。</summary>
    [RelayCommand]
    private void ToggleSelectAll()
    {
        var allSelected = Mods.All(m => m.IsSelected);
        foreach (var m in Mods) m.IsSelected = !allSelected;
        OnPropertyChanged(nameof(SelectedCount));
    }

    /// <summary>清空选择。</summary>
    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var m in Mods) m.IsSelected = false;
        OnPropertyChanged(nameof(SelectedCount));
    }

    /// <summary>从文件名解析 slug（简单启发式：去掉版本号部分）。</summary>
    private static string ParseSlugFromFileName(string fileName)
    {
        // 去掉 .jar
        var name = fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^4] : fileName;

        // 尝试按常见分隔符分割，取前段作为 slug
        // 格式通常为: modname-1.2.3.jar 或 modname-1.2.3-forge.jar
        var parts = name.Split('-', '_');
        if (parts.Length >= 2)
        {
            // 最后一段如果是版本号格式，则取前面的部分
            var last = parts[^1];
            if (System.Text.RegularExpressions.Regex.IsMatch(last, @"^\d+(\.\d+)+"))
            {
                return string.Join("-", parts.Take(parts.Length - 1));
            }
        }
        return name;
    }

    partial void OnIsSelectedChanged(InstalledModItem item, bool oldValue, bool newValue)
    {
        OnPropertyChanged(nameof(SelectedCount));
    }
}