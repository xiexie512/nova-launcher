using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaLauncher.Models;
using NovaLauncher.Services;

namespace NovaLauncher.ViewModels;

/// <summary>
/// 搜索结果项包装器：增加选择状态，支持批量操作。
/// </summary>
public sealed partial class ModSearchResultItem : ObservableObject
{
    public ModrinthItem Item { get; }

    [ObservableProperty]
    private bool _isSelected;

    public ModSearchResultItem(ModrinthItem item) => Item = item;

    // 代理常用属性，方便 XAML 绑定
    public string Title => Item.Title;
    public string Description => Item.Description;
    public string DownloadsText => Item.DownloadsText;
    public string GameVersion => Item.GameVersion;
    public string Loader => Item.Loader;
    public List<string> Categories => Item.Categories;
    public string ProjectType => Item.ProjectType;
    public string ProjectId => Item.ProjectId;
    public string Slug => Item.Slug;
}

/// <summary>
/// 模组搜索与安装：搜索 Modrinth、按版本/加载器筛选、单装/批量安装到选定版本的 mods 目录。
/// </summary>
public sealed partial class ModSearchViewModel : ObservableObject
{
    private string _query = "";
    private string _selectedMcVersion = "";
    private string _selectedLoader = "";
    private string _selectedVersionId = "";
    private string _status = "";
    private bool _busy;

    public ObservableCollection<ModSearchResultItem> Results { get; } = new();
    public ObservableCollection<string> InstalledVersions { get; } = new();
    public ObservableCollection<string> McVersions { get; } = new();
    public ObservableCollection<string> Loaders { get; } = new() { "", "fabric", "quilt", "forge", "neoforge" };

    public string Query { get => _query; set => SetProperty(ref _query, value); }
    public string SelectedMcVersion { get => _selectedMcVersion; set => SetProperty(ref _selectedMcVersion, value); }
    public string SelectedLoader { get => _selectedLoader; set => SetProperty(ref _selectedLoader, value); }
    public string SelectedVersionId { get => _selectedVersionId; set => SetProperty(ref _selectedVersionId, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public bool Busy { get => _busy; set => SetProperty(ref _busy, value); }

    /// <summary>当前有多少项被选中。</summary>
    public int SelectedCount => Results.Count(r => r.IsSelected);

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
            McVersions.Add("");
            foreach (var v in versions) McVersions.Add(v);
        }
        catch { }
    }

    /// <summary>搜索模组。</summary>
    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(Query)) { Status = "请输入搜索关键词"; return; }
        if (string.IsNullOrEmpty(SelectedVersionId)) { Status = "请先选择一个已安装版本作为安装目标"; return; }

        Busy = true;
        Status = "搜索中...";
        Results.Clear();

        try
        {
            var list = await ModrinthClient.SearchModsAsync(Query, SelectedMcVersion, SelectedLoader, 30);
            foreach (var m in list)
            {
                var item = new ModSearchResultItem(m);
                item.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ModSearchResultItem.IsSelected))
                        OnPropertyChanged(nameof(SelectedCount));
                };
                Results.Add(item);
            }
            Status = $"找到 {list.Count} 个模组";
        }
        catch (Exception e)
        {
            Status = "搜索失败: " + e.Message;
        }
        finally { Busy = false; }
    }

    /// <summary>全选 / 取消全选。</summary>
    [RelayCommand]
    private void ToggleSelectAll()
    {
        var allSelected = Results.All(r => r.IsSelected);
        foreach (var r in Results) r.IsSelected = !allSelected;
        OnPropertyChanged(nameof(SelectedCount));
    }

    /// <summary>清空选择。</summary>
    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var r in Results) r.IsSelected = false;
        OnPropertyChanged(nameof(SelectedCount));
    }

    /// <summary>安装单个模组（兼容旧用法：从结果列表直接点安装按钮，自动获取最新版本）。</summary>
    [RelayCommand]
    private async Task InstallAsync(ModSearchResultItem? item)
    {
        if (item == null) return;
        await InstallSingleAsync(item.Item);
    }

    /// <summary>打开版本选择对话框（手动选择特定版本安装）。</summary>
    [RelayCommand]
    private void OpenVersionDialog(ModSearchResultItem? item)
    {
        if (item == null) return;
        var dialogVm = new ModVersionDialogViewModel(this, item.Item);
        var dialog = new Views.ModVersionDialog { DataContext = dialogVm };
        dialogVm.View = dialog;
        dialog.ShowDialog(App.Current.Windows.OfType<Window>().FirstOrDefault());
    }

    /// <summary>批量安装选中的模组。</summary>
    [RelayCommand]
    private async Task BatchInstallAsync()
    {
        var selected = Results.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0) { Status = "请先勾选要安装的模组"; return; }
        if (string.IsNullOrEmpty(SelectedVersionId)) { Status = "请选择安装目标版本"; return; }

        Busy = true;
        int ok = 0, fail = 0, skip = 0;

        foreach (var r in selected)
        {
            try
            {
                var result = await InstallSingleAsync(r.Item, silent: true);
                if (result == InstallResult.Ok) ok++;
                else if (result == InstallResult.Skip) skip++;
                else fail++;
            }
            catch { fail++; }
            Status = $"批量安装中... 成功 {ok} / 跳过 {skip} / 失败 {fail}";
        }

        Status = $"批量完成：成功 {ok}，跳过 {skip}，失败 {fail}";
        ClearSelectionCommand.Execute(null);
        Busy = false;
    }

    private enum InstallResult { Ok, Skip, Fail }

    /// <summary>安装单个模组核心逻辑。</summary>
    private async Task<InstallResult> InstallSingleAsync(ModrinthItem item, bool silent = false)
    {
        if (string.IsNullOrEmpty(SelectedVersionId)) return InstallResult.Fail;

        var modsDir = Path.Combine(AppPaths.GameDir(SelectedVersionId), "mods");
        Directory.CreateDirectory(modsDir);
        var dest = Path.Combine(modsDir, item.Slug + ".jar");

        if (File.Exists(dest))
        {
            if (!silent) Status = $"{item.Title} 已存在，跳过下载";
            return InstallResult.Skip;
        }

        if (!silent) Status = $"正在获取 {item.Title} 下载链接...";

        try
        {
            var file = await ModrinthClient.GetLatestFileAsync(item.ProjectId, SelectedMcVersion, SelectedLoader, ".jar");
            if (!silent) Status = $"正在下载 {file.FileName}...";
            await ModrinthClient.DownloadAsync(file.Url, dest, p => { if (!silent) Status = p; });
            if (!silent) Status = $"{item.Title} 安装完成 -> {SelectedVersionId}/mods/";
            return InstallResult.Ok;
        }
        catch (Exception e)
        {
            if (!silent) Status = "安装失败: " + e.Message;
            return InstallResult.Fail;
        }
    }

    /// <summary>安装指定版本（从版本历史选择对话框调用）。</summary>
    public async Task InstallSpecificVersionAsync(ModrinthItem item, ModrinthVersion version)
    {
        if (string.IsNullOrEmpty(SelectedVersionId)) { Status = "请选择安装目标版本"; return; }

        var modsDir = Path.Combine(AppPaths.GameDir(SelectedVersionId), "mods");
        Directory.CreateDirectory(modsDir);
        var dest = Path.Combine(modsDir, version.FileName);

        if (File.Exists(dest))
        {
            Status = $"{version.FileName} 已存在，跳过下载";
            return;
        }

        Busy = true;
        Status = $"正在下载 {version.FileName}...";

        try
        {
            await ModrinthClient.DownloadAsync(version.Url, dest, p => Status = p);
            Status = $"{item.Title} ({version.VersionNumber}) 安装完成 -> {SelectedVersionId}/mods/";
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